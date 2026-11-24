using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Solver;

namespace JarSystem.Test;

/// <summary>
/// **独立参考求解器**（差分测试用）—— 刻意**不用**生产实现的那套剪枝：
/// <list type="bullet">
/// <item>遍历方式：**BFS**（生产是递归 DFS）；</item>
/// <item>状态键：**精确/逐下标**（罐子按顺序逐位比较，不做多重集归一、不做同构合并）；</item>
/// <item>记忆语义：只记"已访问"（生产记"已证无解"）；</item>
/// <item>不做"同构罐子只留一个代表"的支配剪枝；</item>
/// <item>不设工作预算（只在自身安全上限处放弃，并如实标记为未判定）。</item>
/// </list>
///
/// 因此它是一个**完整搜索**：若生产实现因为剪枝漏掉真解，这里会找到并报出反例。
/// 操作集合、推进规则（POUR_OUT 只有在"倒空且形状等于当前空罐需求"时才推进下标）、
/// 转移预算的判定，都与生产实现保持一致 —— 差分才有意义。
/// </summary>
internal sealed class ReferenceSolver : ISolver
{
    /// <summary>安全上限：超过就如实标记未判定（差分时按"无法比较"计数，不算作不一致）。</summary>
    private const int MaxStates = 400_000;

    public SolverResult Solve(List<Requirement> requirements, WorkshopInfo workshopInfo,
        List<JarState> jars, Dictionary<string, int> liquids, int transferBudget = int.MaxValue)
    {
        var nodes = new List<Node>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<int>();

        var start = new Node(
            jars.Select(j => JarState.Copy(j)).ToList(),
            new Dictionary<string, int>(liquids),
            transferBudget,
            used: 0,
            reqIndex: 0,
            parent: -1,
            op: null);
        nodes.Add(start);
        visited.Add(start.Key());
        queue.Enqueue(0);

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            var state = nodes[index];

            if (state.ReqIndex >= requirements.Count)
                return new SolverResult
                {
                    Feasible = true,
                    Path = BuildPath(nodes, index, requirements),
                    // 契约要求：可行时必须给出最终世界状态（B 类的划分搜索要用）
                    FinalJars = state.Jars.Select(j => JarState.Copy(j)).ToList(),
                    FinalLiquids = new Dictionary<string, int>(state.Liquids)
                };

            foreach (var op in GenerateOps(requirements[state.ReqIndex], state, workshopInfo))
            {
                var next = state.Clone();
                if (!Apply(op, next, workshopInfo)) continue;
                next.ReqIndex = AdvanceIndex(state, requirements, op);
                next.Parent = index;
                next.Op = op;

                var key = next.Key();
                if (!visited.Add(key)) continue;
                if (nodes.Count >= MaxStates)
                    return new SolverResult { Feasible = false, BudgetExceeded = true };

                nodes.Add(next);
                queue.Enqueue(nodes.Count - 1);
            }
        }

        return new SolverResult { Feasible = false };
    }

    // ============================================================
    // 规则（与生产实现同语义，写法刻意不同：这里全部显式展开）
    // ============================================================

    private static IEnumerable<SolverOperation> GenerateOps(Requirement req, Node s, WorkshopInfo info)
    {
        var cap = info.Capability;

        if (req.Liquid != null)
        {
            // 满罐需求：现成满罐，或"同形空罐 + 倒入液体"
            int match = s.Jars.FindIndex(j => j.Shape == req.Shape && j.Liquid == req.Liquid && !j.IsMarked && cap.CanOperateJar(j.Polarity));
            if (match >= 0)
                yield return new SolverOperation { Type = "MATCH", JarIndex = match, Consumed = new() };

            if (!cap.IsBanned(req.Liquid) && cap.IsBaseMarked(req.Shape))
            {
                int empty = s.Jars.FindIndex(j => j.Shape == req.Shape && j.Liquid == null && !j.IsMarked);
                if (empty >= 0 && s.Liquids.GetValueOrDefault(req.Liquid) >= 1)
                    yield return new SolverOperation
                    {
                        Type = "POUR_IN", JarIndex = empty, TargetLiquid = req.Liquid,
                        Consumed = new() { [req.Liquid] = 1 }
                    };
            }
        }
        else
        {
            // 空罐需求：现成空罐 / 空罐改形（消耗任一可用成本液体）/ 满罐自耗改形
            int match = s.Jars.FindIndex(j => j.Shape == req.Shape && j.Liquid == null && !j.IsMarked && cap.CanOperateJar(j.Polarity));
            if (match >= 0)
                yield return new SolverOperation { Type = "MATCH", JarIndex = match, Consumed = new() };

            foreach (var cost in Constants.LIQUIDS)
            {
                if (cap.IsBanned(cost) || s.Liquids.GetValueOrDefault(cost) < 1) continue;
                for (int i = 0; i < s.Jars.Count; i++)
                    if (s.Jars[i].Liquid == null && !s.Jars[i].IsMarked && cap.CanOperateJar(JarPolarity.Neutral) && cap.CanTransform(s.Jars[i].Shape, req.Shape))
                        yield return new SolverOperation
                        {
                            Type = "RESHAPE", JarIndex = i, TargetShape = req.Shape, CostLiquid = cost,
                            Consumed = new() { [cost] = 1 }
                        };
            }

            // 满罐改形消耗 Jar 自有内部液体，不经过 staging；Ban 不适用于这条自耗路径。
            for (int i = 0; i < s.Jars.Count; i++)
                if (s.Jars[i].Liquid != null && !s.Jars[i].IsMarked
                    && cap.CanOperateJar(JarPolarityResolver.Resolve(s.Jars[i].Liquid))
                    && cap.CanTransform(s.Jars[i].Shape, req.Shape))
                    yield return new SolverOperation
                    {
                        Type = "RESHAPE", JarIndex = i, TargetShape = req.Shape, CostLiquid = s.Jars[i].Liquid,
                        FullReshape = true, Consumed = new()
                    };
        }

        // 倒出：任何"未禁液体的满罐 + 液体仓库还有空间"（不推进需求，除非正好倒空当前空罐需求的形状）
        for (int i = 0; i < s.Jars.Count; i++)
        {
            var liquid = s.Jars[i].Liquid;
            if (liquid == null || s.Jars[i].IsMarked || !cap.CanOperateJar(JarPolarityResolver.Resolve(liquid)) || cap.IsBanned(liquid)) continue;
            if (s.Liquids.GetValueOrDefault(liquid) >= info.MaxLiquids) continue;
            yield return new SolverOperation { Type = "POUR_OUT", JarIndex = i, Consumed = new() };
        }
    }

    private static bool Apply(SolverOperation op, Node s, WorkshopInfo info)
    {
        int i = op.JarIndex!.Value;
        if (i < 0 || i >= s.Jars.Count) return false;
        if (s.Jars[i].IsMarked || !info.Capability.CanOperateJar(JarPolarityResolver.Resolve(s.Jars[i].Liquid))) return false;
        // 选择性转移预算：主台来源罐子在预算用尽后不可用
        if (s.Jars[i].MasterOrigin && s.Used >= s.Budget) return false;

        foreach (var kv in op.Consumed ?? new Dictionary<string, int>())
            if (s.Liquids.GetValueOrDefault(kv.Key) < kv.Value) return false;

        switch (op.Type)
        {
            case "MATCH":
                s.Jars[i].IsMarked = true;
                break;
            case "POUR_IN":
                if (s.Jars[i].Liquid != null) return false;
                s.Liquids[op.TargetLiquid!] = s.Liquids.GetValueOrDefault(op.TargetLiquid!) - 1;
                s.Jars[i].Liquid = op.TargetLiquid;
                s.Jars[i].IsMarked = true;
                break;
            case "RESHAPE":
                if (s.Jars[i].Liquid == null)
                {
                    if (op.CostLiquid == null || s.Liquids.GetValueOrDefault(op.CostLiquid) < 1) return false;
                    s.Liquids[op.CostLiquid] = s.Liquids.GetValueOrDefault(op.CostLiquid) - 1;
                }
                else
                {
                    // 资源从 Jar 内部扣除；但该操作仍受工作台-Jar 极性兼容权限约束。
                    s.Jars[i].Liquid = null;
                }
                s.Jars[i].Shape = op.TargetShape!;
                s.Jars[i].IsMarked = true;
                break;
            case "POUR_OUT":
                if (s.Jars[i].Liquid == null) return false;
                var liquid = s.Jars[i].Liquid!;
                s.Jars[i].Liquid = null;
                s.Jars[i].IsMarked = true;
                s.Liquids[liquid] = s.Liquids.GetValueOrDefault(liquid) + 1;
                break;
            default:
                return false;
        }

        if (s.Jars[i].MasterOrigin) s.Used++;
        return true;
    }

    /// <summary>
    /// 推进规则与生产实现一致：除了 POUR_OUT，其余操作都视为满足了当前需求（它们本就是为该需求生成的）；
    /// POUR_OUT **只有在"被倒空的那个罐子形状正好等于当前空罐需求的形状"时**才算满足该需求，
    /// 否则下标不动（继续为同一个需求找办法）。判定必须用**操作前**的状态。
    /// </summary>
    private static int AdvanceIndex(Node before, List<Requirement> requirements, SolverOperation op)
    {
        if (op.Type != "POUR_OUT") return before.ReqIndex + 1;
        if (op.JarIndex is not int i || i < 0 || i >= before.Jars.Count) return before.ReqIndex;

        var req = requirements[before.ReqIndex];
        bool shapeMatches = before.Jars[i].Shape == req.Shape;
        bool isEmptyJarRequirement = req.Liquid == null;
        return shapeMatches && isEmptyJarRequirement ? before.ReqIndex + 1 : before.ReqIndex;
    }

    private static List<SolverStep> BuildPath(List<Node> nodes, int index, List<Requirement> requirements)
    {
        // 先沿父指针收集"节点下标链"（不含起点），再自底向上装配步骤
        var chain = new List<int>();
        for (int i = index; i > 0; i = nodes[i].Parent) chain.Add(i);
        chain.Reverse();

        var steps = new List<SolverStep>();
        for (int k = 0; k < chain.Count; k++)
        {
            var after = nodes[chain[k]];
            var before = nodes[after.Parent];
            steps.Add(new SolverStep
            {
                Step = k,
                // 本步服务的是"操作前"还停在下标上的那个需求
                Requirement = before.ReqIndex < requirements.Count ? requirements[before.ReqIndex] : null,
                Operation = after.Op,
                // 快照同样是契约的一部分（PartitionSearch 的兼容路径会读它；生产实现也填）
                Before = before.Jars.Select(j => JarState.Copy(j)).ToList(),
                After = after.Jars.Select(j => JarState.Copy(j)).ToList(),
                AfterLiquids = new Dictionary<string, int>(after.Liquids)
            });
        }
        return steps;
    }

    private sealed class Node
    {
        public List<JarState> Jars { get; }
        public Dictionary<string, int> Liquids { get; }
        public int Budget { get; }
        public int Used { get; set; }
        public int ReqIndex { get; set; }
        public int Parent { get; set; }
        public SolverOperation? Op { get; set; }

        public Node(List<JarState> jars, Dictionary<string, int> liquids, int budget, int used, int reqIndex, int parent, SolverOperation? op)
        {
            Jars = jars;
            Liquids = liquids;
            Budget = budget;
            Used = used;
            ReqIndex = reqIndex;
            Parent = parent;
            Op = op;
        }

        public Node Clone() => new(
            Jars.Select(j => JarState.Copy(j)).ToList(),
            new Dictionary<string, int>(Liquids),
            Budget, Used, ReqIndex, -1, null);

        /// <summary>**精确键**：罐子逐下标比较（不做排序/归一），因此比生产键更细 ⇒ 不会漏状态。</summary>
        public string Key()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(ReqIndex).Append('/').Append(Used).Append('|');
            foreach (var j in Jars)
                sb.Append(j.Shape).Append('\u0001').Append(j.Liquid ?? "\u0000")
                  .Append(j.IsMarked ? '1' : '0').Append(j.MasterOrigin ? '1' : '0').Append('\u0002');
            sb.Append('|');
            foreach (var kv in Liquids.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\u0002');
            return sb.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Solver;

/// <summary>
/// 求解器的**工作态**（罐子快照 + 液体账目 + 规则视图 + 转移预算）。
/// **internal**：它只在本文件内使用，不出现在任何公开签名里 ⇒ 不对外暴露可变工作态。
/// </summary>
internal class SolverState
{
    public List<JarState> Jars { get; set; }
    public Dictionary<string, int> Liquids { get; set; }
    public WorkshopInfo WorkshopInfo { get; set; }
    // 选择性转移预算：从台阶段最多可「使用」的主台来源罐子数（未使用的主台罐子留在主台）
    public int TransferBudget { get; set; } = int.MaxValue;
    public int TransferUsed { get; set; }

    public SolverState(List<JarState> jars, Dictionary<string, int> liquids, WorkshopInfo workshopInfo)
    {
        Jars = jars.Select(j => JarState.Copy(j)).ToList();
        Liquids = new Dictionary<string, int>(liquids);
        WorkshopInfo = workshopInfo;
    }

    public SolverState Clone() => new SolverState(Jars, Liquids, WorkshopInfo)
    {
        TransferBudget = TransferBudget,
        TransferUsed = TransferUsed
    };
    public int FindJarIndex(Func<JarState, bool> pred) => Jars.FindIndex(j => pred(j));

    // ---- 液体账目 ----
    public int LiquidCount(string l) => Liquids.GetValueOrDefault(l, 0);
    public bool HasLiquid(string l, int a = 1) => LiquidCount(l) >= a;
    public bool ConsumeLiquid(string l, int a = 1) { if (!HasLiquid(l, a)) return false; Liquids[l] -= a; return true; }
    public void AddLiquid(string l, int a = 1) => Liquids[l] = Liquids.GetValueOrDefault(l, 0) + a;

    // ---- 罐子状态（求解器自有的快照态） ----
    public void MarkJar(int i) => Jars[i].IsMarked = true;
    public void SetShape(int i, string s) => Jars[i].Shape = s;
    public void SetLiquid(int i, string l) => Jars[i].Liquid = l;
    public string? TakeLiquid(int i) { var l = Jars[i].Liquid; Jars[i].Liquid = null; return l; }
    public bool IsEmpty(JarState j) => j.Liquid is null;

    // ---- 规则查询 ----
    public bool IsBanned(string l) => WorkshopInfo.Capability.IsBanned(l);
    public bool CanTransform(string f, string t) => WorkshopInfo.Capability.CanTransform(f, t);

    /// <summary>可用成本液体（未被禁 + 有库存 + 不在排除列表）。</summary>
    public List<string> AvailableCostLiquids(List<string>? exclude = null)
    {
        exclude ??= new();
        return Constants.LIQUIDS.Where(l => !IsBanned(l) && HasLiquid(l, 1) && !exclude.Contains(l)).ToList();
    }
}

/// <summary>
/// 求解器侧的工作台规则信息。
///
/// S1：能力参数（标记/禁令）改由 <see cref="Domain.WorkshopCapability"/> 承载 ——
/// 与域层 <c>Workshop</c>、快照 <c>WorkshopSnapshot</c> **共用同一份不可变对象**，
/// 不再各存一份标记表；因此"求解器的规则视图"与"领域层的规则视图"不可能漂移。
/// </summary>
public class WorkshopInfo
{
    /// <summary>能力参数（与域层同源）。</summary>
    public WorkshopCapability Capability { get; set; } = new();

    public int MaxJars { get; set; } = 20;
    public int MaxLiquids { get; set; } = 30;

    public List<string> GetPossibleTransforms(string shape) => Capability.GetPossibleTransforms(shape);
}

public class SolverStats
{
    public int NodesExplored { get; set; }
    public int SolutionsFound { get; set; }
    public int DeadEnds { get; set; }
    public int PourOuts { get; set; }

    /// <summary>被"失败状态记忆化"挡下的重复探索次数（诊断用，不参与任何对外契约）。</summary>
    public int MemoHits { get; set; }

    /// <summary>被"同构罐子剪枝"省掉的等价候选操作数（诊断用）。</summary>
    public int IsomorphicPruned { get; set; }
}

public class SolverResult
{
    public bool Feasible { get; set; }
    public List<SolverStep>? Path { get; set; }
    public string? Blockage { get; set; }
    public SolverStats? Stats { get; set; }

    /// <summary>
    /// 本次求解是否因**超出工作预算**而提前中止（见 <see cref="IWorkBudgetedSolver"/>）。
    /// true 时 <see cref="Feasible"/>=false 表示"未判定"，**不是**"不可行"。
    /// </summary>
    public bool BudgetExceeded { get; set; }

    /// <summary>
    /// 可行时的**最终世界状态：罐子**。这是**求解器契约的一部分**，不是可选诊断信息 ——
    /// B 类（<c>PartitionSearch</c>）要靠它算"主台剩下的罐子"才能构造从台求解池。
    /// 缺失时旧实现会退化成"用主台**原始**状态"，于是从台池算错、结论静默失真
    /// （差分测试实测过：只填 Path 不填快照的求解器会让 B 类算错）。
    /// </summary>
    public List<JarState>? FinalJars { get; set; }

    /// <summary>可行时的**最终世界状态：液体账目**（同 <see cref="FinalJars"/> 的契约要求）。</summary>
    public Dictionary<string, int>? FinalLiquids { get; set; }
}

/// <summary>
/// StateSpaceSolver v6 — 状态空间搜索求解器（实现 <see cref="ISolver"/>）
/// 合法操作: MATCH(预定), POUR_IN(倒液), POUR_OUT(倒空), RESHAPE(改形)
/// RESHAPE: 适用于空罐(用工作台液体)和满罐(用自身液体)
/// </summary>
public class StateSpaceSolver : ISolver, IWorkBudgetedSolver
{
    public SolverStats Stats { get; private set; } = new();
    public List<SolverStep> BestPartialPath { get; private set; } = new();

    /// <summary>
    /// 默认工作预算（搜索节点数）。取值依据：实测本机每个节点约 4~5 µs，
    /// 因此 40 万节点 ≈ 最坏 2 秒 —— 这是一次可行性分析允许占住全局闸门的上限。
    /// 典型 A 类订单（20 罐 / 10 需求）实测约 7 千节点（34 ms），远在预算之内；
    /// 只有"大需求 + 不可行"的 B 类订单会撞到它，此时如实回"未判定"而不是假装不可行。
    /// </summary>
    public const int DefaultNodeBudget = 400_000;

    private int _nodesRemaining = DefaultNodeBudget;
    private bool _explicitBudget;

    /// <inheritdoc />
    public bool BudgetExceeded { get; private set; }

    /// <inheritdoc />
    public void BeginCheck(int nodeBudget)
    {
        _explicitBudget = true;
        BudgetExceeded = false;
        _nodesRemaining = nodeBudget <= 0 ? int.MaxValue : nodeBudget;
    }

    /// <summary>
    /// 已证明"无解"的**子问题**集合 —— 搜索加速的唯一结构（见 <see cref="Bt"/> 的说明）。
    ///
    /// 键是**自描述**的（规则指纹 + 剩余需求 + 罐子多重集 + 液体账目 + 转移预算）。
    /// 注意：本类是**非线程安全**的（<see cref="Stats"/>/<see cref="BestPartialPath"/> 原本就是实例字段），
    /// 与既有约定一致 —— 宿主用 <c>JarSystemService.Gate</c> 串行化全部调用。
    /// </summary>
    private readonly HashSet<string> _failedStates = new();

    /// <summary>记忆化条目上限（安全阀：达到上限后只是不再新增剪枝信息，不影响正确性）。</summary>
    private const int MemoCap = 400_000;

    public SolverResult Solve(List<Requirement> requirements, WorkshopInfo workshopInfo,
        List<JarState> jars, Dictionary<string, int> liquids, int transferBudget = int.MaxValue)
    {
        Stats = new SolverStats();
        BestPartialPath = new();
        // 没人显式给过预算（例如直接构造求解器的实验/脚本）时，按"单次求解"给一份完整预算，
        // 免得第二次调用一开始就处于"预算耗尽"状态。
        if (!_explicitBudget) { BudgetExceeded = false; _nodesRemaining = DefaultNodeBudget; }
        if (BudgetExceeded) return new SolverResult { Feasible = false, BudgetExceeded = true, Stats = Stats };
        // **每次求解清空**：这是经过实测的取舍 —— 若跨调用复用（不清空），B 类划分搜索会把
        // 40 万条长键堆满内存、触顶整体清空、剪枝知识反复归零，实测把 C 类 8 需求从 71 秒
        // 拖成 >890 秒（负优化）。正确做法是减少**求解调用次数**（见 PartitionSearch 的单调剪枝），
        // 而不是让单次求解背着全局缓存。
        _failedStates.Clear();
        var st = new SolverState(jars, liquids, workshopInfo) { TransferBudget = transferBudget };
        var path = new List<SolverStep>();
        var r = Bt(requirements, 0, st, path);
        if (BudgetExceeded)
            return new SolverResult { Feasible = false, BudgetExceeded = true, Stats = Stats };
        return r != null
            ? new SolverResult
            {
                Feasible = true,
                Path = r,
                Stats = Stats,
                // 终态是**契约字段**（见 SolverResult.FinalJars）：B 类的划分搜索靠它算"主台剩下的资源"。
                // 空路径（零需求）时终态就是初始状态。
                FinalJars = r.Count > 0 ? r[^1].After : st.Jars.Select(j => JarState.Copy(j)).ToList(),
                FinalLiquids = r.Count > 0 ? r[^1].AfterLiquids : new Dictionary<string, int>(st.Liquids)
            }
            : new SolverResult { Feasible = false, Path = BestPartialPath, Blockage = Block(requirements, st), Stats = Stats };
    }

    private List<SolverStep>? Bt(List<Requirement> reqs, int idx, SolverState st, List<SolverStep> path)
    {
        // 工作预算（见 IWorkBudgetedSolver）：一旦耗尽就立刻整体退栈（后续所有节点都走这一行），
        // 调用方据 BudgetExceeded 判定"未判定"，**不能**当成不可行。
        if (--_nodesRemaining <= 0) { BudgetExceeded = true; return null; }

        if (path.Count > BestPartialPath.Count)
            BestPartialPath = new List<SolverStep>(path);
        // 走到叶子的路径由各层在**成功返回时**自底向上装配（见下面 foreach 的说明），
        // 因此这里返回空列表即可；path 只用于深度与 BestPartialPath 的计数。
        if (idx >= reqs.Count) { Stats.SolutionsFound++; return new List<SolverStep>(); }

        // ------------------------------------------------------------------
        // #1 搜索加速（2026-09）：**只剪枝，不改探索顺序**
        //
        // 背景（实测）：本函数原来没有任何"状态去重"，而 EmptyOps 会为「每个可用成本液体 ×
        // 每个可改形空罐」各生成一个 RESHAPE 候选 ⇒ 分支因子 ≈ 罐子数×液体数、深度 = 需求数，
        // 于是 20 罐 / 8 需求的一次求解就超过 240 秒（而默认世界每台就是 20 罐、UI 允许 10 个需求）。
        //
        // 剪枝依据（**可证明等价**）：
        //   ① 每个操作的效果只取决于罐子自身属性（形状/液体/标记/来源），与罐子下标无关，
        //      且状态内罐子顺序在一次 Solve 内恒定 ⇒ 对罐子下标做置换是同构变换；
        //   ② 因此 (需求下标 idx, 罐子描述符**多重集**, 液体账目, 转移预算) 唯一决定了子问题是否有解；
        //   ③ 若该子问题已被证明无解，换个到达顺序再来一遍仍然无解 ⇒ 直接跳过。
        //
        // 为什么**不会改变结果**：DFS 仍按原顺序生成并尝试候选，记忆化只跳过"已证明无解的子树"，
        // 所以第一条被找到的路径与改动前逐字相同（这是本项目"契约与路径不变"硬约束的前提）。
        // 唯一无法再精确复现的是 BestPartialPath（诊断用、不参与任何对外契约）。
        // ------------------------------------------------------------------
        var stateKey = StateKey(idx, st);
        if (_failedStates.Contains(stateKey))
        {
            Stats.MemoHits++;
            return null;
        }

        var ops = GenOps(reqs[idx], st);
        foreach (var op in ops)
        {
            Stats.NodesExplored++;
            // 先做**不需要克隆**的廉价否决：预算用尽后，主台来源罐子的操作必被 Apply 拒绝，
            // 没必要为它克隆一份状态。放在 Clone 之前是纯省事，不改任何语义。
            if (op.JarIndex is int ji && !WithinBudget(st, ji)) continue;
            var ns = st.Clone();
            if (!Apply(op, ns)) continue;

            // 关键性能修复（2026-09）：步骤对象**先不带快照**地入栈，只有该分支最终成功时才回填
            // Before/After 两份全量深拷贝。原实现对每个候选操作都构造一次完整快照 —— 而绝大多数
            // 分支立刻失败，那些分配全是垃圾。每个节点 2×N 个 JarState 的分配就此消失。
            var step = new SolverStep { Step = idx, Requirement = reqs[idx], Operation = op };
            path.Add(step);
            int ni = idx + 1;
            if (op.Type == "POUR_OUT")
            {
                var jBefore = st.Jars[op.JarIndex!.Value];
                // Only advance if POUR_OUT satisfies an EMPTY requirement:
                // the jar becomes empty of the matching shape.
                bool shapeMatch = jBefore.Shape == reqs[idx].Shape;
                bool isEmptyReq = reqs[idx].Liquid == null;
                ni = (shapeMatch && isEmptyReq) ? idx + 1 : idx;
            }
            var r = Bt(reqs, ni, ns, path);
            path.RemoveAt(path.Count - 1);
            if (r != null)
            {
                step.Before = st.Jars.Select(j => JarState.Copy(j)).ToList();
                step.After = ns.Jars.Select(j => JarState.Copy(j)).ToList();
                step.AfterLiquids = new Dictionary<string, int>(ns.Liquids);
                r.Insert(0, step);
                return r;
            }
        }
        Stats.DeadEnds++;
        if (_failedStates.Count < MemoCap) _failedStates.Add(stateKey);
        return null;
    }

    /// <summary>
    /// **子问题**的规范键：
    /// 剩余需求下标 + 罐子描述符**多重集**（排序后）+ 液体账目 + 转移预算/已用量。
    ///
    /// 刻意不包含罐子 Id —— 求解器的任何分支都不读 Id（Id 只用于把已找到的路径映射回真实罐子）。
    /// 需求用**下标**表示即可：一次 Solve 内 reqs 是固定的（跨调用复用已被实测否决，见 Solve 的说明）。
    /// </summary>
    private static string StateKey(int idx, SolverState st)
    {
        // 罐子：**排序**成多重集 ⇒ 罐子下标排列不同的同构状态合并为同一个键
        var jars = new List<string>(st.Jars.Count);
        foreach (var j in st.Jars) jars.Add(Descriptor(j));
        jars.Sort(StringComparer.Ordinal);

        // 液体：排序，避免依赖字典的枚举顺序
        var liquids = new List<string>(st.Liquids.Count);
        foreach (var kv in st.Liquids) liquids.Add(kv.Key + "=" + kv.Value);
        liquids.Sort(StringComparer.Ordinal);

        return idx
            + "\u0002" + st.TransferBudget + "/" + st.TransferUsed
            + "\u0002" + string.Join("\u0003", jars)
            + "\u0002" + string.Join("\u0003", liquids);
    }

    /// <summary>
    /// 罐子描述符（决定该罐子可参与的一切操作）：形状 + 液体 + 标记 + 来源，不含 Id。
    ///
    /// 曾经试过把它换成"角色投影"（形状只通过"能不能变成需求形状 / 是不是需求形状"参与搜索，
    /// 于是**无关形状**的罐子可以合并）。实现了、也测了，结论是**回退**：
    /// 「角色投影」版与形状名版在实测场景下**探索节点数一模一样（104,496）**，而耗时稳定慢约 10%
    /// （每节点多一次角色码查表 + 更长的描述符）。
    /// 原因：订单里出现过的形状**必须**互相区分 —— "我就是需求形状 k"和"我不是"是本质差别，
    /// 所以能合并的只剩"既不在需求里、reshape 掩码又相同"的罐子，而这类罐子在实测场景里很少。
    /// 教训：上一轮用"候选罐子去重率 67%"预测收益是**错的指标** —— 那只衡量了改形候选，
    /// 而真正的成本来自状态空间（罐子×液体的可达多重集），不是候选枚举。
    /// </summary>
    private static string Descriptor(JarState j) =>
        j.Shape + "\u0001" + (j.Liquid ?? "\u0000")
        + (j.IsMarked ? "\u00011" : "\u00010") + (j.MasterOrigin ? "\u00011" : "\u00010");

    /// <summary>
    /// 选择性转移预算：主台来源的罐子在预算用尽后一律不可用（与 <see cref="Apply"/> 的判据逐字一致）。
    /// 生成阶段就用它过滤，避免产出"注定被拒绝"的候选 —— 划分搜索里合并池往往有一半是主台来源罐子，
    /// 这条过滤能直接砍掉那一半分支。
    /// </summary>
    private static bool WithinBudget(SolverState st, int jarIndex) =>
        !st.Jars[jarIndex].MasterOrigin || st.TransferUsed < st.TransferBudget;

    private List<SolverOperation> GenOps(Requirement req, SolverState st)
    {
        var ops = new List<SolverOperation>();
        if (req.Liquid != null) FullOps(req.Shape, req.Liquid, st, ops);
        else EmptyOps(req.Shape, st, ops);
        // 倒出候选按**罐子描述符**去重：两个描述符相同的满罐（同形状/同液体/同标记/同来源）
        // 互为同构，倒出任一个得到的后续状态只是罐子下标置换 ⇒ 只需保留第一个代表。
        var seenPourOut = new HashSet<string>();
        for (int i = 0; i < st.Jars.Count; i++)
        {
            var j = st.Jars[i];
            if (j.Liquid != null && !j.IsMarked && !st.IsBanned(j.Liquid))
                if (st.LiquidCount(j.Liquid) < st.WorkshopInfo.MaxLiquids)
                {
                    if (!WithinBudget(st, i)) continue;                       // 预算外：Apply 必拒
                    if (!seenPourOut.Add(Descriptor(j))) { Stats.IsomorphicPruned++; continue; }
                    ops.Add(new SolverOperation { Type = "POUR_OUT", Description = "倒出 " + j.Shape + " 中的 " + j.Liquid, JarIndex = i, Consumed = new() });
                }
        }
        return ops;
    }

    private void FullOps(string shape, string liquid, SolverState st, List<SolverOperation> ops)
    {
        var di = st.FindJarIndex(j => j.Shape == shape && j.Liquid == liquid && !j.IsMarked);
        if (di != -1 && WithinBudget(st, di)) ops.Add(new SolverOperation { Type = "MATCH", Description = "直接使用 " + shape + "/" + liquid, JarIndex = di, Consumed = new() });
        if (!st.IsBanned(liquid) && st.WorkshopInfo.Capability.IsBaseMarked(shape))
        {
            var pi = st.FindJarIndex(j => j.Shape == shape && st.IsEmpty(j) && !j.IsMarked);
            if (pi != -1 && WithinBudget(st, pi)) ops.Add(new SolverOperation { Type = "POUR_IN", Description = "倒入 " + liquid + " 到 " + shape + "空罐", JarIndex = pi, TargetLiquid = liquid, Consumed = new() { [liquid] = 1 } });
        }
    }

    private void EmptyOps(string shape, SolverState st, List<SolverOperation> ops)
    {
        var di = st.FindJarIndex(j => j.Shape == shape && st.IsEmpty(j) && !j.IsMarked);
        if (di != -1 && WithinBudget(st, di)) ops.Add(new SolverOperation { Type = "MATCH", Description = "直接使用 " + shape + "空罐", JarIndex = di, Consumed = new() });
        // 空罐改形：按（成本液体 → 罐子描述符）去重，理由同上（同构罐子互为等价分支）。
        foreach (var cl in st.AvailableCostLiquids())
        {
            var seenEmpty = new HashSet<string>();
            for (int i = 0; i < st.Jars.Count; i++)
                if (st.IsEmpty(st.Jars[i]) && !st.Jars[i].IsMarked && st.CanTransform(st.Jars[i].Shape, shape))
                {
                    if (!WithinBudget(st, i)) continue;                       // 预算外：Apply 必拒
                    if (!seenEmpty.Add(Descriptor(st.Jars[i]))) { Stats.IsomorphicPruned++; continue; }
                    ops.Add(new SolverOperation { Type = "RESHAPE", Description = "改形 " + st.Jars[i].Shape + "->" + shape, JarIndex = i, TargetShape = shape, CostLiquid = cl, Consumed = new() { [cl] = 1 } });
                }
        }
        // 满罐改形同理去重。
        var seenFull = new HashSet<string>();
        for (int i = 0; i < st.Jars.Count; i++)
        {
            var j = st.Jars[i];
            if (j.Liquid != null && !j.IsMarked && !st.IsBanned(j.Liquid) && st.CanTransform(j.Shape, shape))
            {
                if (!WithinBudget(st, i)) continue;                           // 预算外：Apply 必拒
                if (!seenFull.Add(Descriptor(j))) { Stats.IsomorphicPruned++; continue; }
                ops.Add(new SolverOperation { Type = "RESHAPE", Description = "满罐改形 " + j.Shape + "->" + shape + " 自耗" + j.Liquid, JarIndex = i, TargetShape = shape, CostLiquid = j.Liquid, Consumed = new(), FullReshape = true });
            }
        }
    }

    private bool Apply(SolverOperation op, SolverState st)
    {
        int i = op.JarIndex!.Value;
        if (i < 0 || i >= st.Jars.Count) return false;
        if (st.Jars[i].IsMarked) return false;
        // 选择性转移预算：从台阶段使用主台来源罐子会消耗预算，超限则不可用
        if (st.Jars[i].MasterOrigin && st.TransferUsed >= st.TransferBudget) return false;
        foreach (var kv in op.Consumed ?? new())
            if (!st.HasLiquid(kv.Key, kv.Value)) return false;
        switch (op.Type)
        {
            case "MATCH": st.MarkJar(i); break;
            case "POUR_IN":
                if (!st.IsEmpty(st.Jars[i])) return false;
                if (op.TargetLiquid == null || !st.ConsumeLiquid(op.TargetLiquid, 1)) return false;
                st.SetLiquid(i, op.TargetLiquid); st.MarkJar(i); break;
            case "RESHAPE":
                if (st.IsEmpty(st.Jars[i])) { if (op.CostLiquid == null || !st.ConsumeLiquid(op.CostLiquid, 1)) return false; }
                else { if (st.IsBanned(st.Jars[i].Liquid!)) return false; st.TakeLiquid(i); }
                st.SetShape(i, op.TargetShape!); st.MarkJar(i); break;
            case "POUR_OUT":
                if (st.IsEmpty(st.Jars[i])) return false;
                var lq = st.TakeLiquid(i); st.MarkJar(i); st.AddLiquid(lq!, 1); Stats.PourOuts++; break;
            default: return false;
        }
        if (st.Jars[i].MasterOrigin) st.TransferUsed++;
        return true;
    }

    private string Block(List<Requirement> reqs, SolverState st)
    {
        for (int i = 0; i < reqs.Count; i++)
        {
            var ops = GenOps(reqs[i], st);
            if (ops.All(o => o.Type == "POUR_OUT"))
            {
                var d = reqs[i].Liquid != null ? "满罐(" + reqs[i].Shape + "," + reqs[i].Liquid + ")" : "空罐(" + reqs[i].Shape + ")";
                var liq = reqs[i].Liquid;
                if (liq != null && st.IsBanned(liq)) return "需求 " + d + " 液体" + liq + "被禁止";
                return "需求 " + d + " 无任何可行路径";
            }
        }
        return "资源冲突或液体不足";
    }
}

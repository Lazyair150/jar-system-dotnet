using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Solver;

namespace JarSystem.Core.Orders;

/// <summary>划分搜索的输入（一次 B 类订单可行性分析的完整快照）。</summary>
public sealed class PartitionSearchRequest
{
    /// <summary>已按执行顺序（满罐优先）排序的需求。</summary>
    public List<Requirement> Requirements { get; init; } = new();

    public WorkshopInfo MasterInfo { get; init; } = new();
    public WorkshopInfo SlaveInfo { get; init; } = new();

    public List<JarState> MasterJars { get; init; } = new();
    public Dictionary<string, int> MasterLiquids { get; init; } = new();

    public List<JarState> SlaveJars { get; init; } = new();
    public Dictionary<string, int> SlaveLiquids { get; init; } = new();
}

/// <summary>一次可行划分的产出（两阶段路径 + 选择性转移清单 + 计数）。</summary>
public sealed class PartitionSearchOutcome
{
    public List<SolverStep> MasterPath { get; init; } = new();
    public List<SolverStep> SlavePath { get; init; } = new();

    /// <summary>需要从主台转移到从台的罐子 Id（交付罐 + 从台路径实际使用的工具罐）。</summary>
    public List<int> TransferJarIds { get; init; } = new();

    /// <summary>需要从主台转移到从台的游离液体量（每种）。</summary>
    public Dictionary<string, int> TransferLiquids { get; init; } = new();

    public int DirectCount { get; init; }
    public int OperationCount { get; init; }
}

/// <summary>
/// B 类订单的「需求子集划分」精确搜索（原 <c>FeasibilityChecker.CheckTypeB</c> 的主体，整体迁出以便单独测试）。
///
/// 算法：把 N 个需求（重复需求按序号算多次）枚举为 2^N 个「主台子集 / 从台子集」划分；
/// 每个划分先在主台规则下精确求解主台子集，取其**真实终态**（罐子 + 液体账目）与从台合并，
/// 再在从台规则下精确求解从台子集；任一划分可行即订单可行。
///
/// 关键语义（不可改动）：
/// - 主台交付罐必须原样交付，从台阶段不得再使用（防同一罐子跨阶段重复使用）；
/// - 转移预算 = 从台罐位 − 从台原有罐子 − 交付罐数，超预算的划分直接判不可行；
/// - 从台液体池按从台容量截断，与执行的「容量感知部分填充」一致；
/// - 划分按 <c>mask</c> 升序枚举，失败时保留**最后一次**划分的阻塞原因。
/// </summary>
public sealed class PartitionSearch
{
    private readonly ISolver _solver;

    public PartitionSearch(ISolver solver)
    {
        _solver = solver;
    }

    /// <summary>
    /// 搜索所有划分。返回 null 表示无可行划分，此时 <paramref name="lastBlockage"/> 为最后一次划分的阻塞原因。
    /// </summary>
    /// <param name="lastBlockage">最后一次划分的阻塞原因（诊断用，进 <c>blockage</c> 契约字段）。</param>
    /// <param name="inconclusiveReason">
    /// 为 null 表示"搜完了、结论可靠"；非 null 表示**没能得出结论**（原因见字符串），
    /// 此时返回 null **绝不能解释成"不可行"**。两种已知情形：
    /// ① 超出工作预算（见 <see cref="IWorkBudgetedSolver"/>）；
    /// ② 求解器没按 <see cref="ISolver"/> 契约提供主台**终态** —— 那就无法构造从台池，
    ///    与其静默按"主台原始状态"算错，不如如实报"未判定"（差分测试实测过这个坑）。
    /// </param>
    public PartitionSearchOutcome? Search(PartitionSearchRequest request, out string? lastBlockage, out string? inconclusiveReason)
    {
        inconclusiveReason = null;
        var reqs = request.Requirements;
        var masterJars = request.MasterJars;
        var masterLiquids = request.MasterLiquids;
        var slaveJars = request.SlaveJars;
        var slaveLiquids = request.SlaveLiquids;

        string? blockage = null;

        // #1（2026-09）主台侧**单调性剪枝**：可行性对"需求集合"单调 ——
        // 若子集 X 在主台上不可行，则任何 X ⊆ Y 的 Y 在主台上也不可行（Y 必须同时满足 X 的要求）。
        // 于是把"已证不可行的主台子集"记下来，枚举 mask 时先看它是否已包含某个失败子集；
        // 这类 mask 的主台求解**必然失败**，而原实现对"主台求解失败"的分支不做任何事
        //（既不更新 blockage、也不返回）⇒ 跳过它们与逐次求解**逐值等价**，只是省掉那次昂贵的 DFS。
        //
        // 为什么这条最关键：B 类不可行订单要对全部 2^N 个划分各做一次主台+从台求解，
        // 而最贵的正是"主台拿到很多需求"的那些 mask —— 一旦某个小失败子集被证出来，
        // 它的全部超集（数量最多、单次最贵）一次跳过。
        var masterInfeasible = new bool[1 << reqs.Count];
        for (int mask = 0; mask < (1 << reqs.Count); mask++)
        {
            if (ContainsInfeasibleSubset(masterInfeasible, mask)) continue;

            var mReqs = new List<Requirement>();
            var sReqs = new List<Requirement>();
            for (int i = 0; i < reqs.Count; i++)
            {
                if ((mask >> i & 1) == 1) mReqs.Add(reqs[i]);
                else sReqs.Add(reqs[i]);
            }

            var mr = _solver.Solve(mReqs, request.MasterInfo, masterJars, masterLiquids);
            if (_solver is IWorkBudgetedSolver mb && mb.BudgetExceeded)
            {
                inconclusiveReason = "分析超出预算";
                lastBlockage = "分析超出预算";
                return null;
            }
            if (!mr.Feasible || mr.Path == null) { masterInfeasible[mask] = true; continue; }

            // 契约检查：主台终态必须能拿到（FinalJars 或路径末步快照），否则从台池无从构造
            bool hasFinalState = mr.FinalJars != null
                || (mr.Path.Count > 0 && mr.Path[^1].After != null);
            if (!hasFinalState)
            {
                inconclusiveReason = "求解器未按 ISolver 契约提供主台终态（无法构造从台求解池）";
                lastBlockage = inconclusiveReason;
                return null;
            }

            List<JarState> masterFinal = masterJars;
            Dictionary<string, int> masterFinalLiq = masterLiquids;
            // 主台**终态**优先取求解器的契约字段 FinalJars/FinalLiquids（见 SolverResult.FinalJars 的说明）；
            // 其次兼容"只填了路径快照"的实现。两者都缺时才会退回主台**原始**状态 ——
            // 那是一个会静默算错的退化路径（差分测试实测过），因此把它记进 ISolver 的契约要求。
            if (mr.FinalJars != null)
            {
                masterFinal = mr.FinalJars;
                masterFinalLiq = mr.FinalLiquids ?? masterLiquids;
            }
            else if (mr.Path.Count > 0 && mr.Path[^1].After != null)
            {
                masterFinal = mr.Path[^1].After!;
                masterFinalLiq = mr.Path[^1].AfterLiquids ?? masterLiquids;
            }

            // 主台交付罐：主台子集需求在终态中匹配的罐子，转移后必须原样交付，
            // 从台阶段不得再操作/消耗（防止同一罐子跨阶段被重复使用）。
            var deliverableIds = new HashSet<int>();
            if (mReqs.Count > 0)
            {
                var matched = JarMatcher.MatchAll(masterFinal, mReqs, JarMatcher.Matches);
                foreach (var idx in matched)
                {
                    if (idx < 0) continue;
                    // 用局部变量接住再判空：重复索引 masterFinal[idx] 会打断可空性流分析（CS8629）
                    var jarId = masterFinal[idx].Id;
                    if (jarId.HasValue) deliverableIds.Add(jarId.Value);
                }
            }

            // 选择性转移：从台求解池 = 主台终态（扣除交付罐，标记主台来源） + 从台原有罐子
            var slavePool = masterFinal
                .Where(j => !(j.Id.HasValue && deliverableIds.Contains(j.Id.Value)))
                .Select(j => JarState.Copy(j, masterOrigin: true))
                .Concat(slaveJars.Select(j => JarState.Copy(j, masterOrigin: false)))
                .ToList();

            // 转移预算：从台罐位 − 从台原有罐子 − 交付罐数（交付罐必转，不可再占预算）
            int budget = request.SlaveInfo.MaxJars - slaveJars.Count - deliverableIds.Count;
            if (budget < 0) continue;

            // 液体池：从台自身 + 主台可转移液体（按从台每种液体容量截断，与执行部分填充一致）
            var tLiquids = new Dictionary<string, int>();
            foreach (var l in Constants.LIQUIDS)
                tLiquids[l] = Math.Min(
                    masterFinalLiq.GetValueOrDefault(l, 0) + slaveLiquids.GetValueOrDefault(l, 0),
                    request.SlaveInfo.MaxLiquids);

            var sr = _solver.Solve(sReqs, request.SlaveInfo, slavePool, tLiquids, budget);
            if (_solver is IWorkBudgetedSolver sb && sb.BudgetExceeded)
            {
                inconclusiveReason = "分析超出预算";
                lastBlockage = "分析超出预算";
                return null;
            }
            if (sr.Feasible && sr.Path != null)
            {
                // 转移罐子清单：交付罐 + 从台路径实际使用的主台来源罐子
                var transferIds = new HashSet<int>(deliverableIds);
                foreach (var step in sr.Path)
                {
                    if (step.Operation?.JarIndex.HasValue == true && step.Before != null &&
                        step.Operation.JarIndex.Value >= 0 && step.Operation.JarIndex.Value < step.Before.Count)
                    {
                        var bj = step.Before[step.Operation.JarIndex.Value];
                        if (bj.MasterOrigin && bj.Id.HasValue) transferIds.Add(bj.Id.Value);
                    }
                }

                lastBlockage = blockage;
                return new PartitionSearchOutcome
                {
                    MasterPath = mr.Path,
                    SlavePath = sr.Path,
                    TransferJarIds = transferIds.OrderBy(x => x).ToList(),
                    TransferLiquids = LiquidRequirementCalculator.ComputeRequired(sr.Path, slaveLiquids),
                    DirectCount = mr.Path.Count(s => s.Operation?.Type == "MATCH")
                                + sr.Path.Count(s => s.Operation?.Type == "MATCH"),
                    OperationCount = mr.Path.Count(s => s.Operation?.Type != "MATCH")
                                   + sr.Path.Count(s => s.Operation?.Type != "MATCH")
                };
            }

            blockage = sr.Blockage ?? blockage;
        }

        lastBlockage = blockage;
        return null;
    }

    /// <summary>
    /// mask 是否包含一个"已证不可行"的主台子集（枚举其全部子集）。
    /// 需求数上限 10 ⇒ 全部 mask 加起来约 3^N = 59049 次判空，代价可忽略。
    /// </summary>
    private static bool ContainsInfeasibleSubset(bool[] infeasible, int mask)
    {
        for (int sub = mask; sub > 0; sub = (sub - 1) & mask)
            if (infeasible[sub]) return true;
        return false;
    }
}

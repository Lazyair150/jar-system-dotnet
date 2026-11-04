using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;

namespace JarSystem.Core.Orders;

/// <summary>
/// 可行性检查链的一环（阶段 3：责任链模式，替换原 <c>Check</c> 里的 if/else 硬编码分派）。
///
/// 契约：返回 null 表示「本检查不适用或未通过」，交由链上下一个检查；
/// 返回结果即终止链。链序即优先级：单阶段（A 类）→ 需求子集划分搜索（B 类）。
/// 从台在订单开始时必须为空，因此原 <c>slave_only</c> 快速路径在新模型中不可达。
/// 该字符串仍保留为旧存档/契约读取值，但不再由新分析产生。
/// </summary>
internal interface IFeasibilityCheck
{
    /// <summary>写入 <c>FeasibilityResultData.CheckMethod</c> 的取值（对外契约：single_phase/slave_only/partition）。</summary>
    string Method { get; }

    FeasibilityResultData? TryCheck(FeasibilityContext context);
}

/// <summary>A 类订单：仅主台单阶段求解（原 <c>CheckTypeA</c>）。</summary>
internal sealed class SinglePhaseCheck : IFeasibilityCheck
{
    public string Method => Constants.CheckMethod.SINGLE_PHASE;

    public FeasibilityResultData? TryCheck(FeasibilityContext ctx)
    {
        if (ctx.Order.Type != Constants.OrderType.A) return null;
        var jars = ctx.Master.Jars.Select(JarStateMapper.From).ToList();
        var liquids = ctx.Master.Liquids.ToDictionary(kv => kv.Key, kv => kv.Value);
        var solverResult = ctx.Solver.Solve(ctx.Requirements, ctx.MasterInfo, jars, liquids);

        // 2026-09-16 修复（15 位专家审查 P0）：**A 类原先漏了预算检查**。
        // IWorkBudgetedSolver 的契约写明"预算耗尽时 Feasible=false 表示**未判定**，不是不可行"，
        // B 类两处（PartitionSearch.cs:109/177）都查了，只有 A 类漏 ⇒ 会把"未判定"误报成
        // "不可行"，直接打穿"判定不可行 ⇒ 执行必失败"这条承重墙。
        // 差分门结构上看不见这个缺口（它只统计"已被置位的 Undetermined"），
        // 因此补了确定性反例 S22（test/JarSystem.Test）——它把 NodeBudget 压到 1。
        if (ctx.Solver is IWorkBudgetedSolver budgeted && budgeted.BudgetExceeded)
            return new FeasibilityResultData
            {
                Feasible = false,
                Undetermined = true,
                // 未判定的来源不止"预算用尽"一种（例：加载的临时规则本层无法表达）⇒ 取求解器给的原因
                Reason = (budgeted.InconclusiveReason ?? "分析超出预算") + "（订单 " + ctx.Requirements.Count + " 个需求）",
                Blockage = null,
                CheckMethod = Method
            };

        return FeasibilityResultFactory.FromSolverResult(solverResult, Method);
    }
}

/// <summary>B 类精确路径：需求子集划分搜索（原 partition 分支；无论成败都给出结果，是链的兜底）。</summary>
internal sealed class PartitionCheck : IFeasibilityCheck
{
    private readonly PartitionSearch _search;

    public PartitionCheck(PartitionSearch search)
    {
        _search = search;
    }

    public string Method => Constants.CheckMethod.PARTITION;

    public FeasibilityResultData? TryCheck(FeasibilityContext ctx)
    {
        if (ctx.Order.Type == Constants.OrderType.A) return null;

        var outcome = _search.Search(new PartitionSearchRequest
        {
            Requirements = ctx.Requirements,
            MasterInfo = ctx.MasterInfo,
            SlaveInfo = ctx.SlaveInfo,
            MasterJars = ctx.Master.Jars.Select(JarStateMapper.From).ToList(),
            MasterLiquids = ctx.Master.Liquids.ToDictionary(kv => kv.Key, kv => kv.Value)
        }, out var lastBlockage, out var inconclusiveReason);

        // 没能得出结论（预算耗尽 / 求解器没按契约给终态）⇒ **未判定**，不是"不可行"。
        // 必须与"真的搜完了且没找到"区分开：后者才是不可行，并被"判定不可行 ⇒ 执行必失败"守护。
        if (inconclusiveReason != null)
            return new FeasibilityResultData
            {
                Feasible = false,
                Undetermined = true,
                Reason = inconclusiveReason + "（订单 " + ctx.Requirements.Count + " 个需求）",
                Blockage = null,
                CheckMethod = Method
            };

        if (outcome == null)
            return new FeasibilityResultData
            {
                Feasible = false,
                Blockage = lastBlockage ?? "资源冲突或液体不足",
                CheckMethod = Method
            };

        return FeasibilityResultFactory.FromPartition(outcome, Method);
    }
}

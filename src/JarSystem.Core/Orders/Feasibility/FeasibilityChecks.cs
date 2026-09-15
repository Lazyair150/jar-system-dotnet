using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;

namespace JarSystem.Core.Orders;

/// <summary>
/// 可行性检查链的一环（阶段 3：责任链模式，替换原 <c>Check</c> 里的 if/else 硬编码分派）。
///
/// 契约：返回 null 表示「本检查不适用或未通过」，交由链上下一个检查；
/// 返回结果即终止链。链序即优先级：
/// 单阶段（A 类）→ 仅从台快速路径（B 类）→ 需求子集划分搜索（B 类，兜底）。
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
        var jars = ctx.Master.Jars.Select(JarState.From).ToList();
        var liquids = ctx.Master.Liquids.ToDictionary(kv => kv.Key, kv => kv.Value);
        var solverResult = ctx.Solver.Solve(ctx.Requirements, ctx.MasterInfo, jars, liquids);
        return FeasibilityResultFactory.FromSolverResult(solverResult, Method);
    }
}

/// <summary>B 类快速路径：仅用从台自身资源即可满足，无需任何主台资源进入从台（原 slave_only 分支）。</summary>
internal sealed class SlaveOnlyCheck : IFeasibilityCheck
{
    public string Method => Constants.CheckMethod.SLAVE_ONLY;

    public FeasibilityResultData? TryCheck(FeasibilityContext ctx)
    {
        if (ctx.Order.Type == Constants.OrderType.A) return null;

        var solverResult = ctx.Solver.Solve(ctx.Requirements, ctx.SlaveInfo,
            ctx.Slave.Jars.Select(JarState.From).ToList(),
            ctx.Slave.Liquids.ToDictionary(kv => kv.Key, kv => kv.Value));
        if (!solverResult.Feasible) return null;

        var result = FeasibilityResultFactory.FromSolverResult(solverResult, Method);
        // slave_only 不需要任何转移
        result.TransferJarIds = new List<int>();
        result.TransferLiquids = new Dictionary<string, int>();
        return result;
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
            MasterJars = ctx.Master.Jars.Select(JarState.From).ToList(),
            MasterLiquids = ctx.Master.Liquids.ToDictionary(kv => kv.Key, kv => kv.Value),
            SlaveJars = ctx.Slave.Jars.Select(JarState.From).ToList(),
            SlaveLiquids = ctx.Slave.Liquids.ToDictionary(kv => kv.Key, kv => kv.Value)
        }, out var lastBlockage);

        if (outcome == null)
            return new FeasibilityResultData
            {
                Feasible = false,
                Blockage = lastBlockage ?? "资源冲突或液体不足",
                CheckMethod = Method
            };

        var result = FeasibilityResultFactory.FromPath(outcome.SlavePath, Method);
        result.MasterPath = PathSummaryFormatter.Summary(outcome.MasterPath);
        result.SolverPath = PathSummaryFormatter.Summary(outcome.SlavePath);
        result.TransferJarIds = outcome.TransferJarIds;
        result.TransferLiquids = outcome.TransferLiquids;
        result.DirectCount = outcome.DirectCount;
        result.OperationCount = outcome.OperationCount;
        return result;
    }
}

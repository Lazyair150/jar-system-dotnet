using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;

namespace JarSystem.Core.Orders;

/// <summary>
/// 可行性结果的构造工厂（原 <c>FeasibilityChecker.BuildResult/BuildSuccessResult</c>）。
///
/// 把「一次求解结果或一条路径 → FeasibilityResultData」的字段拼装集中在此，
/// 保证单阶段路径与划分搜索路径的字段来源完全一致。
/// </summary>
internal static class FeasibilityResultFactory
{
    /// <summary>由一次求解结果构造（不可行时携带求解器的 blockage 与空路径）。</summary>
    public static FeasibilityResultData FromSolverResult(SolverResult solverResult, string method)
    {
        if (!solverResult.Feasible)
            return new FeasibilityResultData
            {
                Feasible = false,
                // 显式 `new List<...>`（而不是集合表达式 `[]`）：目标类型现为 IReadOnlyList，写法上不赌语法细节
                SolverPath = new List<SolverStepDto>(),
                DirectCount = 0,
                OperationCount = 0,
                Blockage = solverResult.Blockage,
                CheckMethod = method
            };

        return FromPath(solverResult.Path!, method);
    }

    /// <summary>由一条可行路径构造（可直接覆盖 masterPath / 计数，供 A 类使用）。</summary>
    public static FeasibilityResultData FromPath(List<SolverStep> path, string method,
        List<SolverStepDto>? masterPath = null,
        int? directCount = null,
        int? operationCount = null) => new()
    {
        Feasible = true,
        SolverPath = PathSummaryFormatter.Summary(path),
        MasterPath = masterPath,
        DirectCount = directCount ?? path.Count(s => s.Operation?.Type == "MATCH"),
        OperationCount = operationCount ?? path.Count(s => s.Operation?.Type != "MATCH"),
        Blockage = null,
        CheckMethod = method
    };

    /// <summary>由一次划分搜索的成功产出构造（B 类：两阶段路径 + 选择性转移清单 + 计数）。</summary>
    public static FeasibilityResultData FromPartition(PartitionSearchOutcome outcome, string method) => new()
    {
        Feasible = true,
        MasterPath = PathSummaryFormatter.Summary(outcome.MasterPath),
        SolverPath = PathSummaryFormatter.Summary(outcome.SlavePath),
        DirectCount = outcome.DirectCount,
        OperationCount = outcome.OperationCount,
        Blockage = null,
        CheckMethod = method,
        TransferJarIds = outcome.TransferJarIds,
        TransferLiquids = outcome.TransferLiquids
    };
}

using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Solver;

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
    public static FeasibilityResultData FromSolverResult(SolverResult solverResult, string method) =>
        solverResult.Feasible
            ? FromPath(solverResult.Path!, method)
            : new FeasibilityResultData
            {
                Feasible = false,
                SolverPath = [],
                DirectCount = 0,
                OperationCount = 0,
                Blockage = solverResult.Blockage,
                CheckMethod = method
            };

    /// <summary>由一条可行路径构造。</summary>
    public static FeasibilityResultData FromPath(List<SolverStep> path, string method) => new()
    {
        Feasible = true,
        DirectSatisfactions = PathSummaryFormatter.DirectMatches(path),
        OperationsNeeded = PathSummaryFormatter.Operations(path),
        SolverPath = PathSummaryFormatter.Summary(path),
        DirectCount = path.Count(s => s.Operation?.Type == "MATCH"),
        OperationCount = path.Count(s => s.Operation?.Type != "MATCH"),
        Blockage = null,
        CheckMethod = method
    };
}

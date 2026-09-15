using System.Collections.Generic;
using JarSystem.Core.Orders;
using JarSystem.Core.Solver;

namespace JarSystem.Core.Abstractions;

/// <summary>
/// 状态空间求解器契约（阶段 2：让可行性分析依赖抽象而非具体实现）。
/// 实现：<see cref="Solver.StateSpaceSolver"/>。
/// </summary>
public interface ISolver
{
    /// <summary>
    /// 在给定工作台规则与资源快照上求解有序需求序列。
    /// </summary>
    /// <param name="requirements">按执行顺序排列的需求（满罐优先）。</param>
    /// <param name="workshopInfo">工作台规则与容量。</param>
    /// <param name="jars">罐子快照（求解器不修改调用方对象）。</param>
    /// <param name="liquids">游离液体库存快照。</param>
    /// <param name="transferBudget">选择性转移预算：从台阶段最多可使用的「主台来源」罐子数。</param>
    SolverResult Solve(
        List<Requirement> requirements,
        WorkshopInfo workshopInfo,
        List<JarState> jars,
        Dictionary<string, int> liquids,
        int transferBudget = int.MaxValue);
}

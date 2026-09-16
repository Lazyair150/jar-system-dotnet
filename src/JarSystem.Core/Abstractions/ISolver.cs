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
    /// <remarks>
    /// **实现者契约（必读）**：可行时**必须**在 <see cref="SolverResult"/> 里给出最终世界状态
    /// （<see cref="SolverResult.FinalJars"/> / <see cref="SolverResult.FinalLiquids"/>，或至少把
    /// 路径最后一步的 <c>After</c> / <c>AfterLiquids</c> 填好）。B 类订单的划分搜索要靠它计算
    /// 「主台阶段结束后还剩什么」，才能构造从台的求解池；缺失时会静默退化成"用主台**原始**状态"，
    /// 结论随之失真（不是报错，是算错）—— 这是差分测试实测出来的坑，务必遵守。
    /// </remarks>
    SolverResult Solve(
        List<Requirement> requirements,
        WorkshopInfo workshopInfo,
        List<JarState> jars,
        Dictionary<string, int> liquids,
        int transferBudget = int.MaxValue);
}

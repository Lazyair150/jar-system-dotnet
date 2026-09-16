namespace JarSystem.Core.Abstractions;

/// <summary>
/// **可选**能力：带"工作预算"的求解器（<see cref="ISolver"/> 的可选扩展，不实现也不影响既有契约）。
///
/// 为什么需要它：求解器的最坏情况成本是指数级的（B 类订单要对 2^N 个需求划分各做一次搜索，
/// 单次搜索又随"池子大小"指数放大）。若不加限制，一次可行性分析可以把**全局闸门**占住数分钟 ——
/// 期间推进、所有命令、自动检测全部停摆（实测：20 罐池单次求解 10 万节点 / 0.5 秒，
/// 8 需求不可行订单 >60 秒）。
///
/// **语义边界（重要）**：预算耗尽只能得出"**未判定**"，绝不能当成"不可行" ——
/// 否则会破坏本项目最关键的不变量「判定不可行 ⇒ 执行必失败」（贪心执行器可能仍然做成）。
/// 因此调用方必须先查 <see cref="BudgetExceeded"/>，再决定如何解释求解结果。
/// </summary>
public interface IWorkBudgetedSolver
{
    /// <summary>
    /// 开始**一次完整的可行性检查**（不是一次 <see cref="ISolver.Solve"/> 调用）：
    /// 预算在整次检查内共享，因此 2^N 个划分共用同一份额度。
    /// </summary>
    /// <param name="nodeBudget">本次检查允许展开的搜索节点总数（≤0 表示不限）。</param>
    void BeginCheck(int nodeBudget);

    /// <summary>本次检查是否因超出预算而**提前中止**（true 时任何"不可行"结论都不可信）。</summary>
    bool BudgetExceeded { get; }
}

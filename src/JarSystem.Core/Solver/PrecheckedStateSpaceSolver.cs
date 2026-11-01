using System.Collections.Generic;
using JarSystem.Core.Abstractions;

namespace JarSystem.Core.Solver;

/// <summary>
/// **可替换求解器实现**：在 <see cref="StateSpaceSolver"/> 外面套一层
/// <see cref="SolverPrecheck"/>，其余行为逐字委托给内层。
///
/// 为什么做成"另一个实现"而不是改内层：
/// <list type="number">
/// <item>安装方式就是既有的 <see cref="ISolver"/> 抽象 —— 宿主构造
/// <c>new FeasibilityChecker(new PrecheckedStateSpaceSolver())</c> 即可换上，
/// 换回 <c>new FeasibilityChecker()</c> 即换下，不需要改任何调用方；</item>
/// <item>内层 <see cref="StateSpaceSolver"/> 一行不动，它的搜索、剪枝、记忆化、
/// 路径形状、<c>FinalJars</c> 契约全部保持原样 ⇒ 回滚就是把构造参数换回去；</item>
/// <item>两套实现可以**同时存在并互相对拍**（差分测试正是这么用的）。</item>
/// </list>
///
/// 行为差异仅有一处：当 <see cref="SolverPrecheck.ProveInfeasible"/> 能在多项式时间内
/// 证明无解时，直接返回"不可行"，不再进指数搜索。由于该判定只放宽不错杀，
/// 可行/不可行的**结论集合完全一致** —— 这正是 <c>SOLVER-OK (agree=…)</c> 守的东西。
/// </summary>
public sealed class PrecheckedStateSpaceSolver : ISolver, IWorkBudgetedSolver
{
    private readonly StateSpaceSolver _exact;

    /// <summary>内层精确求解器（只读暴露，便于宿主读取诊断统计）。</summary>
    public StateSpaceSolver Exact => _exact;

    /// <summary>内层求解器的诊断统计（节点数、记忆化命中、同构剪枝次数）。</summary>
    public SolverStats Stats => _exact.Stats;

    /// <summary>被前置检查直接判定为不可行、因而**省掉**的搜索次数（诊断用，不参与对外契约）。</summary>
    public int PrecheckShortCircuits { get; private set; }

    /// <summary>默认装配：内层为一枚全新的 <see cref="StateSpaceSolver"/>。</summary>
    public PrecheckedStateSpaceSolver() : this(new StateSpaceSolver()) { }

    public PrecheckedStateSpaceSolver(StateSpaceSolver exact) => _exact = exact;

    /// <inheritdoc />
    public bool BudgetExceeded => _exact.BudgetExceeded;

    /// <inheritdoc />
    public void BeginCheck(int nodeBudget) => _exact.BeginCheck(nodeBudget);

    /// <inheritdoc />
    public SolverResult Solve(
        List<Requirement> requirements,
        WorkshopInfo workshopInfo,
        List<JarState> jars,
        Dictionary<string, int> liquids,
        int transferBudget = int.MaxValue)
    {
        var reason = SolverPrecheck.ProveInfeasible(requirements, workshopInfo, jars, liquids, transferBudget);
        if (reason != null)
        {
            PrecheckShortCircuits++;
            // 注意：**不设** BudgetExceeded —— 这是"确定不可行"，不是"未判定"。
            // 两者在 FeasibilityResultData 里分别落到 Feasible=false 与 Undetermined=true，语义不可混。
            return new SolverResult
            {
                Feasible = false,
                Blockage = reason,
                Stats = new SolverStats()
            };
        }

        return _exact.Solve(requirements, workshopInfo, jars, liquids, transferBudget);
    }

    /// <summary>
    /// 进度下界（"灌注 n/m"），供宿主做显示用。见 <see cref="SolverPrecheck.LowerBound"/>：
    /// 它是下界，**不是判定**。
    /// </summary>
    public int LowerBound(
        List<Requirement> requirements,
        WorkshopInfo workshopInfo,
        List<JarState> jars,
        Dictionary<string, int> liquids) =>
        SolverPrecheck.LowerBound(requirements, workshopInfo, jars, liquids);
}

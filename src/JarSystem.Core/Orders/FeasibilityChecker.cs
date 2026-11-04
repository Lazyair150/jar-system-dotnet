using System;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>
/// 可行性分析（阶段 3 后只剩「组织检查链 + 计时」）：
/// 链上依次为单阶段（A 类）→ 需求子集划分搜索（B 类）。
/// 从台是空的临时工具位，因此旧的 <c>slave_only</c> 快路径不再参与新分析。
///
/// 协作者：<see cref="IFeasibilityCheck"/> 各实现、<see cref="FeasibilityContext"/>（共享输入）、
/// <see cref="FeasibilityResultFactory"/>（结果字段拼装）、<see cref="PathSummaryFormatter"/>（路径摘要）。
/// 求解器依赖 <see cref="ISolver"/> 抽象，可注入替身。
/// </summary>
public class FeasibilityChecker
{
    private readonly IFeasibilityCheck[] _checks;

    /// <summary>求解器（阶段 2：依赖 <see cref="ISolver"/> 抽象）。</summary>
    public ISolver Solver { get; }

    /// <summary>
    /// 一次可行性检查的工作预算（搜索节点数；≤0 表示不限）。宿主可调：
    /// 调大 ⇒ 更可能给出确定结论，但最坏延迟变长；调小 ⇒ 更快回"未判定"。
    /// 默认见 <see cref="SolverLimits.DefaultNodeBudget"/>（实测约 2 秒上限）。
    /// </summary>
    public int NodeBudget { get; set; } = SolverLimits.DefaultNodeBudget;

    /// <summary>
    /// 装配：注入求解器 + 默认检查链。
    ///
    /// <para>⚠️ **本类不再自己挑求解器**（2026-10 重构）。原先还有一个无参构造
    /// <c>: this(new PrecheckedStateSpaceSolver())</c>，于是「可行性分析」（Orders）
    /// 在编译期反过来依赖「求解器实现」（Solver）—— 那是 <c>Orders ↔ Solver</c>
    /// 这组双向依赖唯一的成环边。装配属于**组合根**的职责：现在由 <c>JarSystem</c>
    /// 决定用哪个实现，本类只认 <see cref="ISolver"/>。
    /// 默认装的是 <c>PrecheckedStateSpaceSolver</c>（前置检查 + 精确搜索），换实现改那一行即可。</para>
    /// </summary>
    public FeasibilityChecker(ISolver solver)
    {
        Solver = solver;
        _checks = new IFeasibilityCheck[]
        {
            new SinglePhaseCheck(),
            new PartitionCheck(new PartitionSearch(solver))
        };
    }

    /// <summary>
    /// 对订单做可行性分析。A 类只需主台快照；B 类需要从台快照（A 类调用方可传主台快照占位）。
    /// </summary>
    public FeasibilityResultData Check(Order order, WorkshopSnapshot masterSnap, WorkshopSnapshot slaveSnap)
    {
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var context = new FeasibilityContext(order, masterSnap, slaveSnap, Solver);

        // 一次可行性检查 = 一份共享工作预算（B 类的 2^N 个划分共用它）。
        // 预算耗尽时链上的检查返回"未判定"，不会再让整个宿主等下去（详见 IWorkBudgetedSolver）。
        if (Solver is IWorkBudgetedSolver budgeted) budgeted.BeginCheck(NodeBudget);

        FeasibilityResultData? result = null;
        foreach (var check in _checks)
        {
            result = check.TryCheck(context);
            if (result != null) break;
        }

        // 理论不可达：A 类由 SinglePhaseCheck 兜底，其余类型由 PartitionCheck 兜底
        result ??= new FeasibilityResultData { Feasible = false, Reason = "无适用的可行性检查" };

        result.SetTimestamp((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime).ToString());
        return result;
    }
}

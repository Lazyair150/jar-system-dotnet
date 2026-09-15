using System;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Solver;

namespace JarSystem.Core.Orders;

/// <summary>
/// 可行性分析（阶段 3 后只剩「组织检查链 + 计时」）：
/// 链上依次为 单阶段（A 类）→ 仅从台快速路径（B 类）→ 需求子集划分搜索（B 类兜底）。
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

    /// <summary>默认装配：真实状态空间求解器 + 默认检查链。</summary>
    public FeasibilityChecker() : this(new StateSpaceSolver()) { }

    public FeasibilityChecker(ISolver solver)
    {
        Solver = solver;
        _checks = new IFeasibilityCheck[]
        {
            new SinglePhaseCheck(),
            new SlaveOnlyCheck(),
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

        FeasibilityResultData? result = null;
        foreach (var check in _checks)
        {
            result = check.TryCheck(context);
            if (result != null) break;
        }

        // 理论不可达：A 类由 SinglePhaseCheck 兜底，其余类型由 PartitionCheck 兜底
        result ??= new FeasibilityResultData { Feasible = false, Reason = "无适用的可行性检查" };

        result.Timestamp = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startTime).ToString();
        return result;
    }
}

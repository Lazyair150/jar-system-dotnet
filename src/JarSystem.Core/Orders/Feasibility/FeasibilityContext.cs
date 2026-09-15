using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Solver;

namespace JarSystem.Core.Orders;

/// <summary>
/// 可行性检查链的共享上下文：需求排序与两个工作台的规则信息只构造一次，供链上各检查复用。
/// </summary>
internal sealed class FeasibilityContext
{
    public FeasibilityContext(Order order, WorkshopSnapshot masterSnap, WorkshopSnapshot slaveSnap, ISolver solver)
    {
        Order = order;
        Master = masterSnap;
        Slave = slaveSnap;
        Solver = solver;
        Requirements = RequirementOrdering.FullFirst(order.Requirements);
        MasterInfo = WorkshopInfoFactory.From(masterSnap);
        SlaveInfo = WorkshopInfoFactory.From(slaveSnap);
    }

    public Order Order { get; }

    /// <summary>主台快照（A 类只用它）。</summary>
    public WorkshopSnapshot Master { get; }

    /// <summary>从台快照（A 类订单无从台时由调用方以主台快照占位）。</summary>
    public WorkshopSnapshot Slave { get; }

    public ISolver Solver { get; }

    /// <summary>按执行顺序（满罐优先）排列的需求。</summary>
    public List<Requirement> Requirements { get; }

    public WorkshopInfo MasterInfo { get; }

    public WorkshopInfo SlaveInfo { get; }
}

/// <summary>工作台快照 → 求解器规则信息（原 <c>FeasibilityChecker.BuildWorkshopInfo</c>）。</summary>
internal static class WorkshopInfoFactory
{
    public static WorkshopInfo From(WorkshopSnapshot snap) => new()
    {
        BaseMarks = [.. snap.BaseMarks],
        AdditionalMarks = [.. snap.AdditionalMarks],
        Ban = [.. snap.Ban],
        MaxJars = snap.MaxJars,
        MaxLiquids = snap.MaxLiquids
    };
}

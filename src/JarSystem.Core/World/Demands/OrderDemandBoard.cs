using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// 新世界唯一的需求板，包含主动与被动两个区域。
/// 它管理订单归属、身份唯一性、被动顺序、成功后的区域处理和 CD 时间推进；不负责求解或执行。
/// </summary>
public sealed class OrderDemandBoard
{
    /// <summary>订单归属、区域顺序或 CD 就绪状态发生变化时通知外部调度/求解状态簿。</summary>
    public event Action<OrderDemandBoardChange>? Changed;

    public OrderDemandArea Passive { get; }
    public OrderDemandArea Active { get; }

    public IReadOnlyList<OrderDemand> AllDemands =>
        Passive.Demands.Concat(Active.Demands).ToList().AsReadOnly();

    public int Count => Passive.Demands.Count + Active.Demands.Count;

    public OrderDemandBoard(
        DemandAreaFulfillmentPolicy passiveFulfillmentPolicy,
        DemandAreaFulfillmentPolicy activeFulfillmentPolicy)
    {
        Passive = new OrderDemandArea(DemandAreaKind.Passive, passiveFulfillmentPolicy);
        Active = new OrderDemandArea(DemandAreaKind.Active, activeFulfillmentPolicy);
    }

    public void AddDemand(OrderDemand demand, DemandAreaKind area)
    {
        if (demand == null) throw new ArgumentNullException(nameof(demand));
        if (Find(demand.Id) != null)
            throw new InvalidOperationException($"需求 ID 已登记: {demand.Id}");
        GetArea(area).Add(demand);
        Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.Added, demand.Id, area, area));
    }

    /// <summary>从区域卸载订单不等于销毁订单对象，也不回滚已发生的世界变化。</summary>
    public bool RemoveDemand(int demandId)
    {
        var demand = Find(demandId);
        if (demand == null) return false;
        var area = AreaOf(demand);
        if (!area.Remove(demand)) return false;
        Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.Removed, demand.Id,
            area.Kind, null));
        return true;
    }

    /// <summary>
    /// 执行过程报告订单成功后，由区域策略决定是否卸载该订单。
    /// 本方法只处理区域归属，不负责验证执行结果或回滚资源。
    /// </summary>
    public DemandFulfillmentResult RecordSuccessfulFulfillment(int demandId)
    {
        var demand = Find(demandId);
        if (demand == null) return DemandFulfillmentResult.NotFound;

        var area = AreaOf(demand);
        if (area.RemoveAfterSuccessfulFulfillment)
        {
            area.Remove(demand);
            Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.Removed, demand.Id,
                area.Kind, null));
            return DemandFulfillmentResult.RemovedFromArea;
        }

        Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.FulfilledRetained, demand.Id,
            area.Kind, area.Kind));
        return DemandFulfillmentResult.RetainedInArea;
    }

    /// <summary>将需求移到另一个区域；跨区移动追加到目标区域末尾。</summary>
    public bool MoveToArea(int demandId, DemandAreaKind targetArea)
    {
        var demand = Find(demandId);
        if (demand == null) return false;

        var source = AreaOf(demand);
        var target = GetArea(targetArea);
        if (ReferenceEquals(source, target)) return true;

        // 所有可能失败的参数检查在移除来源之前完成。
        source.Remove(demand);
        target.Add(demand);
        Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.Moved, demand.Id,
            source.Kind, target.Kind));
        return true;
    }

    /// <summary>调整被动区域中的优先顺序；0 为最高优先级。</summary>
    public bool MovePassiveDemand(int demandId, int newIndex)
    {
        var demand = Passive.Demands.FirstOrDefault(x => x.Id == demandId);
        if (demand == null) return false;
        var oldIndex = Passive.Demands.ToList().IndexOf(demand);
        if (!Passive.Move(demand, newIndex)) return false;
        if (oldIndex != newIndex)
            Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.PassiveOrderChanged,
                demand.Id, DemandAreaKind.Passive, DemandAreaKind.Passive));
        return true;
    }

    public OrderDemand? Find(int demandId) =>
        Passive.Demands.Concat(Active.Demands).FirstOrDefault(x => x.Id == demandId);

    /// <summary>
    /// 推进所有已装载订单的 CD，返回本次刚结束 CD 的订单 ID。
    /// 调用方可在阶段四据此触发重新求解。
    /// </summary>
    internal IReadOnlyList<int> AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));

        var ready = new List<int>();
        foreach (var demand in Passive.Demands.Concat(Active.Demands).ToArray())
            if (demand.AdvanceLogicalTime(deltaLogicalTimeMilli))
                ready.Add(demand.Id);

        // 在枚举结束后再通知，避免订阅者在回调中增删订单破坏当前遍历。
        foreach (var demandId in ready)
        {
            var demand = Find(demandId);
            if (demand == null) continue;
            var area = AreaOf(demand);
            Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.CooldownElapsed,
                demandId, area.Kind, area.Kind));
        }
        return ready.AsReadOnly();
    }

    private OrderDemandArea AreaOf(OrderDemand demand) =>
        Passive.Demands.Contains(demand) ? Passive : Active;

    private OrderDemandArea GetArea(DemandAreaKind area) => area switch
    {
        DemandAreaKind.Passive => Passive,
        DemandAreaKind.Active => Active,
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };
}

public enum DemandFulfillmentResult
{
    NotFound,
    RemovedFromArea,
    RetainedInArea
}


/// <summary>需求板向外发布的结构变化；不携带玩法规格内容。</summary>
public sealed record OrderDemandBoardChange(
    OrderDemandBoardChangeKind Kind,
    int DemandId,
    DemandAreaKind? PreviousArea,
    DemandAreaKind? CurrentArea);

public enum OrderDemandBoardChangeKind
{
    Added,
    Removed,
    Moved,
    PassiveOrderChanged,
    CooldownElapsed,
    FulfilledRetained
}

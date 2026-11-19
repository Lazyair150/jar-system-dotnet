using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// 新世界唯一的需求板，包含主动与被动两个区域。
/// 它管理需求归属、身份唯一性、被动顺序和 CD 的逻辑时间推进；不负责求解或执行。
/// </summary>
public sealed class OrderDemandBoard
{
    public OrderDemandArea Passive { get; } = new(DemandAreaKind.Passive);
    public OrderDemandArea Active { get; } = new(DemandAreaKind.Active);

    public IReadOnlyList<OrderDemand> AllDemands =>
        Passive.Demands.Concat(Active.Demands).ToList().AsReadOnly();

    public int Count => Passive.Demands.Count + Active.Demands.Count;

    public void AddDemand(OrderDemand demand, DemandAreaKind area)
    {
        if (demand == null) throw new ArgumentNullException(nameof(demand));
        if (Find(demand.Id) != null)
            throw new InvalidOperationException($"需求 ID 已登记: {demand.Id}");
        GetArea(area).Add(demand);
    }

    /// <summary>移除需求不等于关闭需求；调用方可以按产品语义决定是否先 Close。</summary>
    public bool RemoveDemand(int demandId)
    {
        var demand = Find(demandId);
        if (demand == null) return false;
        return AreaOf(demand).Remove(demand);
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
        return true;
    }

    /// <summary>调整被动区域中的优先顺序；0 为最高优先级。</summary>
    public bool MovePassiveDemand(int demandId, int newIndex)
    {
        var demand = Passive.Demands.FirstOrDefault(x => x.Id == demandId);
        if (demand == null) return false;
        return Passive.Move(demand, newIndex);
    }

    public OrderDemand? Find(int demandId) =>
        Passive.Demands.Concat(Active.Demands).FirstOrDefault(x => x.Id == demandId);

    /// <summary>
    /// 推进所有需求的 CD，返回本次刚结束 CD 的需求 ID。
    /// 调用方可在阶段四据此触发重新求解。
    /// </summary>
    public IReadOnlyList<int> AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));

        var ready = new List<int>();
        foreach (var demand in Passive.Demands.Concat(Active.Demands))
            if (demand.AdvanceLogicalTime(deltaLogicalTimeMilli))
                ready.Add(demand.Id);
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

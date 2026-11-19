using System;
using System.Collections.Generic;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// 订单区域：管理放入其中的订单委托，并定义区域级执行与成功后保留策略。
/// 区域类型（主动/被动）与成功后策略（一次性/持续）是两个独立维度。
/// </summary>
public sealed class OrderDemandArea
{
    private readonly List<OrderDemand> _demands = new();
    private readonly IReadOnlyList<OrderDemand> _view;

    public DemandAreaKind Kind { get; }
    public DemandAreaFulfillmentPolicy FulfillmentPolicy { get; }
    public bool AutoExecute => Kind == DemandAreaKind.Passive;
    public IReadOnlyList<OrderDemand> Demands => _view;

    internal OrderDemandArea(
        DemandAreaKind kind,
        DemandAreaFulfillmentPolicy fulfillmentPolicy)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(fulfillmentPolicy))
            throw new ArgumentOutOfRangeException(nameof(fulfillmentPolicy));

        Kind = kind;
        FulfillmentPolicy = fulfillmentPolicy;
        _view = _demands.AsReadOnly();
    }

    internal void Add(OrderDemand demand) => _demands.Add(demand);
    internal bool Remove(OrderDemand demand) => _demands.Remove(demand);

    /// <summary>成功完成后，按区域策略决定是否卸载该订单。</summary>
    internal bool RemoveAfterSuccessfulFulfillment =>
        FulfillmentPolicy == DemandAreaFulfillmentPolicy.OneShot;

    internal bool Move(OrderDemand demand, int newIndex)
    {
        var oldIndex = _demands.IndexOf(demand);
        if (oldIndex < 0) return false;
        if (newIndex < 0 || newIndex >= _demands.Count)
            throw new ArgumentOutOfRangeException(nameof(newIndex));

        if (oldIndex == newIndex) return true;
        _demands.RemoveAt(oldIndex);
        _demands.Insert(newIndex, demand);
        return true;
    }
}

public enum DemandAreaKind
{
    Passive,
    Active
}

/// <summary>订单成功完成后，区域如何处理该订单。</summary>
public enum DemandAreaFulfillmentPolicy
{
    /// <summary>成功后将本次订单从区域卸载；区域仍可继续接收其他订单。</summary>
    OneShot,

    /// <summary>成功后保留本次订单，等待未来再次满足。</summary>
    Continuous
}

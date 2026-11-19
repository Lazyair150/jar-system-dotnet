using System;
using System.Collections.Generic;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// 同一类需求的区域容器。区域类型只决定后续调度策略，不改变需求规则。
/// 被动区域列表顺序就是自动执行优先顺序；主动区域不使用该优先级语义。
/// </summary>
public sealed class OrderDemandArea
{
    private readonly List<OrderDemand> _demands = new();
    private readonly IReadOnlyList<OrderDemand> _view;

    public DemandAreaKind Kind { get; }
    public bool AutoExecute => Kind == DemandAreaKind.Passive;
    public IReadOnlyList<OrderDemand> Demands => _view;

    internal OrderDemandArea(DemandAreaKind kind)
    {
        Kind = kind;
        _view = _demands.AsReadOnly();
    }

    internal void Add(OrderDemand demand) => _demands.Add(demand);
    internal bool Remove(OrderDemand demand) => _demands.Remove(demand);
    internal int IndexOf(OrderDemand demand) => _demands.IndexOf(demand);
    internal void Insert(int index, OrderDemand demand) => _demands.Insert(index, demand);

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

using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// Durable detached fact that one order execution cycle completed successfully.
/// It intentionally contains no Space/Godot types or effect implementation.
/// </summary>
public sealed record OrderCycleCompletedEvent(
    string EventId,
    string WorldInstanceId,
    int DemandId,
    string OrderDefinitionKey,
    long CycleSequence);

/// <summary>
/// In-memory domain outbox owned by a single World V2 demand board.
/// Its complete state is included in the world execution checkpoint.
/// </summary>
public sealed class OrderCycleOutbox
{
    private readonly List<OrderCycleCompletedEvent> _pending = new();

    public string WorldInstanceId { get; }
    public IReadOnlyList<OrderCycleCompletedEvent> Pending => _pending.AsReadOnly();

    public OrderCycleOutbox(string? worldInstanceId = null)
    {
        WorldInstanceId = string.IsNullOrWhiteSpace(worldInstanceId)
            ? Guid.NewGuid().ToString("N")
            : ValidateWorldInstanceId(worldInstanceId);
    }

    private OrderCycleOutbox(string worldInstanceId, IEnumerable<OrderCycleCompletedEvent> pending)
    {
        WorldInstanceId = ValidateWorldInstanceId(worldInstanceId);
        _pending.AddRange((pending ?? throw new ArgumentNullException(nameof(pending))).Select(ValidateEvent));
        if (_pending.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != _pending.Count)
            throw new ArgumentException("订单事件 Outbox 包含重复 EventId。", nameof(pending));
        if (_pending.Any(item => item.WorldInstanceId != WorldInstanceId))
            throw new ArgumentException("订单事件 Outbox 包含其他世界实例的事件。", nameof(pending));
    }

    public static OrderCycleOutbox Restore(OrderCycleOutboxSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new OrderCycleOutbox(snapshot.WorldInstanceId, snapshot.Pending);
    }

    internal OrderCycleCompletedEvent Append(int demandId, string orderDefinitionKey, long cycleSequence)
    {
        if (demandId <= 0) throw new ArgumentOutOfRangeException(nameof(demandId));
        if (string.IsNullOrWhiteSpace(orderDefinitionKey))
            throw new ArgumentException("订单定义 key 不能为空。", nameof(orderDefinitionKey));
        if (cycleSequence <= 0) throw new ArgumentOutOfRangeException(nameof(cycleSequence));

        var eventId = WorldInstanceId + ":" + demandId + ":" + cycleSequence;
        var existing = _pending.FirstOrDefault(item => item.EventId == eventId);
        if (existing != null) return existing;
        var completed = new OrderCycleCompletedEvent(
            eventId, WorldInstanceId, demandId, orderDefinitionKey, cycleSequence);
        _pending.Add(completed);
        return completed;
    }

    /// <summary>重复确认安全；未知/已确认 EventId 返回 false。</summary>
    public bool Acknowledge(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            throw new ArgumentException("EventId 不能为空。", nameof(eventId));
        var index = _pending.FindIndex(item => item.EventId == eventId);
        if (index < 0) return false;
        _pending.RemoveAt(index);
        return true;
    }

    public OrderCycleOutboxSnapshot Capture() =>
        new(WorldInstanceId, _pending);

    private static string ValidateWorldInstanceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("WorldInstanceId 不能为空。", nameof(value));
        return value;
    }

    private static OrderCycleCompletedEvent ValidateEvent(OrderCycleCompletedEvent value)
    {
        if (value == null || string.IsNullOrWhiteSpace(value.EventId) ||
            string.IsNullOrWhiteSpace(value.WorldInstanceId) || value.DemandId <= 0 ||
            string.IsNullOrWhiteSpace(value.OrderDefinitionKey) || value.CycleSequence <= 0)
            throw new ArgumentException("订单周期事件无效。", nameof(value));
        var expected = value.WorldInstanceId + ":" + value.DemandId + ":" + value.CycleSequence;
        if (!string.Equals(expected, value.EventId, StringComparison.Ordinal))
            throw new ArgumentException("订单周期事件 EventId 与周期身份不一致。", nameof(value));
        return value;
    }
}

/// <summary>Outbox 检查点快照；pending 列表是尚未被 Space 确认的事件。</summary>
public sealed class OrderCycleOutboxSnapshot
{
    public string WorldInstanceId { get; }
    public IReadOnlyList<OrderCycleCompletedEvent> Pending { get; }

    public OrderCycleOutboxSnapshot(string worldInstanceId, IEnumerable<OrderCycleCompletedEvent> pending)
    {
        if (string.IsNullOrWhiteSpace(worldInstanceId))
            throw new ArgumentException("WorldInstanceId 不能为空。", nameof(worldInstanceId));
        WorldInstanceId = worldInstanceId;
        Pending = Array.AsReadOnly((pending ?? throw new ArgumentNullException(nameof(pending))).ToArray());
        if (Pending.Any(item => item == null || item.WorldInstanceId != worldInstanceId ||
            item.DemandId <= 0 || item.CycleSequence <= 0 ||
            string.IsNullOrWhiteSpace(item.EventId) || string.IsNullOrWhiteSpace(item.OrderDefinitionKey) ||
            item.EventId != worldInstanceId + ":" + item.DemandId + ":" + item.CycleSequence))
            throw new ArgumentException("Outbox 检查点包含无效事件。", nameof(pending));
        if (Pending.Select(item => item.EventId).Distinct(StringComparer.Ordinal).Count() != Pending.Count)
            throw new ArgumentException("Outbox 检查点包含重复 EventId。", nameof(pending));
    }
}

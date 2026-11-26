using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// 需求板的可恢复运行态。保留主动/被动区域的顺序、区域成功策略、
/// 订单规格和每张订单独立的冷却剩余时间。
/// </summary>
public sealed class OrderDemandBoardSnapshot
{
    public int DemandIdSequence { get; }
    public DemandAreaFulfillmentPolicy PassiveFulfillmentPolicy { get; }
    public DemandAreaFulfillmentPolicy ActiveFulfillmentPolicy { get; }
    public IReadOnlyList<OrderDemandSnapshot> PassiveDemands { get; }
    public IReadOnlyList<OrderDemandSnapshot> ActiveDemands { get; }
    public OrderCycleOutboxSnapshot Outbox { get; }

    public OrderDemandBoardSnapshot(
        int demandIdSequence,
        DemandAreaFulfillmentPolicy passiveFulfillmentPolicy,
        DemandAreaFulfillmentPolicy activeFulfillmentPolicy,
        IEnumerable<OrderDemandSnapshot> passiveDemands,
        IEnumerable<OrderDemandSnapshot> activeDemands,
        OrderCycleOutboxSnapshot? outbox = null)
    {
        if (demandIdSequence < 0) throw new ArgumentOutOfRangeException(nameof(demandIdSequence));
        if (!Enum.IsDefined(passiveFulfillmentPolicy))
            throw new ArgumentOutOfRangeException(nameof(passiveFulfillmentPolicy));
        if (!Enum.IsDefined(activeFulfillmentPolicy))
            throw new ArgumentOutOfRangeException(nameof(activeFulfillmentPolicy));

        DemandIdSequence = demandIdSequence;
        PassiveFulfillmentPolicy = passiveFulfillmentPolicy;
        ActiveFulfillmentPolicy = activeFulfillmentPolicy;
        PassiveDemands = Array.AsReadOnly((passiveDemands ?? throw new ArgumentNullException(nameof(passiveDemands))).ToArray());
        ActiveDemands = Array.AsReadOnly((activeDemands ?? throw new ArgumentNullException(nameof(activeDemands))).ToArray());
        Outbox = outbox ?? new OrderCycleOutbox().Capture();

        var all = PassiveDemands.Concat(ActiveDemands).ToArray();
        if (all.Any(item => item == null))
            throw new ArgumentException("需求板检查点不能包含 null 订单");
        if (all.Any(item => item.Id <= 0 || item.CooldownDurationLogicalMilli < 0 ||
            item.CooldownRemainingLogicalMilli < 0 ||
            item.CooldownRemainingLogicalMilli > item.CooldownDurationLogicalMilli ||
            item.CycleSequence < 0 ||
            (item.OrderDefinitionKey != null && string.IsNullOrWhiteSpace(item.OrderDefinitionKey))))
            throw new ArgumentException("需求板检查点包含非法订单身份或冷却状态");
        if (all.Select(item => item.Id).Distinct().Count() != all.Length)
            throw new ArgumentException("需求板检查点包含重复需求 ID");
        if (all.Any(item => item.Id > demandIdSequence))
            throw new ArgumentException("需求 ID 序列小于检查点中已分配的最大 ID");
    }
}

/// <summary>可序列化的单张罐子订单运行态；需求顺序由所属区域列表表达。</summary>
public sealed class OrderDemandSnapshot
{
    public int Id { get; }
    public string Type { get; }
    public IReadOnlyList<OrderRequirementSnapshot> Requirements { get; }
    public long CooldownDurationLogicalMilli { get; }
    public long CooldownRemainingLogicalMilli { get; }
    public string? OrderDefinitionKey { get; }
    public long CycleSequence { get; }

    public OrderDemandSnapshot(
        int id,
        string type,
        IEnumerable<OrderRequirementSnapshot> requirements,
        long cooldownDurationLogicalMilli,
        long cooldownRemainingLogicalMilli,
        string? orderDefinitionKey = null,
        long cycleSequence = 0)
    {
        Id = id;
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Requirements = Array.AsReadOnly((requirements ?? throw new ArgumentNullException(nameof(requirements))).ToArray());
        CooldownDurationLogicalMilli = cooldownDurationLogicalMilli;
        CooldownRemainingLogicalMilli = cooldownRemainingLogicalMilli;
        OrderDefinitionKey = orderDefinitionKey;
        CycleSequence = cycleSequence;
    }
}

public sealed record OrderRequirementSnapshot(string Shape, string? Liquid);

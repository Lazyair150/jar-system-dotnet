using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Config;
using JarSystem.Core.Production;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World;

/// <summary>
/// World V2 活动执行检查点的根级 JSON 契约。
/// 只包含显式 wire 值；不序列化 Context、Workshop、JarPool、Scope 或服务对象引用。
/// </summary>
public sealed class WorldV2ExecutionCheckpointWireDto
{
    /// <summary>Optional for legacy checkpoints; new host-created worlds should always populate it.</summary>
    public string? WorldInstanceId { get; set; }
    public WorldV2OwnerIdentityMapWireDto? OwnerIdentities { get; set; }
    public List<string> Shapes { get; set; } = new();
    public List<string> Liquids { get; set; } = new();
    public Dictionary<string, LiquidPolarity> LiquidPolarities { get; set; } = new(StringComparer.Ordinal);
    public ResourceAreaWireDto ResourceArea { get; set; } = new();
    public DemandBoardWireDto DemandBoard { get; set; } = new();
    public ShipLiquidInventoryWireDto ShipLiquidInventory { get; set; } = new();
    public WorkshopRuntimeStateWireDto MasterWorkshop { get; set; } = new();
    public WorkshopRuntimeStateWireDto? SlaveWorkshop { get; set; }
    public AdmissionWireDto Admission { get; set; } = new();
    public WorldResourceReservationBookWireDto ReservationLeases { get; set; } = new();
    public ResourceLedgerWireDto ResourceLedger { get; set; } = new();
    public WorldClockWireDto Clock { get; set; } = new();
    public int RuntimeLedgerTick { get; set; }
    public List<WorldOrderExecutionSessionWireDto> Sessions { get; set; } = new();

    public static WorldV2ExecutionCheckpointWireDto FromCheckpoint(
        WorldV2ExecutionResourceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var wire = new WorldV2ExecutionCheckpointWireDto
        {
            WorldInstanceId = checkpoint.WorldInstanceId?.Value,
            OwnerIdentities = checkpoint.OwnerIdentities == null ? null : WorldV2OwnerIdentityMapWireDto.FromMap(checkpoint.OwnerIdentities),
            Shapes = checkpoint.Shapes.ToList(),
            Liquids = checkpoint.Liquids.ToList(),
            LiquidPolarities = checkpoint.LiquidPolarities.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            ResourceArea = ResourceAreaWireDto.FromSnapshot(checkpoint.ResourceArea),
            DemandBoard = DemandBoardWireDto.FromSnapshot(checkpoint.DemandBoard),
            ShipLiquidInventory = ShipLiquidInventoryWireDto.FromSnapshot(checkpoint.ShipLiquidInventory),
            MasterWorkshop = WorkshopRuntimeStateWireDto.FromSnapshot(checkpoint.MasterWorkshop),
            SlaveWorkshop = checkpoint.SlaveWorkshop == null
                ? null : WorkshopRuntimeStateWireDto.FromSnapshot(checkpoint.SlaveWorkshop),
            Admission = AdmissionWireDto.FromSnapshot(checkpoint.Admission),
            ReservationLeases = Clone(checkpoint.ReservationLeases),
            ResourceLedger = ResourceLedgerWireDto.FromSnapshot(checkpoint.ResourceLedger),
            Clock = WorldClockWireDto.FromSnapshot(checkpoint.Clock),
            RuntimeLedgerTick = checkpoint.RuntimeLedgerTick,
            Sessions = checkpoint.Sessions.Select(WorldOrderExecutionSessionWireDto.FromSnapshot).ToList()
        };
        // 来源是领域层已构造的不可变检查点；严格 Validate 在 JSON 入站/ToCheckpoint 边界执行。
        return wire;
    }

    public WorldV2ExecutionResourceCheckpoint ToCheckpoint(
        string? configurationFingerprint,
        WorldInstanceId? legacyMigrationWorldInstanceId = null)
    {
        Validate();
        if (legacyMigrationWorldInstanceId.HasValue && !legacyMigrationWorldInstanceId.Value.IsValid)
            throw new ArgumentException("Legacy migration WorldInstanceId 无效。", nameof(legacyMigrationWorldInstanceId));
        var persistedWorldInstanceId = WorldInstanceId == null
            ? (WorldInstanceId?)null
            : new global::JarSystem.Core.Domain.WorldInstanceId(WorldInstanceId);
        if (persistedWorldInstanceId.HasValue && legacyMigrationWorldInstanceId.HasValue &&
            persistedWorldInstanceId.Value != legacyMigrationWorldInstanceId.Value)
            throw new InvalidOperationException("迁移身份不能覆盖检查点中已持久化的 WorldInstanceId。");
        var effectiveWorldInstanceId = persistedWorldInstanceId ?? legacyMigrationWorldInstanceId;
        if (OwnerIdentities != null && !effectiveWorldInstanceId.HasValue)
            throw new InvalidOperationException("OwnerIdentity 映射不能脱离 WorldInstanceId 恢复。");
        var effectiveOwnerIdentities = OwnerIdentities == null
            ? null
            : OwnerIdentities.ToMap(effectiveWorldInstanceId!.Value);
        var vocabulary = new Vocabulary(Shapes, Liquids, LiquidPolarities);
        var resourceArea = ResourceArea.ToSnapshot(vocabulary);
        var demandBoard = DemandBoard.ToSnapshot();
        var ship = ShipLiquidInventory.ToSnapshot();
        var master = MasterWorkshop.ToSnapshot(vocabulary);
        var slave = SlaveWorkshop?.ToSnapshot(vocabulary);
        var admission = Admission.ToSnapshot();
        var leases = Clone(ReservationLeases);
        var reservations = WorldResourceReservationBook.RestoreWire(leases).Capture();
        var ledger = ResourceLedger.ToSnapshot();
        var clock = Clock.ToSnapshot();
        var sessions = Sessions.Select(item => item.ToSnapshot()).ToArray();

        return new WorldV2ExecutionResourceCheckpoint(
            resourceArea, demandBoard, configurationFingerprint, ship,
            master, slave, admission, reservations, leases, ledger, clock,
            RuntimeLedgerTick, Shapes, Liquids, LiquidPolarities, sessions,
            effectiveWorldInstanceId, effectiveOwnerIdentities);
    }

    public void Validate()
    {
        if (WorldInstanceId != null && (string.IsNullOrWhiteSpace(WorldInstanceId) ||
            !string.Equals(WorldInstanceId, WorldInstanceId.Trim(), StringComparison.Ordinal)))
            throw new InvalidOperationException("根级检查点 WorldInstanceId 无效。");
        OwnerIdentities?.Validate();
        if (Shapes == null || Liquids == null ||
            Shapes.Count == 0 || Liquids.Count != 2 ||
            Shapes.Any(string.IsNullOrWhiteSpace) || Liquids.Any(string.IsNullOrWhiteSpace) ||
            Shapes.Distinct(StringComparer.Ordinal).Count() != Shapes.Count ||
            Liquids.Distinct(StringComparer.Ordinal).Count() != Liquids.Count ||
            LiquidPolarities == null || LiquidPolarities.Count != 2 ||
            Liquids.Any(liquid => !LiquidPolarities.ContainsKey(liquid)) ||
            LiquidPolarities.Keys.Any(key => !Liquids.Contains(key, StringComparer.Ordinal)) ||
            LiquidPolarities.Values.Count(value => value == LiquidPolarity.Cathode) != 1 ||
            LiquidPolarities.Values.Count(value => value == LiquidPolarity.Anode) != 1)
            throw new InvalidOperationException("根级检查点词表缺失、重复或无效。");
        if (ResourceArea == null || DemandBoard == null || ShipLiquidInventory == null ||
            MasterWorkshop == null || Admission == null || ReservationLeases == null ||
            ResourceLedger == null || Clock == null || Sessions == null)
            throw new InvalidOperationException("根级检查点缺少必要组件。");
        if (RuntimeLedgerTick < 0)
            throw new InvalidOperationException("根级检查点 RuntimeLedgerTick 不能为负数。");
        // 子 DTO 在 ToSnapshot/RestoreWire 时执行字段校验；跨组件身份、阶段和 tick
        // 关系由 WorldV2Context 的统一恢复预检负责，避免两套业务不变量逐渐漂移。
        if (Sessions.Any(item => item == null) ||
            Sessions.Select(item => item.DemandId).Distinct().Count() != Sessions.Count)
            throw new InvalidOperationException("根级检查点包含空 Session 或重复 Session ID.");
    }

    private static WorldResourceReservationBookWireDto Clone(WorldResourceReservationBookWireDto source) =>
        source.DeepCopy();


    internal static Dictionary<string, int> Copy(IReadOnlyDictionary<string, int> source) =>
        source.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
}

public sealed class JarRuntimeStateWireDto
{
    public int? Id { get; set; }
    public string Shape { get; set; } = "";
    public string? Liquid { get; set; }
    public bool IsMarked { get; set; }

    public static JarRuntimeStateWireDto FromJar(Jar jar) => new()
    {
        Id = jar.Id, Shape = jar.Shape, Liquid = jar.Liquid, IsMarked = jar.IsMarked
    };

    public Jar ToJar(Vocabulary vocabulary)
    {
        if (Id is <= 0 || string.IsNullOrWhiteSpace(Shape))
            throw new InvalidOperationException("罐子 Wire DTO 身份或形状无效。");
        var jar = new Jar(Shape, Liquid, Id, vocabulary);
        if (IsMarked) jar.Mark();
        return jar;
    }
}

public sealed class ResourceAreaWireDto
{
    public long JarFlowRateMilli { get; set; }
    public long LiquidFlowRateMilli { get; set; }
    public long JarFlowAccumulator { get; set; }
    public long LiquidFlowAccumulator { get; set; }
    public ProducerModuleWireDto Producers { get; set; } = new();
    public LiquidProductionWireDto LiquidProduction { get; set; } = new();
    public WarehouseWireDto Warehouse { get; set; } = new();

    public static ResourceAreaWireDto FromSnapshot(ResourceAreaRuntimeStateSnapshot value) => new()
    {
        JarFlowRateMilli = value.JarFlowRateMilli,
        LiquidFlowRateMilli = value.LiquidFlowRateMilli,
        JarFlowAccumulator = value.JarFlowAccumulator,
        LiquidFlowAccumulator = value.LiquidFlowAccumulator,
        Producers = ProducerModuleWireDto.FromSnapshot(value.Producers),
        LiquidProduction = LiquidProductionWireDto.FromSnapshot(value.LiquidProduction),
        Warehouse = WarehouseWireDto.FromSnapshot(value.Warehouse)
    };

    public void Validate()
    {
        if (JarFlowRateMilli < 0 || LiquidFlowRateMilli < 0 ||
            JarFlowAccumulator < 0 || JarFlowAccumulator >= 1_000_000 ||
            LiquidFlowAccumulator < 0 || LiquidFlowAccumulator >= 1_000_000 ||
            Producers == null || LiquidProduction == null || Warehouse == null)
            throw new InvalidOperationException("资源区 Wire DTO 包含无效状态。");
        Producers.Validate();
        LiquidProduction.Validate();
        Warehouse.Validate();
    }

    public ResourceAreaRuntimeStateSnapshot ToSnapshot(Vocabulary vocabulary)
    {
        Validate();
        return new ResourceAreaRuntimeStateSnapshot(
            JarFlowRateMilli, LiquidFlowRateMilli, JarFlowAccumulator, LiquidFlowAccumulator,
            Producers.ToSnapshot(), LiquidProduction.ToSnapshot(), Warehouse.ToSnapshot(vocabulary));
    }
}

public sealed class ProducerModuleWireDto
{
    public int ProducerSlots { get; set; }
    public Dictionary<string, Dictionary<string, double>> Producers { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> GeneratorAccumulatedMicro { get; set; } = new(StringComparer.Ordinal);

    public static ProducerModuleWireDto FromSnapshot(ProducerModuleRuntimeStateSnapshot value) => new()
    {
        ProducerSlots = value.ProducerSlots,
        Producers = value.Producers.ToDictionary(
            item => item.Key,
            item => item.Value.ToDictionary(rate => rate.Key, rate => rate.Value, StringComparer.Ordinal),
            StringComparer.Ordinal),
        GeneratorAccumulatedMicro = new Dictionary<string, long>(value.GeneratorAccumulatedMicro, StringComparer.Ordinal)
    };

    public void Validate()
    {
        if (ProducerSlots < 0 || Producers == null || GeneratorAccumulatedMicro == null ||
            Producers.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value == null ||
                item.Value.Any(rate => string.IsNullOrWhiteSpace(rate.Key) ||
                    double.IsNaN(rate.Value) || double.IsInfinity(rate.Value) || rate.Value < 0)) ||
            GeneratorAccumulatedMicro.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0))
            throw new InvalidOperationException("生产器 Wire DTO 无效。");
    }

    public ProducerModuleRuntimeStateSnapshot ToSnapshot()
    {
        Validate();
        return new ProducerModuleRuntimeStateSnapshot(
            ProducerSlots,
            Producers.Select(item => new KeyValuePair<string, IReadOnlyDictionary<string, double>>(
                item.Key, item.Value)),
            GeneratorAccumulatedMicro);
    }
}

public sealed class LiquidProductionWireDto
{
    public Dictionary<string, double> Rates { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> AccumulatedMicro { get; set; } = new(StringComparer.Ordinal);

    public static LiquidProductionWireDto FromSnapshot(LiquidProductionRuntimeStateSnapshot value) => new()
    {
        Rates = new Dictionary<string, double>(value.Rates, StringComparer.Ordinal),
        AccumulatedMicro = new Dictionary<string, long>(value.AccumulatedMicro, StringComparer.Ordinal)
    };

    public void Validate()
    {
        if (Rates == null || AccumulatedMicro == null ||
            Rates.Any(item => string.IsNullOrWhiteSpace(item.Key) ||
                double.IsNaN(item.Value) || double.IsInfinity(item.Value) || item.Value < 0) ||
            AccumulatedMicro.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0))
            throw new InvalidOperationException("液体产能 Wire DTO 无效。");
    }

    public LiquidProductionRuntimeStateSnapshot ToSnapshot()
    {
        Validate();
        return new LiquidProductionRuntimeStateSnapshot(Rates, AccumulatedMicro);
    }
}

public sealed class WarehouseWireDto
{
    public List<JarRuntimeStateWireDto> Jars { get; set; } = new();
    public Dictionary<string, int> LiquidWarehouse { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> DiscardedLiquids { get; set; } = new(StringComparer.Ordinal);
    public int DiscardedJars { get; set; }

    public static WarehouseWireDto FromSnapshot(WarehouseRuntimeStateSnapshot value) => new()
    {
        Jars = value.Jars.Select(JarRuntimeStateWireDto.FromJar).ToList(),
        LiquidWarehouse = WorldV2ExecutionCheckpointWireDto.Copy(value.LiquidWarehouse),
        DiscardedLiquids = WorldV2ExecutionCheckpointWireDto.Copy(value.DiscardedLiquids),
        DiscardedJars = value.DiscardedJars
    };

    public void Validate()
    {
        if (Jars == null || Jars.Any(item => item == null) ||
            LiquidWarehouse == null || DiscardedLiquids == null || DiscardedJars < 0 ||
            LiquidWarehouse.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0) ||
            DiscardedLiquids.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0))
            throw new InvalidOperationException("中央仓库 Wire DTO 无效。");
    }

    public WarehouseRuntimeStateSnapshot ToSnapshot(Vocabulary vocabulary)
    {
        Validate();
        return new WarehouseRuntimeStateSnapshot(Jars.Select(item => item.ToJar(vocabulary)),
            LiquidWarehouse, DiscardedLiquids, DiscardedJars);
    }
}

public sealed class DemandBoardWireDto
{
    public int DemandIdSequence { get; set; }
    public DemandAreaFulfillmentPolicy PassiveFulfillmentPolicy { get; set; }
    public DemandAreaFulfillmentPolicy ActiveFulfillmentPolicy { get; set; }
    public List<OrderDemandWireDto> PassiveDemands { get; set; } = new();
    public List<OrderDemandWireDto> ActiveDemands { get; set; } = new();
    public OrderCycleOutboxWireDto Outbox { get; set; } = new();

    public static DemandBoardWireDto FromSnapshot(OrderDemandBoardSnapshot value) => new()
    {
        DemandIdSequence = value.DemandIdSequence,
        PassiveFulfillmentPolicy = value.PassiveFulfillmentPolicy,
        ActiveFulfillmentPolicy = value.ActiveFulfillmentPolicy,
        PassiveDemands = value.PassiveDemands.Select(OrderDemandWireDto.FromSnapshot).ToList(),
        ActiveDemands = value.ActiveDemands.Select(OrderDemandWireDto.FromSnapshot).ToList(),
        Outbox = OrderCycleOutboxWireDto.FromSnapshot(value.Outbox)
    };

    public void Validate()
    {
        if (DemandIdSequence < 0 || !Enum.IsDefined(PassiveFulfillmentPolicy) ||
            !Enum.IsDefined(ActiveFulfillmentPolicy) || PassiveDemands == null || ActiveDemands == null || Outbox == null)
            throw new InvalidOperationException("需求板 Wire DTO 无效。");
        Outbox.Validate();
        foreach (var item in PassiveDemands.Concat(ActiveDemands)) item.Validate();
        var ids = PassiveDemands.Concat(ActiveDemands).Select(item => item.Id).ToArray();
        if (ids.Distinct().Count() != ids.Length || ids.Any(id => id <= 0 || id > DemandIdSequence))
            throw new InvalidOperationException("需求板 Wire DTO 包含重复订单或非法 ID 序列。");
    }

    public OrderDemandBoardSnapshot ToSnapshot()
    {
        Validate();
        return new OrderDemandBoardSnapshot(DemandIdSequence, PassiveFulfillmentPolicy,
            ActiveFulfillmentPolicy, PassiveDemands.Select(item => item.ToSnapshot()),
            ActiveDemands.Select(item => item.ToSnapshot()), Outbox.ToSnapshot());
    }
}

public sealed class OrderDemandWireDto
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public List<OrderRequirementWireDto> Requirements { get; set; } = new();
    public long CooldownDurationLogicalMilli { get; set; }
    public long CooldownRemainingLogicalMilli { get; set; }
    public string? OrderDefinitionKey { get; set; }
    public long CycleSequence { get; set; }

    public static OrderDemandWireDto FromSnapshot(OrderDemandSnapshot value) => new()
    {
        Id = value.Id, Type = value.Type,
        Requirements = value.Requirements.Select(item => new OrderRequirementWireDto
            { Shape = item.Shape, Liquid = item.Liquid }).ToList(),
        CooldownDurationLogicalMilli = value.CooldownDurationLogicalMilli,
        CooldownRemainingLogicalMilli = value.CooldownRemainingLogicalMilli,
        OrderDefinitionKey = value.OrderDefinitionKey,
        CycleSequence = value.CycleSequence
    };

    public void Validate()
    {
        if (Id <= 0 || string.IsNullOrWhiteSpace(Type) || Requirements == null ||
            Requirements.Count == 0 || CooldownDurationLogicalMilli < 0 ||
            CooldownRemainingLogicalMilli < 0 || CooldownRemainingLogicalMilli > CooldownDurationLogicalMilli || CycleSequence < 0 ||
            (OrderDefinitionKey != null && string.IsNullOrWhiteSpace(OrderDefinitionKey)) ||
            Requirements.Any(item => item == null || string.IsNullOrWhiteSpace(item.Shape)))
            throw new InvalidOperationException("订单 Wire DTO 无效。");
    }

    public OrderDemandSnapshot ToSnapshot()
    {
        Validate();
        return new OrderDemandSnapshot(Id, Type,
            Requirements.Select(item => new OrderRequirementSnapshot(item.Shape, item.Liquid)),
            CooldownDurationLogicalMilli, CooldownRemainingLogicalMilli,
            OrderDefinitionKey, CycleSequence);
    }
}

public sealed class OrderRequirementWireDto
{
    public string Shape { get; set; } = "";
    public string? Liquid { get; set; }
}

public sealed class OrderCycleOutboxWireDto
{
    public string WorldInstanceId { get; set; } = "";
    public List<OrderCycleCompletedEventWireDto> Pending { get; set; } = new();

    public static OrderCycleOutboxWireDto FromSnapshot(OrderCycleOutboxSnapshot value) => new()
    {
        WorldInstanceId = value.WorldInstanceId,
        Pending = value.Pending.Select(OrderCycleCompletedEventWireDto.FromEvent).ToList()
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WorldInstanceId) || Pending == null ||
            Pending.Any(item => item == null))
            throw new InvalidOperationException("订单事件 Outbox Wire DTO 无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Pending)
        {
            item.Validate(WorldInstanceId);
            if (!ids.Add(item.EventId))
                throw new InvalidOperationException("订单事件 Outbox 包含重复 EventId。");
        }
    }

    public OrderCycleOutboxSnapshot ToSnapshot()
    {
        Validate();
        return new OrderCycleOutboxSnapshot(WorldInstanceId, Pending.Select(item => item.ToEvent()));
    }
}

public sealed class OrderCycleCompletedEventWireDto
{
    public string EventId { get; set; } = "";
    public string WorldInstanceId { get; set; } = "";
    public int DemandId { get; set; }
    public string OrderDefinitionKey { get; set; } = "";
    public long CycleSequence { get; set; }

    public static OrderCycleCompletedEventWireDto FromEvent(OrderCycleCompletedEvent value) => new()
    {
        EventId = value.EventId, WorldInstanceId = value.WorldInstanceId,
        DemandId = value.DemandId, OrderDefinitionKey = value.OrderDefinitionKey,
        CycleSequence = value.CycleSequence
    };

    public void Validate(string expectedWorldInstanceId)
    {
        if (string.IsNullOrWhiteSpace(EventId) || WorldInstanceId != expectedWorldInstanceId ||
            DemandId <= 0 || string.IsNullOrWhiteSpace(OrderDefinitionKey) || CycleSequence <= 0 ||
            EventId != WorldInstanceId + ":" + DemandId + ":" + CycleSequence)
            throw new InvalidOperationException("订单周期事件 Wire DTO 无效。");
    }

    public OrderCycleCompletedEvent ToEvent() =>
        new(EventId, WorldInstanceId, DemandId, OrderDefinitionKey, CycleSequence);
}

public sealed class ShipLiquidInventoryWireDto
{
    public int Capacity { get; set; }
    public Dictionary<string, int> Balances { get; set; } = new(StringComparer.Ordinal);

    public static ShipLiquidInventoryWireDto FromSnapshot(ShipLiquidInventorySnapshot value) => new()
    {
        Capacity = value.Capacity,
        Balances = WorldV2ExecutionCheckpointWireDto.Copy(value.Balances)
    };

    public void Validate()
    {
        if (Capacity < 0 || Balances == null ||
            Balances.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0) ||
            Balances.Values.Sum(value => (long)value) > Capacity)
            throw new InvalidOperationException("自机液库 Wire DTO 无效。");
    }

    public ShipLiquidInventorySnapshot ToSnapshot()
    {
        Validate();
        return new ShipLiquidInventorySnapshot(Capacity, Balances);
    }
}

public sealed class WorkshopRuntimeStateWireDto
{
    public string Name { get; set; } = "";
    public int MaxJars { get; set; }
    public int MaxLiquids { get; set; }
    public int LiquidStagingCapacity { get; set; }
    public List<string> BaseMarks { get; set; } = new();
    public List<string> AdditionalMarks { get; set; } = new();
    public List<string> BannedLiquids { get; set; } = new();
    public List<JarRuntimeStateWireDto> Jars { get; set; } = new();
    public Dictionary<string, int> LiquidStorage { get; set; } = new(StringComparer.Ordinal);

    public static WorkshopRuntimeStateWireDto FromSnapshot(WorkshopRuntimeStateSnapshot value) => new()
    {
        Name = value.Name, MaxJars = value.MaxJars, MaxLiquids = value.MaxLiquids,
        LiquidStagingCapacity = value.LiquidStagingCapacity,
        BaseMarks = value.BaseMarks.ToList(), AdditionalMarks = value.AdditionalMarks.ToList(),
        BannedLiquids = value.BannedLiquids.ToList(),
        Jars = value.Jars.Select(JarRuntimeStateWireDto.FromJar).ToList(),
        LiquidStorage = WorldV2ExecutionCheckpointWireDto.Copy(value.LiquidStorage)
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || MaxJars <= 0 || MaxLiquids <= 0 ||
            LiquidStagingCapacity < 0 || BaseMarks == null || AdditionalMarks == null ||
            BannedLiquids == null || Jars == null || LiquidStorage == null ||
            Jars.Count > MaxJars || Jars.Any(item => item == null) || LiquidStorage.Any(item => string.IsNullOrWhiteSpace(item.Key) ||
                item.Value < 0 || item.Value > MaxLiquids))
            throw new InvalidOperationException("工作台 Wire DTO 无效。");
        var ids = Jars.Select(item => item.Id).ToArray();
        if (ids.Any(id => id is not > 0) || ids.Distinct().Count() != ids.Length)
            throw new InvalidOperationException("工作台 Wire DTO 包含无效或重复罐子 ID。");
    }

    public WorkshopRuntimeStateSnapshot ToSnapshot(Vocabulary vocabulary)
    {
        Validate();
        return new WorkshopRuntimeStateSnapshot(Name, MaxJars, MaxLiquids, LiquidStagingCapacity,
            BaseMarks, AdditionalMarks, BannedLiquids,
            Jars.Select(item => item.ToJar(vocabulary)), LiquidStorage);
    }
}

public sealed class AdmissionWireDto
{
    public WorkshopLiquidStagingWireDto? MasterStaging { get; set; }
    public WorkshopLiquidStagingWireDto? SlaveStaging { get; set; }
    public List<WorldOrderExecutionScopeSnapshot> ActiveOrders { get; set; } = new();

    public static AdmissionWireDto FromSnapshot(WorldOrderExecutionAdmissionSnapshot value) => new()
    {
        MasterStaging = value.MasterStaging == null ? null : WorkshopLiquidStagingWireDto.FromSnapshot(value.MasterStaging),
        SlaveStaging = value.SlaveStaging == null ? null : WorkshopLiquidStagingWireDto.FromSnapshot(value.SlaveStaging),
        ActiveOrders = value.ActiveOrders.ToList()
    };

    public void Validate()
    {
        if (ActiveOrders == null) throw new InvalidOperationException("准入 Wire DTO 缺少活动订单集合。");
        MasterStaging?.Validate();
        SlaveStaging?.Validate();
        if (ActiveOrders.Any(item => item == null || item.DemandId <= 0) ||
            ActiveOrders.Select(item => item.DemandId).Distinct().Count() != ActiveOrders.Count ||
            ActiveOrders.Any(item => item.UsesSlave != item.SlaveCapacityReserved))
            throw new InvalidOperationException("准入 Wire DTO 活动订单边界无效。");
        if (ActiveOrders.Any(item => item.UsesSlave) && SlaveStaging == null)
            throw new InvalidOperationException("B 类活动订单缺少从台暂存区。");
        if (ActiveOrders.Count > 0 && MasterStaging == null)
            throw new InvalidOperationException("活动订单缺少主台暂存区。");
    }

    public WorldOrderExecutionAdmissionSnapshot ToSnapshot()
    {
        Validate();
        return new WorldOrderExecutionAdmissionSnapshot(
            MasterStaging?.ToSnapshot(), SlaveStaging?.ToSnapshot(), ActiveOrders);
    }
}

public sealed class WorkshopLiquidStagingWireDto
{
    public int Capacity { get; set; }
    public List<string> BannedLiquids { get; set; } = new();
    public Dictionary<int, Dictionary<string, int>> Accounts { get; set; } = new();
    public Dictionary<int, int> ReservedCapacityByOrder { get; set; } = new();
    public Dictionary<string, int> PrefillBalances { get; set; } = new(StringComparer.Ordinal);

    public static WorkshopLiquidStagingWireDto FromSnapshot(WorkshopLiquidStagingSnapshot value) => new()
    {
        Capacity = value.Capacity,
        BannedLiquids = value.BannedLiquids.ToList(),
        Accounts = value.Accounts.ToDictionary(item => item.Key,
            item => WorldV2ExecutionCheckpointWireDto.Copy(item.Value)),
        ReservedCapacityByOrder = new Dictionary<int, int>(value.ReservedCapacityByOrder),
        PrefillBalances = WorldV2ExecutionCheckpointWireDto.Copy(value.PrefillBalances)
    };

    public void Validate()
    {
        if (Capacity < 0 || BannedLiquids == null || Accounts == null ||
            ReservedCapacityByOrder == null || PrefillBalances == null ||
            Accounts.Any(item => item.Key <= 0 || item.Value == null ||
                item.Value.Any(liquid => string.IsNullOrWhiteSpace(liquid.Key) || liquid.Value < 0)) ||
            ReservedCapacityByOrder.Any(item => item.Key <= 0 || item.Value < 0) ||
            PrefillBalances.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0))
            throw new InvalidOperationException("工作台液体暂存 Wire DTO 无效。");
        var ids = Accounts.Keys.Concat(ReservedCapacityByOrder.Keys).Distinct().ToArray();
        var occupied = PrefillBalances.Values.Sum(value => (long)value);
        foreach (var id in ids)
            occupied += Math.Max(ReservedCapacityByOrder.GetValueOrDefault(id),
                Accounts.TryGetValue(id, out var account) ? account.Values.Sum(value => (long)value) : 0L);
        if (occupied > Capacity)
            throw new InvalidOperationException("工作台液体暂存 Wire DTO 超过总容量。");
    }

    public WorkshopLiquidStagingSnapshot ToSnapshot()
    {
        Validate();
        return new WorkshopLiquidStagingSnapshot(Capacity, BannedLiquids, Accounts,
            ReservedCapacityByOrder, PrefillBalances);
    }
}

public sealed class ResourceLedgerWireDto
{
    public int Capacity { get; set; }
    public long LastSeq { get; set; }
    public int Dropped { get; set; }
    public List<ResourceEventWireDto> Events { get; set; } = new();
    public List<string> OperationIds { get; set; } = new();
    public List<ResourceTransferReceipt> TransferReceipts { get; set; } = new();
    public Dictionary<string, string> OperationFingerprints { get; set; } = new(StringComparer.Ordinal);

    public static ResourceLedgerWireDto FromSnapshot(ResourceLedgerSnapshot value) => new()
    {
        Capacity = value.Capacity, LastSeq = value.LastSeq, Dropped = value.Dropped,
        Events = value.Entries.Select(ResourceEventWireDto.FromEvent).ToList(),
        OperationIds = value.OperationIds.ToList(),
        TransferReceipts = value.TransferReceipts.Select(item => item with { }).ToList(),
        OperationFingerprints = value.OperationFingerprints.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
    };

    public void Validate()
    {
        if (Capacity < 1 || LastSeq < 0 || Dropped < 0 || Events == null || OperationIds == null ||
            TransferReceipts == null || OperationFingerprints == null || Events.Count > Capacity || OperationIds.Any(string.IsNullOrWhiteSpace) ||
            OperationIds.Distinct(StringComparer.Ordinal).Count() != OperationIds.Count ||
            TransferReceipts.Any(item => item == null || !OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                !OperationFingerprints.ContainsKey(item.OperationId)) ||
            TransferReceipts.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count() != TransferReceipts.Count ||
            OperationFingerprints.Any(item => string.IsNullOrWhiteSpace(item.Key) ||
                !OperationIds.Contains(item.Key, StringComparer.Ordinal) || item.Value == null || item.Value.Length != 64 ||
                item.Value.Any(character => !Uri.IsHexDigit(character))))
            throw new InvalidOperationException("资源账本 Wire DTO 无效。");
        long previous = 0;
        foreach (var item in Events)
        {
            if (item == null) throw new InvalidOperationException("资源账本 Wire DTO 包含空事件。");
            item.Validate();
            if (item.OperationId != null && (!OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                !OperationFingerprints.ContainsKey(item.OperationId)))
                throw new InvalidOperationException("账本事件 OperationId 不存在于幂等索引。");
            if (item.Seq <= previous || item.Seq > LastSeq) throw new InvalidOperationException("账本事件序号无效。");
            previous = item.Seq;
        }
        if (Events.Count > 0 && Events[^1].Seq != LastSeq)
            throw new InvalidOperationException("账本 LastSeq 与最后事件不一致。");
        foreach (var receipt in TransferReceipts)
        {
            receipt.Validate();
            if (receipt.Outcome is not (ResourceTransferOutcome.Transferred or ResourceTransferOutcome.PartiallyTransferred))
                throw new InvalidOperationException("资源账本 Wire DTO 只能包含已提交的转移回执。");
        }
    }

    public ResourceLedgerSnapshot ToSnapshot()
    {
        Validate();
        return new ResourceLedgerSnapshot(Capacity, LastSeq, Dropped,
            Events.Select(item => item.ToEvent()), OperationIds, TransferReceipts, OperationFingerprints);
    }
}

public sealed class ResourceEventWireDto
{
    public long Seq { get; set; }
    public int Tick { get; set; }
    public string Cause { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ResourceIdentity { get; set; }
    public string? OperationId { get; set; }
    public int Amount { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? FromAccountId { get; set; }
    public string? ToAccountId { get; set; }
    public ResourceEndpointWireDto? FromEndpoint { get; set; }
    public ResourceEndpointWireDto? ToEndpoint { get; set; }
    public string? Reason { get; set; }

    public static ResourceEventWireDto FromEvent(ResourceEvent value) => new()
    {
        Seq = value.Seq, Tick = value.Tick, Cause = value.Cause, Kind = value.Kind,
        Name = value.Name, ResourceIdentity = value.ResourceIdentity, OperationId = value.OperationId,
        Amount = value.Amount, From = value.From, To = value.To,
        FromAccountId = value.FromAccountId, ToAccountId = value.ToAccountId,
        FromEndpoint = ResourceEndpointWireDto.FromEndpoint(value.FromEndpoint),
        ToEndpoint = ResourceEndpointWireDto.FromEndpoint(value.ToEndpoint),
        Reason = value.Reason
    };

    public void Validate()
    {
        if (Seq <= 0 || Tick < 0 || Amount <= 0 ||
            !ResourceCauses.All.Contains(Cause, StringComparer.Ordinal) ||
            (Kind != ResourceKinds.Jar && Kind != ResourceKinds.Liquid) ||
            string.IsNullOrWhiteSpace(Name) ||
            (ResourceIdentity != null && string.IsNullOrWhiteSpace(ResourceIdentity)) ||
            (FromAccountId != null && string.IsNullOrWhiteSpace(FromAccountId)) ||
            (ToAccountId != null && string.IsNullOrWhiteSpace(ToAccountId)) ||
            (FromEndpoint != null && !FromEndpoint.IsValid) ||
            (ToEndpoint != null && !ToEndpoint.IsValid) ||
            (FromEndpoint != null && FromAccountId != null &&
                (FromEndpoint.Kind != ResourceEndpointKind.Account || FromEndpoint.AccountId != FromAccountId)) ||
            (ToEndpoint != null && ToAccountId != null &&
                (ToEndpoint.Kind != ResourceEndpointKind.Account || ToEndpoint.AccountId != ToAccountId)) ||
            (OperationId != null && (string.IsNullOrWhiteSpace(OperationId) || string.IsNullOrWhiteSpace(ResourceIdentity))) ||
            (OperationId != null && Kind == ResourceKinds.Jar && !IsCanonicalJarId(ResourceIdentity)))
            throw new InvalidOperationException("资源账本事件 Wire DTO 无效。");
    }

    private static bool IsCanonicalJarId(string? identity) =>
        int.TryParse(identity, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var jarId) && jarId > 0 &&
        jarId.ToString(System.Globalization.CultureInfo.InvariantCulture) == identity;

    public ResourceEvent ToEvent()
    {
        Validate();
        return new ResourceEvent
        {
            Seq = Seq, Tick = Tick, Cause = Cause, Kind = Kind, Name = Name,
            ResourceIdentity = ResourceIdentity, OperationId = OperationId,
            Amount = Amount, From = From, To = To,
            FromAccountId = FromAccountId, ToAccountId = ToAccountId,
            FromEndpoint = FromEndpoint?.ToEndpoint() ??
                (FromAccountId == null ? ResourceEndpoint.LegacyUnknown : ResourceEndpoint.ForAccount(FromAccountId)),
            ToEndpoint = ToEndpoint?.ToEndpoint() ??
                (ToAccountId == null ? ResourceEndpoint.LegacyUnknown : ResourceEndpoint.ForAccount(ToAccountId)),
            Reason = Reason
        };
    }
}

public sealed class WorldClockWireDto
{
    public long WorldTimeMilli { get; set; }
    public long LogicalTimeMilli { get; set; }
    public long SpeedRatioMilli { get; set; }
    public bool IsPaused { get; set; }
    public long ConversionRemainder { get; set; }

    public static WorldClockWireDto FromSnapshot(WorldClockSnapshot value) => new()
    {
        WorldTimeMilli = value.WorldTimeMilli, LogicalTimeMilli = value.LogicalTimeMilli,
        SpeedRatioMilli = value.SpeedRatioMilli, IsPaused = value.IsPaused,
        ConversionRemainder = value.ConversionRemainder
    };

    public void Validate()
    {
        if (WorldTimeMilli < 0 || LogicalTimeMilli < 0 || SpeedRatioMilli < 0 ||
            ConversionRemainder < 0 || ConversionRemainder >= 1000)
            throw new InvalidOperationException("世界时钟 Wire DTO 无效。");
    }

    public WorldClockSnapshot ToSnapshot()
    {
        Validate();
        return new WorldClockSnapshot(WorldTimeMilli, LogicalTimeMilli, SpeedRatioMilli,
            IsPaused, ConversionRemainder);
    }
}

/// <summary>Wire representation of the tagged resource provenance endpoint.</summary>
public sealed class ResourceEndpointWireDto
{
    public ResourceEndpointKind Kind { get; set; }
    public string? AccountId { get; set; }
    public string? ExternalIdentity { get; set; }

    public static ResourceEndpointWireDto FromEndpoint(ResourceEndpoint value) => new()
    {
        Kind = value.Kind,
        AccountId = value.AccountId,
        ExternalIdentity = value.ExternalIdentity
    };

    public bool IsValid
    {
        get
        {
            try { ToEndpoint().Validate(); return true; }
            catch (ArgumentException) { return false; }
            catch (FormatException) { return false; }
        }
    }

    public ResourceEndpoint ToEndpoint() => Kind switch
    {
        ResourceEndpointKind.LegacyUnknown when AccountId == null && ExternalIdentity == null =>
            ResourceEndpoint.LegacyUnknown,
        ResourceEndpointKind.Account when AccountId != null && ExternalIdentity == null =>
            ResourceEndpoint.ForAccount(AccountId),
        ResourceEndpointKind.ExternalSource when AccountId == null && ExternalIdentity != null =>
            ResourceEndpoint.ExternalSource(ExternalIdentity),
        ResourceEndpointKind.ExternalSink when AccountId == null && ExternalIdentity != null =>
            ResourceEndpoint.ExternalSink(ExternalIdentity),
        _ => throw new ArgumentException("Resource endpoint wire payload does not match its tag.")
    };
}

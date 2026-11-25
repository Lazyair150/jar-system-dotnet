using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Config;
using JarSystem.Core.Production;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World;

/// <summary>
/// 一个 World v2 世界的完整协作对象图。
///
/// Context 是装配边界，不是第二个运行时：它把同一世界的资源区、岗位、需求板、
/// 求解簿、资源预留、执行准入、Runtime 与 Scheduler 放在一起，防止宿主分别创建
/// 这些对象时误把不同世界的组件交叉连接。
///
/// 注意：本类型接收已构造的资源区、工作台和求解器注册表；它不是 WorldOptions/JSON 配置工厂。
/// 时间推进仍由 Runtime 执行；调度决策仍由 Scheduler 执行；本类型不启动线程、不推进时间。
/// </summary>
public sealed class WorldV2Context : IDisposable
{
    private bool _disposed;

    /// <summary>Context 是否已释放；释放后不得再作为当前世界使用。</summary>
    public bool IsDisposed => _disposed;

    public WorldInstanceId? WorldInstanceId { get; }
    public WorldV2OwnerIdentityMap? OwnerIdentities { get; }
    public ResourceArea Resources { get; }
    public ShipLiquidInventory? ShipLiquidInventory { get; }
    public ResourceLedger ResourceLedger { get; }
    public WorkshopRoster Roster { get; }
    public OrderDemandBoard DemandBoard { get; }
    public SequentialIdGenerator DemandIdGenerator { get; }
    public string? ConfigurationFingerprint { get; }
    public IReadOnlyList<string> Shapes { get; }
    public IReadOnlyList<string> Liquids { get; }
    public IReadOnlyDictionary<string, LiquidPolarity> LiquidPolarities { get; }
    public OrderTemplateLibrary OrderTemplates { get; }
    public WorldOrderSolveBook SolveBook { get; }
    /// <summary>
    /// 可跨资源库存变化复用的规则候选缓存。它是派生数据，不进入执行检查点；Restore 后可重建。
    /// </summary>
    public WorldOrderRuleCandidateBook RuleCandidates => SolveBook.RuleCandidates;
    public WorldResourceReservationBook Reservations { get; }
    public WorldOrderExecutionAdmission Admission { get; }
    public ResourceWorldRuntime Runtime { get; }
    public WorldOrderScheduler Scheduler { get; }

    private WorldV2Context(
        WorldInstanceId? worldInstanceId,
        WorldV2OwnerIdentityMap? ownerIdentities,
        ResourceArea resources,
        ShipLiquidInventory? shipLiquidInventory,
        ResourceLedger resourceLedger,
        WorkshopRoster roster,
        OrderDemandBoard demandBoard,
        SequentialIdGenerator demandIdGenerator,
        WorldOrderSolveBook solveBook,
        WorldResourceReservationBook reservations,
        WorldOrderExecutionAdmission admission,
        ResourceWorldRuntime runtime,
        WorldOrderScheduler scheduler,
        string? configurationFingerprint,
        IReadOnlyList<string> shapes,
        IReadOnlyList<string> liquids,
        IReadOnlyDictionary<string, LiquidPolarity> liquidPolarities,
        OrderTemplateLibrary orderTemplates)
    {
        WorldInstanceId = worldInstanceId;
        OwnerIdentities = ownerIdentities;
        Resources = resources;
        ShipLiquidInventory = shipLiquidInventory;
        ResourceLedger = resourceLedger;
        Roster = roster;
        DemandBoard = demandBoard;
        DemandIdGenerator = demandIdGenerator;
        SolveBook = solveBook;
        Reservations = reservations;
        Admission = admission;
        Runtime = runtime;
        Scheduler = scheduler;
        ConfigurationFingerprint = configurationFingerprint;
        Shapes = Array.AsReadOnly(shapes.ToArray());
        Liquids = Array.AsReadOnly(liquids.ToArray());
        LiquidPolarities = new ReadOnlyDictionary<string, LiquidPolarity>(
            liquidPolarities.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
        OrderTemplates = orderTemplates ?? throw new ArgumentNullException(nameof(orderTemplates));
    }

    /// <summary>
    /// 从世界的基础对象装配唯一一致的 World v2 协作对象图。
    /// 调用方负责创建资源区、工作台和求解器注册表；其余协作对象由本方法统一创建。
    /// 主动区与被动区的成功处理策略必须显式提供，避免配置遗漏时静默落入 OneShot。
    /// </summary>
    public static WorldV2Context Create(
        ResourceArea resources,
        IEnumerable<Workshop> workshops,
        Workshop master,
        Workshop? slave,
        WorldOrderSolverRegistry solverRegistry,
        long logicalTimePerUnit,
        DemandAreaFulfillmentPolicy passiveFulfillmentPolicy,
        DemandAreaFulfillmentPolicy activeFulfillmentPolicy,
        WorldClock? clock = null,
        ShipLiquidInventory? shipLiquidInventory = null,
        string? configurationFingerprint = null,
        IEnumerable<string>? shapeVocabulary = null,
        IEnumerable<string>? liquidVocabulary = null,
        OrderTemplateLibrary? orderTemplates = null,
        WorldInstanceId? worldInstanceId = null,
        WorldV2OwnerIdentityMap? ownerIdentities = null)
    {
        if (resources == null) throw new ArgumentNullException(nameof(resources));
        if (workshops == null) throw new ArgumentNullException(nameof(workshops));
        if (master == null) throw new ArgumentNullException(nameof(master));
        if (solverRegistry == null) throw new ArgumentNullException(nameof(solverRegistry));
        if (worldInstanceId.HasValue && !worldInstanceId.Value.IsValid)
            throw new ArgumentException("WorldInstanceId 无效。", nameof(worldInstanceId));
        if (ownerIdentities != null)
        {
            if (!worldInstanceId.HasValue)
                throw new ArgumentException("Owner identity map requires an explicit WorldInstanceId.", nameof(ownerIdentities));
            ownerIdentities.ValidateForWorld(worldInstanceId.Value);
        }
        if (logicalTimePerUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalTimePerUnit));
        if (shipLiquidInventory != null && resources.ShipLiquidInventory != null &&
            !ReferenceEquals(shipLiquidInventory, resources.ShipLiquidInventory))
            throw new ArgumentException("Context 与 ResourceArea 必须引用同一个自机液库", nameof(shipLiquidInventory));
        shipLiquidInventory ??= resources.ShipLiquidInventory;

        // 先完成所有可能失败的基础对象校验与组装，再向调用方返回 Context。
        var roster = new WorkshopRoster(workshops, master, slave);
        if (ownerIdentities != null)
        {
            ownerIdentities.ValidateForWorld(worldInstanceId!.Value);
            if (shipLiquidInventory?.OwnerIdentity != ownerIdentities.Ship ||
                resources.OwnerIdentity != ownerIdentities.ProductionArea ||
                resources.Producers.Warehouse.OwnerIdentity != ownerIdentities.Warehouse)
                throw new ArgumentException(
                    "Ship/ProductionArea/Warehouse runtime objects do not match the supplied OwnerIdentity map.",
                    nameof(ownerIdentities));

            var workshopIds = roster.Workshops
                .Select(item => item.OwnedInstanceId)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
            if (roster.Workshops.Any(item => string.IsNullOrWhiteSpace(item.OwnedInstanceId)) ||
                !ownerIdentities.Workshops.Keys.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(workshopIds))
                throw new ArgumentException(
                    "OwnerIdentity map must identify every exact Workshop OwnedInstanceId and no others.",
                    nameof(ownerIdentities));
            foreach (var workshop in roster.Workshops)
                if (workshop.OwnerIdentity != ownerIdentities.GetWorkshop(workshop.OwnedInstanceId!))
                    throw new ArgumentException(
                        "Workshop runtime OwnerIdentity does not match its exact OwnedInstanceId.",
                        nameof(ownerIdentities));

            var producerInstances = resources.Producers.Producers.Values.ToArray();
            var producerIds = producerInstances
                .Select(item => item.ProducerInstanceId)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
            if (producerInstances.Any(item => string.IsNullOrWhiteSpace(item.ProducerInstanceId)) ||
                !ownerIdentities.Producers.Keys.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(producerIds))
                throw new ArgumentException(
                    "OwnerIdentity map must identify every exact ProducerInstanceId and no others.",
                    nameof(ownerIdentities));
            foreach (var producer in producerInstances)
                if (producer.OwnerIdentity != ownerIdentities.GetProducer(producer.ProducerInstanceId!))
                    throw new ArgumentException(
                        "Producer runtime OwnerIdentity does not match its exact ProducerInstanceId.",
                        nameof(ownerIdentities));
        }

        var worldShapeIds = new HashSet<string>(master.KnownShapes, StringComparer.Ordinal);
        var worldLiquidIds = new HashSet<string>(master.Capability.Liquids, StringComparer.Ordinal);
        var worldLiquidPolarities = master.Capability.LiquidPolarities;
        foreach (var workshop in roster.Workshops)
        {
            var capability = workshop.Capability;
            if (!worldShapeIds.SetEquals(workshop.KnownShapes) ||
                !workshop.KnownShapes.SequenceEqual(master.KnownShapes, StringComparer.Ordinal) ||
                !worldLiquidIds.SetEquals(capability.Liquids) ||
                capability.LiquidPolarities.Count != worldLiquidPolarities.Count ||
                worldLiquidPolarities.Any(item =>
                    !capability.LiquidPolarities.TryGetValue(item.Key, out var polarity) ||
                    polarity != item.Value))
                throw new ArgumentException(
                    "同一 World v2 Context 的所有工作台必须共享完全一致的液体身份与极性映射及形状词表。",
                    nameof(workshops));
        }
        var board = new OrderDemandBoard(passiveFulfillmentPolicy, activeFulfillmentPolicy);
        var demandIdGenerator = new SequentialIdGenerator();
        var solveBook = new WorldOrderSolveBook(board);
        var reservations = new WorldResourceReservationBook();
        var resourceLedger = new ResourceLedger();
        if (shipLiquidInventory != null)
        {
            foreach (var item in shipLiquidInventory.Balances.OrderBy(item => item.Key, StringComparer.Ordinal))
                resourceLedger.Record(0, ResourceCauses.Stock, ResourceKinds.Liquid,
                    item.Key, item.Value, from: "关卡初始库存", to: "自机液库",
                    resourceIdentity: item.Key);
        }
        var admission = shipLiquidInventory == null
            ? new WorldOrderExecutionAdmission(board, solveBook, roster, reservations)
            : new WorldOrderExecutionAdmission(board, solveBook, roster, reservations, shipLiquidInventory);
        var runtime = new ResourceWorldRuntime(
            resources, clock: clock, roster: roster, demandBoard: board,
            resourceLedger: resourceLedger,
            masterPrefill: shipLiquidInventory == null
                ? null
                : targetWorkshop => admission.BeginMasterWorkshopPrefill(
                    targetWorkshop, resources.LiquidProductionPriority),
            returnRetiredMasterPrefill: shipLiquidInventory == null
                ? null
                : retiredMaster => admission.ReturnRetiredMasterPrefill(retiredMaster));
        admission.BindOrderAdmissionGate(() => !runtime.BlocksOrderAdmission);
        var scheduler = new WorldOrderScheduler(
            runtime, board, solveBook, solverRegistry, admission, roster, logicalTimePerUnit);

        var shapes = (shapeVocabulary ?? master.KnownShapes)
            .Distinct(StringComparer.Ordinal).ToArray();
        var liquids = (liquidVocabulary ?? master.KnownLiquids)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (shapes.Length == 0 || liquids.Length != 2 ||
            shapes.Any(string.IsNullOrWhiteSpace) || liquids.Any(string.IsNullOrWhiteSpace) ||
            !worldShapeIds.SetEquals(shapes) ||
            !master.KnownShapes.SequenceEqual(shapes, StringComparer.Ordinal) ||
            !worldLiquidIds.SetEquals(liquids) ||
            !master.KnownLiquids.SequenceEqual(liquids, StringComparer.Ordinal))
            throw new ArgumentException("World v2 Context 必须使用工作台所属 World 的完整形状与液体词表");

        return new WorldV2Context(
            worldInstanceId, ownerIdentities, resources, shipLiquidInventory, resourceLedger, roster, board, demandIdGenerator,
            solveBook, reservations, admission, runtime, scheduler, configurationFingerprint,
            shapes, liquids, master.Capability.LiquidPolarities,
            orderTemplates ?? OrderTemplateLibrary.Empty);
    }

    /// <summary>
    /// 捕获活动执行所需的工作台动态库存、液体资源、需求板、资源预留、账本、时钟与会话进度，
    /// 同时记录由 WorldV2Factory 装配时生成的配置指纹。必须在 Host 独占操作内调用。
    /// 当前 World v2 Context 尚未持有动态 ModifierStack；不能把其他旧世界修饰器误认为已纳入本检查点。
    /// </summary>
    public WorldV2ExecutionResourceCheckpoint CaptureExecutionResourceCheckpoint()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorldV2Context));
        if (Runtime.IsFaulted || Runtime.ActiveReplacement is { IsFinished: false } ||
            Runtime.ActiveReplacement is { InterruptionOutcomeUncertain: true })
            throw new InvalidOperationException(
                "世界故障或工作台替换状态尚未纳入执行资源检查点，拒绝生成不完整检查点。");
        if (ShipLiquidInventory == null)
            throw new InvalidOperationException("旧版兼容世界没有自机液库，不能捕获新液体资源检查点。");
        return new WorldV2ExecutionResourceCheckpoint(
            Resources.CaptureRuntimeState(),
            DemandBoard.CaptureRuntimeState(DemandIdGenerator),
            ConfigurationFingerprint,
            ShipLiquidInventory.Capture(),
            Roster.Master?.CaptureRuntimeState()
                ?? throw new InvalidOperationException("检查点捕获时没有主工作台。"),
            Roster.Slave?.CaptureRuntimeState(),
            Admission.Capture(),
            Reservations.Capture(),
            Reservations.CaptureWire(),
            ResourceLedger.Capture(),
            Runtime.Clock.Capture(),
            Runtime.LedgerTick,
            Shapes,
            Liquids,
            LiquidPolarities,
            Runtime.ActiveExecutionSessions.Select(session => session.CaptureProgress()),
            WorldInstanceId,
            OwnerIdentities);
    }

    /// <summary>
    /// 将活动执行检查点恢复到一个已按相同世界配置装配的空 Context；需求板本身也由检查点恢复。
    /// 此方法不会重新准入订单，不会重复扣液、泵送、MATCH 或执行任何计划步骤。
    /// </summary>
    public void RestoreExecutionResourceCheckpoint(WorldV2ExecutionResourceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (_disposed) throw new ObjectDisposedException(nameof(WorldV2Context));
        if (ShipLiquidInventory == null)
            throw new InvalidOperationException("旧版兼容世界不能恢复新液体资源检查点。");
        if (Runtime.IsFaulted || Runtime.ActiveReplacement is { IsFinished: false } ||
            Runtime.ActiveReplacement is { InterruptionOutcomeUncertain: true } ||
            Runtime.ActiveExecutionSessions.Count != 0 || Reservations.Count != 0 ||
            Admission.Capture().ActiveOrders.Count != 0)
            throw new InvalidOperationException("只能向没有活动会话、预留或工作台替换的 Context 恢复检查点。");

        Resources.ValidateRuntimeStateSnapshot(checkpoint.ResourceArea);
        if (WorldInstanceId != checkpoint.WorldInstanceId)
            throw new InvalidOperationException("检查点 WorldInstanceId 与当前世界不一致，拒绝恢复。");
        if ((OwnerIdentities == null) != (checkpoint.OwnerIdentities == null) ||
            (OwnerIdentities != null && !OwnerIdentities.IsEquivalentTo(checkpoint.OwnerIdentities)))
            throw new InvalidOperationException("检查点 OwnerIdentity 映射与当前世界不一致，拒绝恢复。");
        foreach (var entry in checkpoint.ResourceLedger.Entries)
        {
            if (entry.FromAccountId != null)
                AccountIdentity.ValidateWorldScope(entry.FromAccountId, WorldInstanceId);
            if (entry.ToAccountId != null)
                AccountIdentity.ValidateWorldScope(entry.ToAccountId, WorldInstanceId);
        }
        foreach (var receipt in checkpoint.ResourceLedger.TransferReceipts)
        {
            AccountIdentity.ValidateWorldScope(receipt.SourceAccount.AccountId, WorldInstanceId);
            AccountIdentity.ValidateWorldScope(receipt.TargetAccount.AccountId, WorldInstanceId);
        }
        if (!string.Equals(ConfigurationFingerprint, checkpoint.ConfigurationFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("检查点配置指纹与当前世界不一致，拒绝恢复。");
        if (!Shapes.SequenceEqual(checkpoint.Shapes, StringComparer.Ordinal) ||
            !Liquids.SequenceEqual(checkpoint.Liquids, StringComparer.Ordinal) ||
            checkpoint.LiquidPolarities.Count != LiquidPolarities.Count ||
            LiquidPolarities.Any(item => !checkpoint.LiquidPolarities.TryGetValue(item.Key, out var polarity) || polarity != item.Value))
            throw new InvalidOperationException("检查点形状/液体词表/极性映射与当前世界配置不一致，拒绝恢复。");
        if (checkpoint.ShipLiquidInventory.Capacity != ShipLiquidInventory.Capacity)
            throw new InvalidOperationException("检查点自机液库容量与当前世界配置不一致。");
        if ((Roster.Slave == null) != (checkpoint.SlaveWorkshop == null))
            throw new InvalidOperationException("检查点主从工作台岗位结构与当前世界不一致。");
        Roster.Master!.ValidateRuntimeStateSnapshot(checkpoint.MasterWorkshop);
        if (Roster.Slave != null)
            Roster.Slave.ValidateRuntimeStateSnapshot(checkpoint.SlaveWorkshop!);

        var allCheckpointJarIds = checkpoint.ResourceArea.Warehouse.Jars
            .Concat(checkpoint.MasterWorkshop.Jars)
            .Concat(checkpoint.SlaveWorkshop?.Jars ?? Array.Empty<Jar>())
            .Where(jar => jar.Id.HasValue)
            .Select(jar => jar.Id!.Value)
            .ToArray();
        if (allCheckpointJarIds.Distinct().Count() != allCheckpointJarIds.Length)
            throw new InvalidOperationException("检查点在中央仓库与工作台之间重复拥有同一罐子 ID。");

        if (checkpoint.ResourceLedger.Capacity != ResourceLedger.Capacity)
            throw new InvalidOperationException("检查点资源账本容量与当前世界不一致。");

        // 先在临时对象图中完整验证资源账户、所有权索引、时钟与会话游标。
        // 任一快照无效时，当前 Context 不发生任何状态修改。
        var stagedShip = new ShipLiquidInventory(
            checkpoint.ShipLiquidInventory.Capacity,
            checkpoint.ShipLiquidInventory.Balances);
        var vocabulary = new Vocabulary(checkpoint.Shapes, checkpoint.Liquids, checkpoint.LiquidPolarities);
        var stagedDemandIds = new SequentialIdGenerator();
        var stagedBoard = OrderDemandBoard.RestoreRuntimeState(
            checkpoint.DemandBoard, vocabulary, stagedDemandIds);
        var stagedSolveBook = new WorldOrderSolveBook(stagedBoard);
        var stagedReservations = WorldResourceReservationBook.RestoreWire(checkpoint.ReservationLeases);
        var stagedAdmission = new WorldOrderExecutionAdmission(
            stagedBoard, stagedSolveBook, Roster, stagedReservations, stagedShip);
        stagedAdmission.Restore(checkpoint.Admission);
        var stagedLedger = ResourceLedger.Restore(checkpoint.ResourceLedger);
        _ = WorldClock.Restore(checkpoint.Clock);
        var activeScopeIds = checkpoint.Admission.ActiveOrders.Select(item => item.DemandId).ToHashSet();
        var sessionIds = checkpoint.Sessions.Select(item => item.DemandId).ToHashSet();
        if (sessionIds.Count != checkpoint.Sessions.Count)
            throw new InvalidOperationException("检查点包含重复执行会话 ID。");
        if (checkpoint.Sessions.Any(item => item.DemandId <= 0))
            throw new InvalidOperationException("检查点包含无效执行会话 ID。");
        if (checkpoint.ResourceLedger.Entries.Any(item => item.Tick > checkpoint.RuntimeLedgerTick))
            throw new InvalidOperationException("检查点资源账本事件 tick 超过 Runtime 当前 tick。");
        if (!activeScopeIds.SetEquals(sessionIds))
            throw new InvalidOperationException("检查点活动订单范围与执行会话集合不一致。");
        if (checkpoint.RuntimeLedgerTick < 0)
            throw new InvalidOperationException("检查点 Runtime 账本 tick 无效。");
        foreach (var session in checkpoint.Sessions)
        {
            WorldOrderExecutionSession.ValidateProgressAgainstWorkshopSnapshots(
                session, checkpoint.MasterWorkshop, checkpoint.SlaveWorkshop);
            WorldOrderExecutionSession.RestoreProgress(
                session, stagedBoard, stagedSolveBook, stagedAdmission, Roster,
                stagedReservations, stagedLedger, () => checkpoint.RuntimeLedgerTick);
        }
        stagedSolveBook.Dispose();

        // Advance every Workshop ID source before the restored world accepts new Jar entities.
        // The high-water mark includes Warehouse and Workshop accounts from the authoritative checkpoint.
        var jarIdHighWaterMark = allCheckpointJarIds.DefaultIfEmpty(0).Max();
        Resources.Inventory.RestoreJarIdGeneratorAtLeast(jarIdHighWaterMark);
        foreach (var workshop in Roster.Workshops)
            workshop.RestoreJarIdGeneratorAtLeast(jarIdHighWaterMark);

        // 上述验证全部通过后才提交。恢复操作只装载快照，不重新执行资源流转。
        Resources.RestoreRuntimeState(checkpoint.ResourceArea);
        DemandBoard.RestoreRuntimeStateFrom(checkpoint.DemandBoard, vocabulary, DemandIdGenerator);
        Roster.Master.RestoreRuntimeState(checkpoint.MasterWorkshop);
        if (Roster.Slave != null)
            Roster.Slave.RestoreRuntimeState(checkpoint.SlaveWorkshop!);
        ShipLiquidInventory.RestoreFrom(checkpoint.ShipLiquidInventory);
        Reservations.RestoreFromWire(checkpoint.ReservationLeases);
        Admission.Restore(checkpoint.Admission);
        ResourceLedger.RestoreFrom(checkpoint.ResourceLedger);
        Runtime.Clock.RestoreFrom(checkpoint.Clock);
        Runtime.RestoreExecutionSessions(
            checkpoint.Sessions, SolveBook, Admission, Reservations, checkpoint.RuntimeLedgerTick);
    }

    /// <summary>释放求解簿对需求板的事件订阅；不会尝试关闭或回滚世界资源。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        SolveBook.Dispose();
        _disposed = true;
    }
}


/// <summary>
/// 活动执行检查点：保留自机液库、主从工作台动态库存、订单暂存、容量/罐子预留、
/// 资源账本、世界时钟、需求板、配置指纹和执行游标。
/// 这仍是活动执行检查点而非完整世界存档；当前不保存动态 ModifierStack 实例，也不包含可独立重建求解器注册表的插件数据。
/// </summary>
public sealed class WorldV2ExecutionResourceCheckpoint
{
    internal WorldV2ExecutionResourceCheckpoint(
        ResourceAreaRuntimeStateSnapshot resourceArea,
        OrderDemandBoardSnapshot demandBoard,
        string? configurationFingerprint,
        ShipLiquidInventorySnapshot shipLiquidInventory,
        WorkshopRuntimeStateSnapshot masterWorkshop,
        WorkshopRuntimeStateSnapshot? slaveWorkshop,
        WorldOrderExecutionAdmissionSnapshot admission,
        WorldResourceReservationBookSnapshot reservations,
        WorldResourceReservationBookWireDto reservationLeases,
        ResourceLedgerSnapshot resourceLedger,
        WorldClockSnapshot clock,
        int runtimeLedgerTick,
        IEnumerable<string> shapes,
        IEnumerable<string> liquids,
        IReadOnlyDictionary<string, LiquidPolarity> liquidPolarities,
        IEnumerable<WorldOrderExecutionSessionProgressSnapshot> sessions,
        WorldInstanceId? worldInstanceId = null,
        WorldV2OwnerIdentityMap? ownerIdentities = null)
    {
        if (worldInstanceId.HasValue && !worldInstanceId.Value.IsValid)
            throw new ArgumentException("检查点 WorldInstanceId 无效。", nameof(worldInstanceId));
        WorldInstanceId = worldInstanceId;
        if (ownerIdentities != null)
        {
            if (!worldInstanceId.HasValue)
                throw new ArgumentException("Owner identity map requires an explicit WorldInstanceId.", nameof(ownerIdentities));
            ownerIdentities.ValidateForWorld(worldInstanceId.Value);
        }
        OwnerIdentities = ownerIdentities;
        ResourceArea = resourceArea;
        DemandBoard = demandBoard;
        ConfigurationFingerprint = configurationFingerprint;
        ShipLiquidInventory = shipLiquidInventory;
        MasterWorkshop = masterWorkshop;
        SlaveWorkshop = slaveWorkshop;
        Admission = admission;
        Reservations = reservations;
        ReservationLeases = reservationLeases ?? throw new ArgumentNullException(nameof(reservationLeases));
        ReservationLeases.Validate();
        ResourceLedger = resourceLedger;
        Clock = clock;
        RuntimeLedgerTick = runtimeLedgerTick;
        var shapeValues = shapes.ToArray();
        var liquidValues = liquids.ToArray();
        if (shapeValues.Length == 0 || liquidValues.Length != 2 ||
            shapeValues.Any(string.IsNullOrWhiteSpace) || liquidValues.Any(string.IsNullOrWhiteSpace) ||
            shapeValues.Distinct(StringComparer.Ordinal).Count() != shapeValues.Length ||
            liquidValues.Distinct(StringComparer.Ordinal).Count() != liquidValues.Length)
            throw new ArgumentException("检查点形状或液体词表无效");
        Shapes = Array.AsReadOnly(shapeValues);
        Liquids = Array.AsReadOnly(liquidValues);
        if (liquidPolarities == null || liquidPolarities.Count != 2 ||
            liquidValues.Any(liquid => !liquidPolarities.ContainsKey(liquid)) ||
            liquidPolarities.Keys.Any(key => !liquidValues.Contains(key, StringComparer.Ordinal)) ||
            liquidPolarities.Values.Count(value => value == LiquidPolarity.Cathode) != 1 ||
            liquidPolarities.Values.Count(value => value == LiquidPolarity.Anode) != 1)
            throw new ArgumentException("检查点液体极性映射必须完整且恰好包含一个 Cathode 和一个 Anode");
        LiquidPolarities = new ReadOnlyDictionary<string, LiquidPolarity>(
            liquidPolarities.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
        Sessions = Array.AsReadOnly(sessions.OrderBy(item => item.DemandId).ToArray());
    }

    public WorldInstanceId? WorldInstanceId { get; }
    public WorldV2OwnerIdentityMap? OwnerIdentities { get; }
    public ResourceAreaRuntimeStateSnapshot ResourceArea { get; }
    public OrderDemandBoardSnapshot DemandBoard { get; }
    public string? ConfigurationFingerprint { get; }
    public ShipLiquidInventorySnapshot ShipLiquidInventory { get; }
    public WorkshopRuntimeStateSnapshot MasterWorkshop { get; }
    public WorkshopRuntimeStateSnapshot? SlaveWorkshop { get; }
    public WorldOrderExecutionAdmissionSnapshot Admission { get; }
    /// <summary>兼容旧投影；完整恢复以包含 Lease 结算收据的 ReservationLeases 为准。</summary>
    public WorldResourceReservationBookSnapshot Reservations { get; }
    public WorldResourceReservationBookWireDto ReservationLeases { get; }
    public ResourceLedgerSnapshot ResourceLedger { get; }
    public WorldClockSnapshot Clock { get; }
    public int RuntimeLedgerTick { get; }
    public IReadOnlyList<string> Shapes { get; }
    public IReadOnlyList<string> Liquids { get; }
    public IReadOnlyDictionary<string, LiquidPolarity> LiquidPolarities { get; }
    public IReadOnlyList<WorldOrderExecutionSessionProgressSnapshot> Sessions { get; }
}

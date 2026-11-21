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

    public ResourceArea Resources { get; }
    public ShipLiquidInventory? ShipLiquidInventory { get; }
    public ResourceLedger ResourceLedger { get; }
    public WorkshopRoster Roster { get; }
    public OrderDemandBoard DemandBoard { get; }
    public SequentialIdGenerator DemandIdGenerator { get; }
    public string? ConfigurationFingerprint { get; }
    public WorldOrderSolveBook SolveBook { get; }
    public WorldResourceReservationBook Reservations { get; }
    public WorldOrderExecutionAdmission Admission { get; }
    public ResourceWorldRuntime Runtime { get; }
    public WorldOrderScheduler Scheduler { get; }

    private WorldV2Context(
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
        string? configurationFingerprint)
    {
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
        string? configurationFingerprint = null)
    {
        if (resources == null) throw new ArgumentNullException(nameof(resources));
        if (workshops == null) throw new ArgumentNullException(nameof(workshops));
        if (master == null) throw new ArgumentNullException(nameof(master));
        if (solverRegistry == null) throw new ArgumentNullException(nameof(solverRegistry));
        if (logicalTimePerUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalTimePerUnit));
        if (shipLiquidInventory != null && resources.ShipLiquidInventory != null &&
            !ReferenceEquals(shipLiquidInventory, resources.ShipLiquidInventory))
            throw new ArgumentException("Context 与 ResourceArea 必须引用同一个自机液库", nameof(shipLiquidInventory));
        shipLiquidInventory ??= resources.ShipLiquidInventory;

        // 先完成所有可能失败的基础对象校验与组装，再向调用方返回 Context。
        var roster = new WorkshopRoster(workshops, master, slave);
        var board = new OrderDemandBoard(passiveFulfillmentPolicy, activeFulfillmentPolicy);
        var demandIdGenerator = new SequentialIdGenerator();
        var solveBook = new WorldOrderSolveBook(board);
        var reservations = new WorldResourceReservationBook();
        var resourceLedger = new ResourceLedger();
        if (shipLiquidInventory != null)
        {
            foreach (var item in shipLiquidInventory.Balances.OrderBy(item => item.Key, StringComparer.Ordinal))
                resourceLedger.Record(0, ResourceCauses.Stock, ResourceKinds.Liquid,
                    item.Key, item.Value, from: "关卡初始库存", to: "自机液库");
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
                    targetWorkshop, resources.LiquidProductionPriority));
        var scheduler = new WorldOrderScheduler(
            runtime, board, solveBook, solverRegistry, admission, roster, logicalTimePerUnit);

        return new WorldV2Context(
            resources, shipLiquidInventory, resourceLedger, roster, board, demandIdGenerator, solveBook, reservations, admission, runtime, scheduler, configurationFingerprint);
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
            ResourceLedger.Capture(),
            Runtime.Clock.Capture(),
            Runtime.LedgerTick,
            Runtime.ActiveExecutionSessions.Select(session => session.CaptureProgress()));
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
        if (!string.Equals(ConfigurationFingerprint, checkpoint.ConfigurationFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("检查点配置指纹与当前世界不一致，拒绝恢复。");
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
        var vocabulary = new Vocabulary(
            Resources.Producers.Generators.Keys,
            Resources.Inventory.LiquidWarehouse.Keys);
        var stagedDemandIds = new SequentialIdGenerator();
        var stagedBoard = OrderDemandBoard.RestoreRuntimeState(
            checkpoint.DemandBoard, vocabulary, stagedDemandIds);
        var stagedSolveBook = new WorldOrderSolveBook(stagedBoard);
        var stagedReservations = WorldResourceReservationBook.Restore(checkpoint.Reservations);
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

        // 上述验证全部通过后才提交。恢复操作只装载快照，不重新执行资源流转。
        Resources.RestoreRuntimeState(checkpoint.ResourceArea);
        DemandBoard.RestoreRuntimeStateFrom(checkpoint.DemandBoard, vocabulary, DemandIdGenerator);
        Roster.Master.RestoreRuntimeState(checkpoint.MasterWorkshop);
        if (Roster.Slave != null)
            Roster.Slave.RestoreRuntimeState(checkpoint.SlaveWorkshop!);
        ShipLiquidInventory.RestoreFrom(checkpoint.ShipLiquidInventory);
        Reservations.RestoreFrom(checkpoint.Reservations);
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
        ResourceLedgerSnapshot resourceLedger,
        WorldClockSnapshot clock,
        int runtimeLedgerTick,
        IEnumerable<WorldOrderExecutionSessionProgressSnapshot> sessions)
    {
        ResourceArea = resourceArea;
        DemandBoard = demandBoard;
        ConfigurationFingerprint = configurationFingerprint;
        ShipLiquidInventory = shipLiquidInventory;
        MasterWorkshop = masterWorkshop;
        SlaveWorkshop = slaveWorkshop;
        Admission = admission;
        Reservations = reservations;
        ResourceLedger = resourceLedger;
        Clock = clock;
        RuntimeLedgerTick = runtimeLedgerTick;
        Sessions = Array.AsReadOnly(sessions.OrderBy(item => item.DemandId).ToArray());
    }

    public ResourceAreaRuntimeStateSnapshot ResourceArea { get; }
    public OrderDemandBoardSnapshot DemandBoard { get; }
    public string? ConfigurationFingerprint { get; }
    public ShipLiquidInventorySnapshot ShipLiquidInventory { get; }
    public WorkshopRuntimeStateSnapshot MasterWorkshop { get; }
    public WorkshopRuntimeStateSnapshot? SlaveWorkshop { get; }
    public WorldOrderExecutionAdmissionSnapshot Admission { get; }
    public WorldResourceReservationBookSnapshot Reservations { get; }
    public ResourceLedgerSnapshot ResourceLedger { get; }
    public WorldClockSnapshot Clock { get; }
    public int RuntimeLedgerTick { get; }
    public IReadOnlyList<WorldOrderExecutionSessionProgressSnapshot> Sessions { get; }
}

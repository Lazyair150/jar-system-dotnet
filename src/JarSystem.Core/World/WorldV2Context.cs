using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
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
        WorldOrderSolveBook solveBook,
        WorldResourceReservationBook reservations,
        WorldOrderExecutionAdmission admission,
        ResourceWorldRuntime runtime,
        WorldOrderScheduler scheduler)
    {
        Resources = resources;
        ShipLiquidInventory = shipLiquidInventory;
        ResourceLedger = resourceLedger;
        Roster = roster;
        DemandBoard = demandBoard;
        SolveBook = solveBook;
        Reservations = reservations;
        Admission = admission;
        Runtime = runtime;
        Scheduler = scheduler;
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
        ShipLiquidInventory? shipLiquidInventory = null)
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
            resourceLedger: resourceLedger);
        var scheduler = new WorldOrderScheduler(
            runtime, board, solveBook, solverRegistry, admission, roster, logicalTimePerUnit);

        return new WorldV2Context(
            resources, shipLiquidInventory, resourceLedger, roster, board, solveBook, reservations, admission, runtime, scheduler);
    }

    /// <summary>
    /// 捕获活动执行所需的液体资源、资源预留、账本、时钟与会话进度。
    /// 必须在 Host 独占操作内调用。工作台罐子实体快照仍由独立的 Workshop 状态边界负责。
    /// </summary>
    public WorldV2ExecutionResourceCheckpoint CaptureExecutionResourceCheckpoint()
    {
        if (ShipLiquidInventory == null)
            throw new InvalidOperationException("旧版兼容世界没有自机液库，不能捕获新液体资源检查点。");
        return new WorldV2ExecutionResourceCheckpoint(
            ShipLiquidInventory.Capture(),
            Roster.Master?.CaptureRuntimeState()
                ?? throw new InvalidOperationException("检查点捕获时没有主工作台。"),
            Roster.Slave?.CaptureRuntimeState(),
            Admission.Capture(),
            Reservations.Capture(),
            ResourceLedger.Capture(),
            Runtime.Clock.Capture(),
            Runtime.ActiveExecutionSessions.Select(session => session.CaptureProgress()));
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
/// 资源账本、世界时钟和执行游标。
/// 这不是完整世界存档；恢复执行仍需配套需求板、资源生产器/时钟策略与规则修饰器状态。
/// </summary>
public sealed class WorldV2ExecutionResourceCheckpoint
{
    internal WorldV2ExecutionResourceCheckpoint(
        ShipLiquidInventorySnapshot shipLiquidInventory,
        WorkshopRuntimeStateSnapshot masterWorkshop,
        WorkshopRuntimeStateSnapshot? slaveWorkshop,
        WorldOrderExecutionAdmissionSnapshot admission,
        WorldResourceReservationBookSnapshot reservations,
        ResourceLedgerSnapshot resourceLedger,
        WorldClockSnapshot clock,
        IEnumerable<WorldOrderExecutionSessionProgressSnapshot> sessions)
    {
        ShipLiquidInventory = shipLiquidInventory;
        MasterWorkshop = masterWorkshop;
        SlaveWorkshop = slaveWorkshop;
        Admission = admission;
        Reservations = reservations;
        ResourceLedger = resourceLedger;
        Clock = clock;
        Sessions = Array.AsReadOnly(sessions.OrderBy(item => item.DemandId).ToArray());
    }

    public ShipLiquidInventorySnapshot ShipLiquidInventory { get; }
    public WorkshopRuntimeStateSnapshot MasterWorkshop { get; }
    public WorkshopRuntimeStateSnapshot? SlaveWorkshop { get; }
    public WorldOrderExecutionAdmissionSnapshot Admission { get; }
    public WorldResourceReservationBookSnapshot Reservations { get; }
    public ResourceLedgerSnapshot ResourceLedger { get; }
    public WorldClockSnapshot Clock { get; }
    public IReadOnlyList<WorldOrderExecutionSessionProgressSnapshot> Sessions { get; }
}

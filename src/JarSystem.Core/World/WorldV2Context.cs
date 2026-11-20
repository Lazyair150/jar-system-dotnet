using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using JarSystem.Core.World.Demands;
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
    public WorkshopRoster Roster { get; }
    public OrderDemandBoard DemandBoard { get; }
    public WorldOrderSolveBook SolveBook { get; }
    public WorldResourceReservationBook Reservations { get; }
    public WorldOrderExecutionAdmission Admission { get; }
    public ResourceWorldRuntime Runtime { get; }
    public WorldOrderScheduler Scheduler { get; }

    private WorldV2Context(
        ResourceArea resources,
        WorkshopRoster roster,
        OrderDemandBoard demandBoard,
        WorldOrderSolveBook solveBook,
        WorldResourceReservationBook reservations,
        WorldOrderExecutionAdmission admission,
        ResourceWorldRuntime runtime,
        WorldOrderScheduler scheduler)
    {
        Resources = resources;
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
        WorldClock? clock = null)
    {
        if (resources == null) throw new ArgumentNullException(nameof(resources));
        if (workshops == null) throw new ArgumentNullException(nameof(workshops));
        if (master == null) throw new ArgumentNullException(nameof(master));
        if (solverRegistry == null) throw new ArgumentNullException(nameof(solverRegistry));
        if (logicalTimePerUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalTimePerUnit));

        // 先完成所有可能失败的基础对象校验与组装，再向调用方返回 Context。
        var roster = new WorkshopRoster(workshops, master, slave);
        var board = new OrderDemandBoard(passiveFulfillmentPolicy, activeFulfillmentPolicy);
        var solveBook = new WorldOrderSolveBook(board);
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, solveBook, roster, reservations);
        var runtime = new ResourceWorldRuntime(
            resources, clock: clock, roster: roster, demandBoard: board);
        var scheduler = new WorldOrderScheduler(
            runtime, board, solveBook, solverRegistry, admission, roster, logicalTimePerUnit);

        return new WorldV2Context(
            resources, roster, board, solveBook, reservations, admission, runtime, scheduler);
    }

    /// <summary>释放求解簿对需求板的事件订阅；不会尝试关闭或回滚世界资源。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        SolveBook.Dispose();
        _disposed = true;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 把需求板、求解簿、求解器、执行准入与 Runtime 串成一次调度过程。
/// 本类不拥有世界时间，也不改变订单领域规则；宿主应在世界推进后调用 RunSchedulingPass。
/// </summary>
public sealed class WorldOrderScheduler
{
    private readonly ResourceWorldRuntime _runtime;
    private readonly OrderDemandBoard _board;
    private readonly WorldOrderSolveBook _solveBook;
    private readonly WorldOrderSolverRegistry _solverRegistry;
    private readonly WorldOrderExecutionAdmission _admission;
    private readonly WorkshopRoster _roster;
    private readonly long _logicalTimePerUnit;
    private bool _running;

    public WorldOrderScheduler(
        ResourceWorldRuntime runtime,
        OrderDemandBoard board,
        WorldOrderSolveBook solveBook,
        WorldOrderSolverRegistry solverRegistry,
        WorldOrderExecutionAdmission admission,
        WorkshopRoster roster,
        long logicalTimePerUnit)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _solveBook = solveBook ?? throw new ArgumentNullException(nameof(solveBook));
        _solverRegistry = solverRegistry ?? throw new ArgumentNullException(nameof(solverRegistry));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));

        if (logicalTimePerUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalTimePerUnit));
        if (!ReferenceEquals(runtime.DemandBoard, board) ||
            !ReferenceEquals(runtime.Roster, roster) ||
            !admission.IsBoundTo(board, solveBook, roster))
            throw new ArgumentException("调度器的 Runtime、需求板、求解簿、准入器与岗位必须属于同一世界");

        _logicalTimePerUnit = logicalTimePerUnit;
    }

    /// <summary>
    /// 对所有已登记订单刷新求解状态，并按被动区域当前顺序尝试自动启动。
    /// 主动区域只刷新求解结果，不会因本方法被自动启动。
    /// </summary>
    public WorldOrderSchedulingPassResult RunSchedulingPass()
    {
        if (_runtime.IsFaulted)
            throw new InvalidOperationException("世界运行时已故障，不能执行订单调度：" + _runtime.FaultReason);
        if (_running)
            throw new InvalidOperationException("订单调度过程不可重入");

        _running = true;
        try
        {
            var initialDemands = _board.AllDemands.ToArray();
            var decisions = new Dictionary<int, WorldOrderSchedulingDecision>();
            var initialAreas = initialDemands.ToDictionary(
                demand => demand.Id, demand => AreaOf(demand.Id));
            var started = new List<int>();

            // 所有订单先共享同一份只读世界快照。Evaluate 会复用同版本缓存，
            // 只对 Dirty、NotReady 到期或世界版本变化的订单真正调用求解器。
            var sharedSnapshot = WorldSnapshotFactory.Capture(_roster, _admission.ShipLiquidInventory);
            foreach (var demand in initialDemands)
            {
                var record = _solveBook.Evaluate(demand.Id, sharedSnapshot, _solverRegistry);
                decisions[demand.Id] = new WorldOrderSchedulingDecision(
                    demand.Id, AreaOf(demand.Id), record.State, false,
                    record.StateReason ?? record.DirtyReason);
            }

            // 被动订单的列表顺序就是同一调度轮次中的竞争顺序。
            // 每次启动后都重新捕获快照：会话构造可能立即执行零时间 MATCH 并改变罐子标记。
            var currentSnapshot = sharedSnapshot;
            var refreshSnapshot = false;
            foreach (var demand in _board.Passive.Demands.ToArray())
            {
                if (_runtime.IsFaulted) break;
                if (!ReferenceEquals(_board.Find(demand.Id), demand)) continue;

                if (refreshSnapshot)
                {
                    currentSnapshot = WorldSnapshotFactory.Capture(_roster, _admission.ShipLiquidInventory);
                    refreshSnapshot = false;
                }
                var record = _solveBook.Evaluate(demand.Id, currentSnapshot, _solverRegistry);
                if (record.State != WorldOrderSolveState.Current ||
                    record.Result == null || !record.Result.IsFeasible)
                {
                    decisions[demand.Id] = new WorldOrderSchedulingDecision(
                        demand.Id, DemandAreaKind.Passive, record.State, false,
                        record.StateReason ?? record.DirtyReason ?? record.Result?.Reason ?? "订单当前不可执行");
                    continue;
                }

                var result = _runtime.TryStartExecutionSession(
                    demand.Id, _solveBook, _admission, _logicalTimePerUnit);
                if (result.Success)
                {
                    started.Add(demand.Id);
                    // 会话构造可能立即执行 MATCH；下一张被动订单必须针对新世界版本求解。
                    refreshSnapshot = true;
                    decisions[demand.Id] = new WorldOrderSchedulingDecision(
                        demand.Id, DemandAreaKind.Passive, record.State, true, null);
                }
                else
                {
                    decisions[demand.Id] = new WorldOrderSchedulingDecision(
                        demand.Id, DemandAreaKind.Passive, record.State, false,
                        result.Reason ?? "执行准入拒绝");
                }
            }

            // 把本轮末尾仍登记的订单记录同步到最终世界版本。被动启动过程中若发生
            // 零时间 MATCH/即时交付，主动订单的早期求解结果也不能被报告为当前结果。
            var finalSnapshot = WorldSnapshotFactory.Capture(_roster, _admission.ShipLiquidInventory);
            foreach (var demand in initialDemands)
            {
                var record = _solveBook.Evaluate(demand.Id, finalSnapshot, _solverRegistry);
                decisions[demand.Id] = new WorldOrderSchedulingDecision(
                    demand.Id, initialAreas[demand.Id], record.State, started.Contains(demand.Id),
                    record.StateReason ?? record.DirtyReason ??
                    (record.Result?.IsFeasible == false ? record.Result?.Reason : null));
            }

            return new WorldOrderSchedulingPassResult(
                initialDemands.Length,
                started.AsReadOnly(),
                initialDemands.Select(demand => decisions[demand.Id]).ToArray());
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// 推进一次世界时间，并在同一宿主调用中立即运行订单调度。
    /// 这只是调用顺序的协调入口：时间仍由 Runtime 推进，订单仍由 RunSchedulingPass 决策。
    /// </summary>
    public WorldOrderScheduledStepResult AdvanceWorldAndSchedule(long deltaWorldTimeMilli)
    {
        if (_running)
            throw new InvalidOperationException("当前正在执行调度过程，不能重入推进世界");

        var worldStep = _runtime.AdvanceWorldTime(deltaWorldTimeMilli);

        // 暂停是世界级冻结：即使宿主继续提交帧/时间片，也不能借此自动求解或启动被动订单。
        // 用户仍可通过显式命令启动主动订单；其执行会等到世界恢复后才消耗逻辑时间。
        if (_runtime.Clock.IsPaused)
        {
            var pausedScheduling = new WorldOrderSchedulingPassResult(
                0, Array.Empty<int>(), Array.Empty<WorldOrderSchedulingDecision>());
            return new WorldOrderScheduledStepResult(worldStep, pausedScheduling);
        }

        var scheduling = RunSchedulingPass();
        return new WorldOrderScheduledStepResult(worldStep, scheduling);
    }

    /// <summary>
    /// 用户显式启动主动区域订单。此入口只接受 Active 订单，不替用户选择主动订单。
    /// 求解与准入仍使用和被动订单相同的快照、计划与资源规则。
    /// </summary>
    public WorldOrderExecutionStartResult TryStartActiveDemand(int demandId)
    {
        if (_runtime.IsFaulted)
            return WorldOrderExecutionStartResult.Rejected("世界运行时已故障，不能启动订单：" + _runtime.FaultReason);
        if (_running)
            return WorldOrderExecutionStartResult.Rejected("当前正在执行调度过程，不能重入启动主动订单");

        if (!_board.Active.Demands.Any(demand => demand.Id == demandId))
            return WorldOrderExecutionStartResult.Rejected("指定订单不属于主动需求区域");

        var snapshot = WorldSnapshotFactory.Capture(_roster, _admission.ShipLiquidInventory);
        _solveBook.Evaluate(demandId, snapshot, _solverRegistry);
        return _runtime.TryStartExecutionSession(
            demandId, _solveBook, _admission, _logicalTimePerUnit);
    }

    private DemandAreaKind AreaOf(int demandId) =>
        _board.Passive.Demands.Any(demand => demand.Id == demandId)
            ? DemandAreaKind.Passive
            : DemandAreaKind.Active;
}

/// <summary>一次世界推进及其紧随其后的调度结果。</summary>
public sealed record WorldOrderScheduledStepResult(
    ResourceWorldStep WorldStep,
    WorldOrderSchedulingPassResult Scheduling);

/// <summary>一次调度轮次的可观测结果；不代表会话已完成。</summary>
public sealed record WorldOrderSchedulingPassResult(
    int VisitedDemandCount,
    IReadOnlyList<int> StartedDemandIds,
    IReadOnlyList<WorldOrderSchedulingDecision> Decisions);

/// <summary>调度器对一张订单在本轮中的处理结果。</summary>
public sealed record WorldOrderSchedulingDecision(
    int DemandId,
    DemandAreaKind Area,
    WorldOrderSolveState SolveState,
    bool Started,
    string? Reason);

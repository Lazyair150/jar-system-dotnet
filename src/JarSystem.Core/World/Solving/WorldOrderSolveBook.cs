using System;
using System.Collections.Generic;
using JarSystem.Core.World.Demands;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 管理需求板中每张订单的求解结论缓存与失效状态。
/// 它不决定何时启动执行，也不预留资源；调度器只读取当前快照版本下的有效结论。
/// </summary>
public sealed class WorldOrderSolveBook : IDisposable
{
    private readonly OrderDemandBoard _board;
    private readonly Dictionary<int, WorldOrderSolveRecord> _records = new();
    private bool _disposed;

    public WorldOrderSolveBook(OrderDemandBoard board)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        foreach (var demand in board.AllDemands)
            _records[demand.Id] = WorldOrderSolveRecord.Dirty(demand.Id, "需求板接入求解簿");
        _board.Changed += OnBoardChanged;
    }

    /// <summary>返回指定订单的当前记录；未登记过时返回 null。</summary>
    public WorldOrderSolveRecord? Find(int demandId) =>
        _records.TryGetValue(demandId, out var record) ? record : null;

    /// <summary>
    /// 仅当订单仍登记、CD 已结束且求解结论匹配当前快照版本时返回结论。
    /// 若版本变化，会将记录转为 Dirty 并返回 null。
    /// </summary>
    public WorldOrderSolveResult? GetCurrentResult(int demandId, WorldSnapshot snapshot)
    {
        ThrowIfDisposed();
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

        var demand = _board.Find(demandId);
        if (demand == null)
        {
            SetDetached(demandId);
            return null;
        }

        if (!demand.ReadyForSolve)
        {
            SetNotReady(demandId);
            return null;
        }

        if (!_records.TryGetValue(demandId, out var record) ||
            record.State != WorldOrderSolveState.Current ||
            record.Result == null)
            return null;

        if (!string.Equals(record.Result.WorldVersion, snapshot.Version, StringComparison.Ordinal))
        {
            _records[demandId] = WorldOrderSolveRecord.Dirty(
                demandId, "世界求解输入版本已变化");
            return null;
        }

        return record.Result;
    }

    /// <summary>
    /// 对指定订单求解或复用当前版本的缓存结果。
    /// AwaitingRearm 的持续订单不会被自动再次求解，必须由上层策略显式 MarkDirty。
    /// </summary>
    public WorldOrderSolveRecord Evaluate(
        int demandId,
        WorldSnapshot snapshot,
        WorldOrderSolverRegistry registry)
    {
        ThrowIfDisposed();
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (registry == null) throw new ArgumentNullException(nameof(registry));

        var demand = _board.Find(demandId);
        if (demand == null)
        {
            SetDetached(demandId);
            return _records[demandId];
        }

        if (!_records.TryGetValue(demandId, out var record))
        {
            record = WorldOrderSolveRecord.Dirty(demandId, "订单首次进入求解簿");
            _records[demandId] = record;
        }

        if (record.State == WorldOrderSolveState.AwaitingRearm)
            return record;

        if (!demand.ReadyForSolve)
        {
            SetNotReady(demandId);
            return _records[demandId];
        }

        if (record.State == WorldOrderSolveState.Current &&
            record.Result != null &&
            string.Equals(record.Result.WorldVersion, snapshot.Version, StringComparison.Ordinal))
            return record;

        if (record.State == WorldOrderSolveState.Current)
        {
            record = WorldOrderSolveRecord.Dirty(demandId, "世界求解输入版本已变化");
            _records[demandId] = record;
        }

        var result = registry.Solve(demand, snapshot);
        var state = result.Status == WorldOrderSolveStatus.NotReady
            ? WorldOrderSolveState.NotReady
            : WorldOrderSolveState.Current;
        var updated = new WorldOrderSolveRecord(
            demandId, state, result, null,
            state == WorldOrderSolveState.NotReady ? result.Reason : null);
        _records[demandId] = updated;
        return updated;
    }

    /// <summary>
    /// 显式使一张仍登记的订单重新具备求解资格。
    /// 对持续订单成功后的重新求解，必须由上层策略明确调用，而不是自动循环。
    /// </summary>
    public bool MarkDirty(int demandId, string reason)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("失效原因不能为空", nameof(reason));
        if (_board.Find(demandId) == null)
        {
            SetDetached(demandId);
            return false;
        }

        _records[demandId] = WorldOrderSolveRecord.Dirty(demandId, reason);
        return true;
    }

    /// <summary>将当前已登记订单全部标记为 Dirty；用于外部确认的全局求解条件变化。</summary>
    public void MarkAllDirty(string reason)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("失效原因不能为空", nameof(reason));

        foreach (var demand in _board.AllDemands)
        {
            if (_records.TryGetValue(demand.Id, out var current) &&
                current.State == WorldOrderSolveState.AwaitingRearm)
                continue;

            _records[demand.Id] = WorldOrderSolveRecord.Dirty(demand.Id, reason);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _board.Changed -= OnBoardChanged;
        _disposed = true;
    }

    private void OnBoardChanged(OrderDemandBoardChange change)
    {
        switch (change.Kind)
        {
            case OrderDemandBoardChangeKind.Added:
                _records[change.DemandId] =
                    WorldOrderSolveRecord.Dirty(change.DemandId, "订单加入需求区域");
                break;

            case OrderDemandBoardChangeKind.Removed:
                SetDetached(change.DemandId);
                break;

            case OrderDemandBoardChangeKind.Moved:
                if (!_records.TryGetValue(change.DemandId, out var movedRecord) ||
                    movedRecord.State != WorldOrderSolveState.AwaitingRearm)
                    _records[change.DemandId] =
                        WorldOrderSolveRecord.Dirty(change.DemandId, "订单在主动/被动区域之间移动");
                break;

            case OrderDemandBoardChangeKind.CooldownStarted:
                _records[change.DemandId] = new WorldOrderSolveRecord(
                    change.DemandId, WorldOrderSolveState.NotReady, null, null,
                    "订单因执行中断进入 CD");
                break;

            case OrderDemandBoardChangeKind.CooldownElapsed:
                if (!_records.TryGetValue(change.DemandId, out var cooledRecord) ||
                    cooledRecord.State != WorldOrderSolveState.AwaitingRearm)
                    _records[change.DemandId] =
                        WorldOrderSolveRecord.Dirty(change.DemandId, "订单 CD 结束");
                break;

            case OrderDemandBoardChangeKind.FulfilledRetained:
                _records[change.DemandId] = new WorldOrderSolveRecord(
                    change.DemandId, WorldOrderSolveState.AwaitingRearm, null, null,
                    "持续订单已完成；等待上层策略决定何时重新参与求解");
                break;

            case OrderDemandBoardChangeKind.PassiveOrderChanged:
                // 顺序影响被动执行调度，不改变订单在同一世界快照上的求解结论。
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    private void SetDetached(int demandId) =>
        _records[demandId] = new WorldOrderSolveRecord(
            demandId, WorldOrderSolveState.Detached, null, null, "订单已不在需求板中");

    private void SetNotReady(int demandId) =>
        _records[demandId] = new WorldOrderSolveRecord(
            demandId, WorldOrderSolveState.NotReady, null, null, "订单仍处于冷却时间");

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorldOrderSolveBook));
    }
}

public sealed record WorldOrderSolveRecord(
    int DemandId,
    WorldOrderSolveState State,
    WorldOrderSolveResult? Result,
    string? DirtyReason,
    string? StateReason)
{
    internal static WorldOrderSolveRecord Dirty(int demandId, string reason) =>
        new(demandId, WorldOrderSolveState.Dirty, null, reason, null);
}

public enum WorldOrderSolveState
{
    Dirty,
    Current,
    NotReady,
    Detached,
    AwaitingRearm
}

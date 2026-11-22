using System;
using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.World.Demands;
using JarSystem.Core.Orders;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 管理需求板中每张订单的求解结论缓存与失效状态。
/// 它不决定何时启动执行，也不预留资源；调度器只读取当前快照版本下的有效结论。
/// </summary>
public sealed class WorldOrderSolveBook : IDisposable
{
    private readonly OrderDemandBoard _board;
    private readonly Dictionary<int, WorldOrderSolveRecord> _records = new();
    private readonly Dictionary<int, WorldOrderSymbolicSolutionSet> _symbolicSolutions = new();
    public WorldOrderRuleCandidateBook RuleCandidates { get; } = new();
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
    /// 只在规则集、工作台 Profile 与订单规格均匹配时返回符号候选集。
    /// ResourceVersion 不参与此检查：资源变化不会使规则候选失效。
    /// </summary>
    public WorldOrderSymbolicSolutionSet? GetSymbolicSolutions(
        int demandId,
        WorldSnapshot snapshot)
    {
        ThrowIfDisposed();
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var demand = _board.Find(demandId);
        if (demand?.Specification is not JarOrderSpecification specification)
            return null;
        if (!_symbolicSolutions.TryGetValue(demandId, out var candidates))
            return null;

        var specificationVersion = WorldOrderRuleCandidateCatalog.Fingerprint(specification);
        return string.Equals(candidates.RuleSetVersion, snapshot.RuleSetVersion, StringComparison.Ordinal) &&
               string.Equals(candidates.WorkshopProfileVersion, snapshot.WorkshopProfileVersion, StringComparison.Ordinal) &&
               string.Equals(candidates.SpecificationVersion, specificationVersion, StringComparison.Ordinal)
            ? candidates
            : null;
    }

    /// <summary>
    /// 仅当订单仍登记且 CD 已结束时返回当前结论。
    /// 若完整世界版本变化，会先尝试规则/Profile/规格不变前提下的资源计划重绑定；
    /// 只有重绑定验证失败才将记录转为 Dirty 并返回 null。
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
            if (TryRebind(record, demand, snapshot, out var rebound, out var reason))
            {
                _records[demandId] = rebound!;
                return rebound!.Result;
            }

            _records[demandId] = WorldOrderSolveRecord.Dirty(demandId, reason);
            return null;
        }

        return record.Result;
    }

    /// <summary>
    /// 对指定订单求解或复用当前版本的缓存结果。
    /// 规则/Profile/规格候选跨资源版本复用；具体计划只有通过资源清单重验证后才可重绑定。
    /// 订单自身 CD 是求解资格的硬门槛；任何 Dirty/invalidation 都不能绕过它。
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

        if (!demand.ReadyForSolve)
        {
            SetNotReady(demandId);
            return _records[demandId];
        }

        CacheRuleCandidates(demand, snapshot);

        if (record.State == WorldOrderSolveState.Current &&
            record.Result != null &&
            string.Equals(record.Result.WorldVersion, snapshot.Version, StringComparison.Ordinal))
            return record;

        if (record.State == WorldOrderSolveState.Current)
        {
            if (TryRebind(record, demand, snapshot, out var rebound, out var reason))
            {
                _records[demandId] = rebound!;
                return rebound!;
            }

            record = WorldOrderSolveRecord.Dirty(demandId, reason);
            _records[demandId] = record;
        }

        if (record.State == WorldOrderSolveState.Current)
        {
            record = WorldOrderSolveRecord.Dirty(demandId, "世界求解输入版本已变化");
            _records[demandId] = record;
        }

        var result = registry.Solve(demand, snapshot, RuleCandidates);
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
    /// 使一张仍登记的订单失效。若订单仍处于 CD，则保持 NotReady，不能绕过冷却。
    /// 持续订单在 CD 到期后由需求板事件自动重新标记为 Dirty。
    /// </summary>
    public bool MarkDirty(int demandId, string reason)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("失效原因不能为空", nameof(reason));
        var demand = _board.Find(demandId);
        if (demand == null)
        {
            SetDetached(demandId);
            return false;
        }

        if (!demand.ReadyForSolve)
            SetNotReady(demandId);
        else
            _records[demandId] = WorldOrderSolveRecord.Dirty(demandId, reason);
        return true;
    }

    /// <summary>使当前已登记订单失效；仍处于 CD 的订单保持 NotReady。</summary>
    public void MarkAllDirty(string reason)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("失效原因不能为空", nameof(reason));

        foreach (var demand in _board.AllDemands)
        {
            if (!demand.ReadyForSolve)
                SetNotReady(demand.Id);
            else
                _records[demand.Id] = WorldOrderSolveRecord.Dirty(demand.Id, reason);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _board.Changed -= OnBoardChanged;
        RuleCandidates.Clear();
        _symbolicSolutions.Clear();
        _disposed = true;
    }

    private static bool TryRebind(
        WorldOrderSolveRecord record,
        OrderDemand demand,
        WorldSnapshot snapshot,
        out WorldOrderSolveRecord? reboundRecord,
        out string reason)
    {
        reboundRecord = null;
        if (record.Result is not { IsFeasible: true, Plan: not null } previous)
        {
            reason = "旧结果没有可重绑定的可行计划";
            return false;
        }
        if (string.IsNullOrWhiteSpace(previous.RuleSetVersion) ||
            !string.Equals(previous.RuleSetVersion, snapshot.RuleSetVersion, StringComparison.Ordinal) ||
            !string.Equals(previous.WorkshopProfileVersion, snapshot.WorkshopProfileVersion, StringComparison.Ordinal))
        {
            reason = "规则集或工作台 Profile 已变化";
            return false;
        }
        if (demand.Specification is not JarOrderSpecification specification ||
            !string.Equals(previous.SpecificationVersion,
                WorldOrderRuleCandidateCatalog.Fingerprint(specification), StringComparison.Ordinal))
        {
            reason = "订单规格已变化";
            return false;
        }

        try
        {
            // 先验证旧计划引用的 Jar 初态、液体最低供给与容量仍成立。
            // 只有具体资源绑定失效才退回完整求解；规则/Profile/规格变化则绝不复用。
            _ = WorldOrderResourceManifest.CreateForResourceRebind(previous, snapshot);
            var rebound = previous.RebindTo(snapshot);
            reboundRecord = new WorldOrderSolveRecord(
                demand.Id, WorldOrderSolveState.Current, rebound, null, null);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or
                                   OverflowException or KeyNotFoundException)
        {
            reason = "资源变化使旧计划无法重绑定：" + ex.Message;
            return false;
        }
    }

    private void CacheRuleCandidates(OrderDemand demand, WorldSnapshot snapshot)
    {
        if (demand.Specification is not JarOrderSpecification specification ||
            snapshot.Master == null ||
            (specification.Type == Constants.OrderType.B && snapshot.Slave == null))
        {
            _symbolicSolutions.Remove(demand.Id);
            return;
        }

        var profiles = new List<WorldOrderRuleCandidateProfile>
        {
            RuleCandidates.GetOrBuild(snapshot, snapshot.Master, "Master", specification)
        };
        if (specification.Type == Constants.OrderType.B)
            profiles.Add(RuleCandidates.GetOrBuild(snapshot, snapshot.Slave!, "Slave", specification));

        _symbolicSolutions[demand.Id] = RuleCandidates.GetOrBuildSolutions(profiles);
    }

    private void OnBoardChanged(OrderDemandBoardChange change)
    {
        switch (change.Kind)
        {
            case OrderDemandBoardChangeKind.Added:
                _symbolicSolutions.Remove(change.DemandId);
                _records[change.DemandId] =
                    WorldOrderSolveRecord.Dirty(change.DemandId, "订单加入需求区域");
                break;

            case OrderDemandBoardChangeKind.Removed:
                _symbolicSolutions.Remove(change.DemandId);
                SetDetached(change.DemandId);
                break;

            case OrderDemandBoardChangeKind.Moved:
            {
                var movedDemand = _board.Find(change.DemandId);
                if (movedDemand == null)
                    SetDetached(change.DemandId);
                else if (!movedDemand.ReadyForSolve)
                    SetNotReady(change.DemandId);
                else
                    _records[change.DemandId] =
                        WorldOrderSolveRecord.Dirty(change.DemandId, "订单在主动/被动区域之间移动");
                break;
            }

            case OrderDemandBoardChangeKind.CooldownStarted:
                _records[change.DemandId] = new WorldOrderSolveRecord(
                    change.DemandId, WorldOrderSolveState.NotReady, null, null,
                    "订单因执行中断进入 CD");
                break;

            case OrderDemandBoardChangeKind.CooldownElapsed:
            {
                var cooledDemand = _board.Find(change.DemandId);
                if (cooledDemand == null)
                    SetDetached(change.DemandId);
                else if (!cooledDemand.ReadyForSolve)
                    SetNotReady(change.DemandId);
                else
                    _records[change.DemandId] =
                        WorldOrderSolveRecord.Dirty(change.DemandId, "订单 CD 结束");
                break;
            }

            case OrderDemandBoardChangeKind.FulfilledRetained:
            {
                // Board 在发布成功保留事件前已启动订单自身 CD。
                // 有 CD 则 NotReady；CD 为 0 则 Dirty，允许下一轮立即重新求解。
                var fulfilledDemand = _board.Find(change.DemandId);
                if (fulfilledDemand == null)
                    SetDetached(change.DemandId);
                else if (!fulfilledDemand.ReadyForSolve)
                    SetNotReady(change.DemandId);
                else
                    _records[change.DemandId] =
                        WorldOrderSolveRecord.Dirty(change.DemandId, "持续订单完成且 CD 为 0");
                break;
            }

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
    Detached
}

using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.World.Demands;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 订单执行准入：把登记状态、有效求解结果、真实工作台资源与资源预留合并为一个入口。
/// 成功只表示资源已被预留，不代表执行会话已经创建或执行已经完成。
/// </summary>
public sealed class WorldOrderExecutionAdmission
{
    private readonly OrderDemandBoard _board;
    private readonly WorldOrderSolveBook _solveBook;
    private readonly WorkshopRoster _roster;
    private readonly WorldResourceReservationBook _reservations;

    public WorldOrderExecutionAdmission(
        OrderDemandBoard board, WorldOrderSolveBook solveBook,
        WorkshopRoster roster, WorldResourceReservationBook reservations)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _solveBook = solveBook ?? throw new ArgumentNullException(nameof(solveBook));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
    }

    internal bool IsBoundTo(
        OrderDemandBoard board, WorldOrderSolveBook solveBook, WorkshopRoster roster) =>
        ReferenceEquals(_board, board) &&
        ReferenceEquals(_solveBook, solveBook) &&
        ReferenceEquals(_roster, roster);

    /// <summary>
    /// 尝试准入一张订单。应在世界逻辑调度线程内同步调用，避免检查与预留之间插入世界变更。
    /// </summary>
    public WorldOrderExecutionAdmissionResult TryAdmit(int demandId)
    {
        var demand = _board.Find(demandId);
        if (demand == null)
            return WorldOrderExecutionAdmissionResult.Rejected("订单已不在需求板中");
        if (!demand.ReadyForSolve)
            return WorldOrderExecutionAdmissionResult.Rejected("订单仍处于 CD");

        var record = _solveBook.Find(demandId);
        if (record == null || record.State != WorldOrderSolveState.Current || record.Result == null)
        {
            var reason = record?.State switch
            {
                WorldOrderSolveState.AwaitingRearm => "持续订单尚未由上层策略重新唤醒",
                WorldOrderSolveState.NotReady => "订单尚未具备求解资格",
                WorldOrderSolveState.Detached => "订单已脱离需求板",
                WorldOrderSolveState.Dirty => "订单求解结果已失效，必须重新求解",
                _ => "订单没有当前有效的求解结果"
            };
            return WorldOrderExecutionAdmissionResult.Rejected(reason);
        }

        var snapshot = WorldSnapshotFactory.Capture(_roster);
        var result = _solveBook.GetCurrentResult(demandId, snapshot);
        if (result == null)
            return WorldOrderExecutionAdmissionResult.Rejected("求解结果不存在或与当前世界版本不一致");
        if (result.DemandId != demandId || !result.IsFeasible || result.Plan == null)
            return WorldOrderExecutionAdmissionResult.Rejected("当前求解结果不是该订单的有效可行计划");

        WorldOrderResourceManifest manifest;
        try
        {
            manifest = WorldOrderResourceManifest.Create(result, snapshot);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            return WorldOrderExecutionAdmissionResult.Rejected("资源清单校验失败：" + ex.Message);
        }

        var master = _roster.Master;
        if (master == null)
            return WorldOrderExecutionAdmissionResult.Rejected("当前没有主工作台");

        var liveJars = new Dictionary<int, Jar>();
        foreach (var jar in master.Jars)
        {
            if (jar.Id is not > 0)
                return WorldOrderExecutionAdmissionResult.Rejected("主工作台存在没有有效 ID 的罐子");
            if (!liveJars.TryAdd(jar.Id.Value, jar))
                return WorldOrderExecutionAdmissionResult.Rejected("主工作台存在重复罐子 ID");
        }

        // 所有计划引用的罐子在准入时仍必须位于主台；B 类转移发生在准入之后。
        foreach (var jarId in manifest.LockedJarIds)
        {
            if (!liveJars.ContainsKey(jarId))
                return WorldOrderExecutionAdmissionResult.Rejected($"罐子 #{jarId} 已不在主工作台");
        }

        if (manifest.IsPartitioned)
        {
            var slave = _roster.Slave;
            if (slave == null)
                return WorldOrderExecutionAdmissionResult.Rejected("B 类订单没有从工作台");
            if (slave.HasResources)
                return WorldOrderExecutionAdmissionResult.Rejected("B 类订单准入时从工作台必须为空");
            if (manifest.TransferJarIds.Count > slave.MaxJars)
                return WorldOrderExecutionAdmissionResult.Rejected("从工作台罐位不足，不能完整接收转移罐子");

            foreach (var item in manifest.TransferLiquids)
            {
                if (item.Value < 0 || item.Value > slave.MaxLiquids)
                    return WorldOrderExecutionAdmissionResult.Rejected($"从工作台液体「{item.Key}」容量不足");
                if (item.Value > 0 && slave.IsBanned(item.Key))
                    return WorldOrderExecutionAdmissionResult.Rejected($"从工作台禁用液体「{item.Key}」，不能接收执行所需液体");
            }
        }

        if (!_reservations.TryReserve(manifest, master.LiquidStorage, out var reserveError))
            return WorldOrderExecutionAdmissionResult.Rejected(reserveError ?? "资源预留失败");

        return WorldOrderExecutionAdmissionResult.Admitted(manifest);
    }

    /// <summary>执行结束、取消或中断后由生命周期所有者调用；只释放预留，不回滚资源。</summary>
    public bool Release(int demandId) => _reservations.Release(demandId);
}

/// <summary>一次准入尝试的结果。</summary>
public sealed record WorldOrderExecutionAdmissionResult(
    bool Success, string? Reason, WorldOrderResourceManifest? Manifest)
{
    internal static WorldOrderExecutionAdmissionResult Rejected(string reason) =>
        new(false, reason, null);

    internal static WorldOrderExecutionAdmissionResult Admitted(WorldOrderResourceManifest manifest) =>
        new(true, null, manifest);
}

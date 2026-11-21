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
    private readonly ShipLiquidInventory? _shipLiquidInventory;
    private readonly Dictionary<Workshop, WorkshopLiquidStaging> _stagingByWorkshop =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, (OrderLiquidScope Master, OrderLiquidScope? Slave)> _scopesByDemand = new();
    private readonly Dictionary<int, LiquidBatchReceipt> _settlementsByDemand = new();

    public WorldOrderExecutionAdmission(
        OrderDemandBoard board, WorldOrderSolveBook solveBook,
        WorkshopRoster roster, WorldResourceReservationBook reservations)
        : this(board, solveBook, roster, reservations, null)
    {
    }

    /// <summary>
    /// 新液体资源路径：订单初始供给从自机液库扣除，并分配到共享工作台暂存区。
    /// 未传液库的旧构造器仅保留迁移兼容，不代表新领域契约。
    /// </summary>
    public WorldOrderExecutionAdmission(
        OrderDemandBoard board, WorldOrderSolveBook solveBook,
        WorkshopRoster roster, WorldResourceReservationBook reservations,
        ShipLiquidInventory shipLiquidInventory)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _solveBook = solveBook ?? throw new ArgumentNullException(nameof(solveBook));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _shipLiquidInventory = shipLiquidInventory ?? throw new ArgumentNullException(nameof(shipLiquidInventory));
    }

    private WorldOrderExecutionAdmission(
        OrderDemandBoard board, WorldOrderSolveBook solveBook,
        WorkshopRoster roster, WorldResourceReservationBook reservations,
        ShipLiquidInventory? shipLiquidInventory)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _solveBook = solveBook ?? throw new ArgumentNullException(nameof(solveBook));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _shipLiquidInventory = shipLiquidInventory;
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

            if (_shipLiquidInventory == null)
            {
                foreach (var item in manifest.TransferLiquids)
                {
                    if (item.Value < 0 || item.Value > slave.MaxLiquids)
                        return WorldOrderExecutionAdmissionResult.Rejected($"从工作台液体「{item.Key}」容量不足");
                    if (item.Value > 0 && slave.IsBanned(item.Key))
                        return WorldOrderExecutionAdmissionResult.Rejected($"从工作台禁用液体「{item.Key}」，不能接收执行所需液体");
                }
            }
            else if (manifest.SlaveStagingPeakCapacity > slave.MaxLiquids)
            {
                return WorldOrderExecutionAdmissionResult.Rejected("从工作台订单液体路径峰值超过总暂存容量");
            }
        }

        var availableLiquids = _shipLiquidInventory?.Balances ?? master.LiquidStorage;
        if (!_reservations.TryReserve(manifest, availableLiquids, out var reserveError))
            return WorldOrderExecutionAdmissionResult.Rejected(reserveError ?? "资源预留失败");

        if (_shipLiquidInventory != null)
        {
            if (!TryAllocateOrderLiquids(manifest, master, _roster.Slave, out var allocationError))
            {
                _reservations.Release(demandId);
                return WorldOrderExecutionAdmissionResult.Rejected(allocationError ?? "订单液体资源分配失败");
            }
        }

        return WorldOrderExecutionAdmissionResult.Admitted(manifest);
    }

    internal (OrderLiquidScope Master, OrderLiquidScope? Slave)? GetLiquidScopes(int demandId) =>
        _scopesByDemand.TryGetValue(demandId, out var scopes) ? scopes : null;

    /// <summary>结算并返还订单全部工作台游离液体；重复调用返回同一结果。</summary>
    internal LiquidBatchReceipt? SettleLiquidResources(int demandId)
    {
        if (_settlementsByDemand.TryGetValue(demandId, out var settled)) return settled;
        if (_shipLiquidInventory == null || !_scopesByDemand.TryGetValue(demandId, out var scopes))
            return null;

        var requested = new Dictionary<string, int>(StringComparer.Ordinal);
        Merge(requested, scopes.Master.Staging.DrainOrder(demandId));
        if (scopes.Slave != null)
            Merge(requested, scopes.Slave.Staging.DrainOrder(demandId));

        var receipt = _shipLiquidInventory.Return(requested);
        _settlementsByDemand.Add(demandId, receipt);
        return receipt;
    }

    /// <summary>执行结束、取消或中断后释放预留；新路径先结算，再释放容量配额。</summary>
    public bool Release(int demandId)
    {
        if (_scopesByDemand.Remove(demandId, out var scopes))
        {
            SettleLiquidResources(demandId);
            scopes.Master.Staging.ReleaseOrderCapacity(demandId);
            scopes.Slave?.Staging.ReleaseOrderCapacity(demandId);
        }
        var released = _reservations.Release(demandId);
        return released;
    }

    private bool TryAllocateOrderLiquids(
        WorldOrderResourceManifest manifest, Workshop master, Workshop? slave, out string? reason)
    {
        var masterStaging = GetOrCreateStaging(master);
        WorkshopLiquidStaging? slaveStaging = null;
        var masterReserved = false;
        var slaveReserved = false;
        var withdrawn = false;

        if (!masterStaging.TryReserveOrderCapacity(manifest.DemandId, manifest.MasterStagingPeakCapacity))
        {
            reason = "主工作台订单液体峰值容量无法预留";
            return false;
        }
        masterReserved = true;

        if (manifest.IsPartitioned)
        {
            if (slave == null)
            {
                masterStaging.ReleaseOrderCapacity(manifest.DemandId);
                reason = "B 类订单缺少从工作台";
                return false;
            }
            slaveStaging = GetOrCreateStaging(slave);
            if (!slaveStaging.TryReserveOrderCapacity(manifest.DemandId, manifest.SlaveStagingPeakCapacity))
            {
                masterStaging.ReleaseOrderCapacity(manifest.DemandId);
                reason = "从工作台订单液体峰值容量无法预留";
                return false;
            }
            slaveReserved = true;
        }

        if (!_shipLiquidInventory!.TryWithdraw(manifest.MasterInitialLiquidAmounts, out _))
        {
            if (slaveReserved) slaveStaging!.ReleaseOrderCapacity(manifest.DemandId);
            if (masterReserved) masterStaging.ReleaseOrderCapacity(manifest.DemandId);
            reason = "自机液库初始供给不足";
            return false;
        }
        withdrawn = true;

        foreach (var item in manifest.MasterInitialLiquidAmounts)
        {
            if (item.Value == 0) continue;
            if (!masterStaging.TryReceiveExact(manifest.DemandId, item.Key, item.Value, out var grant))
            {
                var drained = masterStaging.DrainOrder(manifest.DemandId);
                _shipLiquidInventory.Return(drained);
                if (slaveReserved) slaveStaging!.ReleaseOrderCapacity(manifest.DemandId);
                if (masterReserved) masterStaging.ReleaseOrderCapacity(manifest.DemandId);
                reason = grant.Reason ?? "主工作台暂存区无法接收订单初始供给";
                return false;
            }
        }

        var masterScope = new OrderLiquidScope(manifest.DemandId, masterStaging);
        var slaveScope = slaveStaging == null ? null : new OrderLiquidScope(manifest.DemandId, slaveStaging);
        _scopesByDemand.Add(manifest.DemandId, (masterScope, slaveScope));
        reason = null;
        return true;
    }

    private WorkshopLiquidStaging GetOrCreateStaging(Workshop workshop)
    {
        if (_stagingByWorkshop.TryGetValue(workshop, out var existing)) return existing;
        // 过渡期复用 Workshop.MaxLiquids 作为新模型的“全液种合计容量”配置值；
        // 旧 Workshop.LiquidStorage 仍按原规则运行，最终由独立配置替代此映射。
        var staging = new WorkshopLiquidStaging(workshop.MaxLiquids, workshop.Ban);
        _stagingByWorkshop.Add(workshop, staging);
        return staging;
    }

    private static void Merge(
        Dictionary<string, int> target, IReadOnlyDictionary<string, int> source)
    {
        foreach (var item in source)
            target[item.Key] = checked(target.GetValueOrDefault(item.Key) + item.Value);
    }
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

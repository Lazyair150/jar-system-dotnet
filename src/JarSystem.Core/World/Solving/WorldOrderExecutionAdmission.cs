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
    private readonly OrderLiquidAllocationService? _liquidAllocationService;
    internal ShipLiquidInventory? ShipLiquidInventory => _shipLiquidInventory;
    internal WorldResourceReservationBook Reservations => _reservations;
    private readonly Dictionary<Workshop, WorkshopLiquidStaging> _stagingByWorkshop =
        new(ReferenceEqualityComparer.Instance);
    private Func<bool>? _canAdmitOrders;

    public WorldOrderExecutionAdmission(
        OrderDemandBoard board, WorldOrderSolveBook solveBook,
        WorkshopRoster roster, WorldResourceReservationBook reservations)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _solveBook = solveBook ?? throw new ArgumentNullException(nameof(solveBook));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
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
        _liquidAllocationService = new OrderLiquidAllocationService(
            _shipLiquidInventory, _reservations, GetOrCreateStaging);
    }

    internal void BindOrderAdmissionGate(Func<bool> canAdmitOrders)
    {
        ArgumentNullException.ThrowIfNull(canAdmitOrders);
        if (_canAdmitOrders != null)
            throw new InvalidOperationException("订单准入门禁已经绑定。");
        _canAdmitOrders = canAdmitOrders;
    }

    internal bool IsBoundTo(
        OrderDemandBoard board, WorldOrderSolveBook solveBook, WorkshopRoster roster) =>
        ReferenceEquals(_board, board) &&
        ReferenceEquals(_solveBook, solveBook) &&
        ReferenceEquals(_roster, roster);

    /// <summary>
    /// 尝试准入一张订单。应在世界逻辑调度线程内同步调用，避免检查与预留之间插入世界变更。
    /// </summary>
    internal IReadOnlyDictionary<string, int> GetPrefillBalances(Workshop workshop) =>
        _stagingByWorkshop.TryGetValue(workshop, out var staging)
            ? staging.PrefillBalances
            : new Dictionary<string, int>(StringComparer.Ordinal);

    internal WorldSnapshot CaptureWorldSnapshot()
    {
        var master = _roster.Master;
        var prefill = master != null && _stagingByWorkshop.TryGetValue(master, out var staging)
            ? staging.PrefillBalances
            : new Dictionary<string, int>(StringComparer.Ordinal);
        return WorldSnapshotFactory.Capture(_roster, _shipLiquidInventory, prefill);
    }

    internal WorkshopPrefillTransaction BeginMasterWorkshopPrefill(
        Workshop targetWorkshop, IEnumerable<string> productionPriority)
    {
        ArgumentNullException.ThrowIfNull(targetWorkshop);
        ArgumentNullException.ThrowIfNull(productionPriority);
        if (_shipLiquidInventory == null)
            throw new InvalidOperationException("旧版兼容准入不支持主台切换前置补液。");
        if (!_roster.IsSpare(targetWorkshop))
            throw new InvalidOperationException("主台前置补液目标必须是当前主从岗位之外的备用工作台。");
        return GetOrCreateStaging(targetWorkshop)
            .BeginPrefillFromShip(_shipLiquidInventory, productionPriority);
    }

    internal LiquidBatchReceipt PrefillMasterWorkshop(
        Workshop targetWorkshop, IEnumerable<string> productionPriority)
    {
        var transaction = BeginMasterWorkshopPrefill(targetWorkshop, productionPriority);
        transaction.Commit();
        return transaction.Receipt;
    }

    /// <summary>
    /// 主台岗位已成功提交替换后，将退役主台未消费的预置余额返还自机液库。
    /// 此入口只处理预置余额，不触碰订单账户；订单账户必须已由中断结算清空。
    /// </summary>
    internal LiquidBatchReceipt ReturnRetiredMasterPrefill(Workshop retiredMaster)
    {
        ArgumentNullException.ThrowIfNull(retiredMaster);
        if (_shipLiquidInventory == null)
            throw new InvalidOperationException("旧版兼容准入没有自机液库，不能返还主台预置余额。");
        if (ReferenceEquals(_roster.Master, retiredMaster) ||
            ReferenceEquals(_roster.Slave, retiredMaster))
            throw new InvalidOperationException("只有已经退出主从岗位的工作台才能返还退役主台预置余额。");
        if (!_stagingByWorkshop.TryGetValue(retiredMaster, out var staging))
            return new LiquidBatchReceipt(
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal));

        var drained = staging.DrainPrefillForReturn();
        try
        {
            var receipt = _shipLiquidInventory.Return(drained);
            _stagingByWorkshop.Remove(retiredMaster);
            return receipt;
        }
        catch
        {
            staging.RestoreDrainedPrefill(drained);
            throw;
        }
    }

    public WorldOrderExecutionAdmissionResult TryAdmit(int demandId)
    {
        if (_canAdmitOrders != null && !_canAdmitOrders())
            return WorldOrderExecutionAdmissionResult.Rejected(
                "世界当前不允许订单准入：工作台替换正在进行或世界运行时已故障。");
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

        var snapshot = CaptureWorldSnapshot();
        var result = _solveBook.GetCurrentResult(demandId, snapshot);
        if (result == null)
            return WorldOrderExecutionAdmissionResult.Rejected("求解结果不存在或与当前世界版本不一致");
        if (result.DemandId != demandId || !result.IsFeasible || result.Plan == null)
            return WorldOrderExecutionAdmissionResult.Rejected("当前求解结果不是该订单的有效可行计划");

        WorldOrderResourceManifest manifest;
        try
        {
            manifest = result.ResourceReplay is { IsComplete: true } replay
                ? WorldOrderResourceManifest.CreateWithReplay(result, snapshot, replay)
                : WorldOrderResourceManifest.Create(result, snapshot);
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
            else if (manifest.SlaveStagingPeakCapacity > slave.LiquidStagingCapacity)
            {
                return WorldOrderExecutionAdmissionResult.Rejected("从工作台订单液体路径峰值超过总暂存容量");
            }
        }

        var availableLiquids = _shipLiquidInventory == null
            ? master.LiquidStorage
            : _liquidAllocationService!.BuildReservationLiquidityView(manifest, master);
        if (!_reservations.TryReserve(manifest, availableLiquids, out var reserveError))
            return WorldOrderExecutionAdmissionResult.Rejected(reserveError ?? "资源预留失败");

        if (_shipLiquidInventory != null)
        {
            if (!_liquidAllocationService!.TryAllocate(manifest, master, _roster.Slave, out var allocationError))
            {
                _reservations.Release(demandId);
                return WorldOrderExecutionAdmissionResult.Rejected(allocationError ?? "订单液体资源分配失败");
            }
        }

        return WorldOrderExecutionAdmissionResult.Admitted(manifest);
    }

    /// <summary>
    /// 捕获新液体资源路径的全部工作台暂存状态与活动订单范围。
    /// 该快照必须在 Host 独占区内取得，避免看到准入/结算的中间状态。
    /// </summary>
    public WorldOrderExecutionAdmissionSnapshot Capture()
    {
        if (_shipLiquidInventory == null)
            throw new InvalidOperationException("旧版兼容准入不支持活动液体资源快照。");
        var master = _roster.Master ?? throw new InvalidOperationException("快照时没有主工作台。");
        var slave = _roster.Slave;
        foreach (var workshop in _stagingByWorkshop.Keys.ToArray())
        {
            if (ReferenceEquals(workshop, master) || ReferenceEquals(workshop, slave))
                continue;
            var retired = _stagingByWorkshop[workshop].Capture();
            if (retired.Accounts.Count != 0 || retired.ReservedCapacityByOrder.Count != 0 ||
                retired.PrefillBalances.Count != 0)
                throw new InvalidOperationException("已退役工作台仍持有订单暂存资源，不能静默丢弃。");
            // 工作台替换后留下的空暂存对象不再属于当前岗位图，不进入新快照。
            _stagingByWorkshop.Remove(workshop);
        }

        var masterStaging = _stagingByWorkshop.TryGetValue(master, out var masterValue)
            ? masterValue.Capture()
            : null;
        var slaveStaging = slave != null && _stagingByWorkshop.TryGetValue(slave, out var slaveValue)
            ? slaveValue.Capture()
            : null;
        var activeOrders = _reservations.Capture().Reservations.OrderBy(item => item.DemandId)
            .Select(item => new WorldOrderExecutionScopeSnapshot(
                item.DemandId,
                item.UsesSlave,
                masterStaging?.ReservedCapacityByOrder.ContainsKey(item.DemandId) ?? false,
                slaveStaging?.ReservedCapacityByOrder.ContainsKey(item.DemandId) ?? false))
            .ToArray();
        return new WorldOrderExecutionAdmissionSnapshot(masterStaging, slaveStaging, activeOrders);
    }

    /// <summary>
    /// 将活动订单暂存状态恢复到一个全新的准入器。
    /// 不重新扣自机液库、不重新执行订单；恢复前核对资源预留与工作台配置。
    /// </summary>
    public void Restore(WorldOrderExecutionAdmissionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_shipLiquidInventory == null)
            throw new InvalidOperationException("旧版兼容准入不支持活动液体资源恢复。");
        if (_stagingByWorkshop.Count != 0 ||
            _reservations.Capture().Reservations.Any(item =>
                _reservations.FindLease(item.DemandId)?.HasLiquidScopes == true))
            throw new InvalidOperationException("只能向没有活动暂存状态的新准入器恢复。");

        var master = _roster.Master ?? throw new InvalidOperationException("恢复时没有主工作台。");
        var slave = _roster.Slave;
        ValidateStagingForWorkshop(snapshot.MasterStaging, master, "主台", required: snapshot.ActiveOrders.Count > 0);
        ValidateStagingForWorkshop(snapshot.SlaveStaging, slave, "从台",
            required: snapshot.ActiveOrders.Any(item => item.UsesSlave));

        var activeIds = new HashSet<int>();
        foreach (var active in snapshot.ActiveOrders)
        {
            if (active.DemandId <= 0 || !activeIds.Add(active.DemandId))
                throw new InvalidOperationException("活动订单暂存快照包含无效或重复订单 ID。");
            var reservation = _reservations.Find(active.DemandId)
                ?? throw new InvalidOperationException("活动订单缺少资源预留：" + active.DemandId);
            if (reservation.UsesSlave != active.UsesSlave)
                throw new InvalidOperationException("活动订单的从台占用状态与资源预留不一致：" + active.DemandId);
            var masterHasReservation = snapshot.MasterStaging?.ReservedCapacityByOrder
                .ContainsKey(active.DemandId) ?? false;
            var slaveHasReservation = snapshot.SlaveStaging?.ReservedCapacityByOrder
                .ContainsKey(active.DemandId) ?? false;
            if (active.MasterCapacityReserved != masterHasReservation)
                throw new InvalidOperationException("活动订单主台容量预留状态与暂存快照不一致：" + active.DemandId);
            if (active.SlaveCapacityReserved != slaveHasReservation)
                throw new InvalidOperationException("活动订单从台容量预留状态与暂存快照不一致：" + active.DemandId);
            if (active.UsesSlave != reservation.UsesSlave ||
                active.SlaveCapacityReserved != active.UsesSlave)
                throw new InvalidOperationException("活动订单的从台使用状态与容量预留不一致：" + active.DemandId);
            if (!active.UsesSlave && reservation.UsesSlave)
                throw new InvalidOperationException("A 类订单不能占用从台。");
        }

        var reservationIds = _reservations.Capture().Reservations.Select(item => item.DemandId).ToHashSet();
        if (!reservationIds.SetEquals(activeIds))
            throw new InvalidOperationException("资源预留集合与活动订单液体范围集合不一致。");

        var masterReservedIds = snapshot.MasterStaging?.ReservedCapacityByOrder.Keys.ToHashSet()
            ?? new HashSet<int>();
        var expectedMasterReservedIds = snapshot.ActiveOrders
            .Where(item => item.MasterCapacityReserved)
            .Select(item => item.DemandId).ToHashSet();
        if (!masterReservedIds.SetEquals(expectedMasterReservedIds))
            throw new InvalidOperationException("主台容量预留集合与活动订单阶段状态不一致。");
        var expectedSlaveReservedIds = snapshot.ActiveOrders
            .Where(item => item.SlaveCapacityReserved)
            .Select(item => item.DemandId).ToHashSet();
        var slaveReservedIds = snapshot.SlaveStaging?.ReservedCapacityByOrder.Keys.ToHashSet()
            ?? new HashSet<int>();
        if (!slaveReservedIds.SetEquals(expectedSlaveReservedIds))
            throw new InvalidOperationException("从台容量预留集合与活动订单阶段状态不一致。");
        if (snapshot.MasterStaging != null &&
            snapshot.MasterStaging.Accounts.Keys.Any(id => !activeIds.Contains(id)))
            throw new InvalidOperationException("主台暂存账户属于未登记的活动订单。");
        var slaveOrderIds = snapshot.ActiveOrders.Where(item => item.UsesSlave)
            .Select(item => item.DemandId).ToHashSet();
        if (snapshot.SlaveStaging != null &&
            snapshot.SlaveStaging.Accounts.Keys.Any(id => !slaveOrderIds.Contains(id)))
            throw new InvalidOperationException("从台暂存账户属于非 B 类活动订单。");

        if (snapshot.MasterStaging != null)
            _stagingByWorkshop.Add(master, WorkshopLiquidStaging.Restore(snapshot.MasterStaging));
        if (snapshot.SlaveStaging != null)
        {
            if (slave == null) throw new InvalidOperationException("快照包含从台暂存区，但当前世界没有从台。");
            _stagingByWorkshop.Add(slave, WorkshopLiquidStaging.Restore(snapshot.SlaveStaging));
        }

        foreach (var active in snapshot.ActiveOrders)
        {
            var masterScope = new OrderLiquidScope(active.DemandId, _stagingByWorkshop[master], master.OwnedInstanceId);
            var slaveScope = active.UsesSlave
                ? new OrderLiquidScope(active.DemandId, _stagingByWorkshop[slave!], slave!.OwnedInstanceId)
                : null;
            var lease = _reservations.FindLease(active.DemandId)
                ?? throw new InvalidOperationException("恢复活动液体范围时缺少资源租约：" + active.DemandId);
            lease.AttachLiquidScopes(masterScope, slaveScope);
        }
    }

    private static void ValidateStagingForWorkshop(
        WorkshopLiquidStagingSnapshot? snapshot, Workshop? workshop, string role, bool required)
    {
        if (snapshot == null)
        {
            if (required) throw new InvalidOperationException(role + "活动订单缺少暂存区快照。");
            return;
        }
        if (workshop == null)
            throw new InvalidOperationException("快照包含" + role + "暂存区，但当前世界没有对应工作台。");
        if (snapshot.Capacity != workshop.LiquidStagingCapacity)
            throw new InvalidOperationException(role + "暂存区容量与当前工作台配置不一致。");
        var expectedBans = workshop.Ban.OrderBy(item => item, StringComparer.Ordinal);
        if (!snapshot.BannedLiquids.OrderBy(item => item, StringComparer.Ordinal)
            .SequenceEqual(expectedBans, StringComparer.Ordinal))
            throw new InvalidOperationException(role + "暂存区禁液规则与当前工作台配置不一致。");
    }

    internal (OrderLiquidScope Master, OrderLiquidScope? Slave)? GetLiquidScopes(int demandId) =>
        _reservations.FindLease(demandId)?.LiquidScopes;

    /// <summary>结算并返还订单全部工作台游离液体；重复调用返回同一结果。</summary>
    internal LiquidBatchReceipt? SettleLiquidResources(int demandId)
    {
        var lease = _reservations.FindLease(demandId);
        if (lease?.SettlementReceipt != null) return lease.SettlementReceipt;
        var scopes = lease?.LiquidScopes;
        if (_shipLiquidInventory == null || !scopes.HasValue)
            return null;

        if (lease!.SettlementStage != OrderResourceSettlementStage.NotStarted)
            throw new InvalidOperationException("订单结算阶段已推进，但缺少可重放的结算收据。");

        // Validate/aggregate before mutating either Workshop account. A checked overflow
        // across two individually valid staging scopes must not strand the first drained scope.
        var requested = new Dictionary<string, int>(StringComparer.Ordinal);
        Merge(requested, scopes.Value.Master.Balance);
        scopes.Value.Master.Staging.EnsureCanTransferOrderLiquids();
        if (scopes.Value.Slave != null)
        {
            Merge(requested, scopes.Value.Slave.Balance);
            scopes.Value.Slave.Staging.EnsureCanTransferOrderLiquids();
        }

        scopes.Value.Master.Staging.DrainOrder(demandId);
        if (scopes.Value.Slave != null)
            scopes.Value.Slave.Staging.DrainOrder(demandId);
        var receipt = _shipLiquidInventory.Return(requested);
        return lease.RecordSettlement(receipt);
    }

    /// <summary>
    /// 释放资源租约及其容量配额，不执行液体结算。
    /// 新执行路径必须由 OrderResourceSettlementService 先完成结算，再调用此方法。
    /// </summary>
    internal bool ReleaseReservationOnly(int demandId)
    {
        var lease = _reservations.FindLease(demandId);
        var scopes = lease?.LiquidScopes;
        if (scopes.HasValue)
        {
            scopes.Value.Master.Staging.ReleaseOrderCapacity(demandId);
            scopes.Value.Slave?.Staging.ReleaseOrderCapacity(demandId);
        }
        return _reservations.Release(demandId);
    }

    /// <summary>
    /// 兼容旧调用方：结算液体后释放资源。新执行会话不应调用此入口。
    /// </summary>
    public bool Release(int demandId)
    {
        SettleLiquidResources(demandId);
        return ReleaseReservationOnly(demandId);
    }

    internal OrderResourceLease? FindLease(int demandId) =>
        _reservations.FindLease(demandId);

    private WorkshopLiquidStaging GetOrCreateStaging(Workshop workshop)
    {
        if (_stagingByWorkshop.TryGetValue(workshop, out var existing))
        {
            var expectedBans = workshop.Ban.OrderBy(item => item, StringComparer.Ordinal);
            if (existing.BannedLiquids.OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual(expectedBans, StringComparer.Ordinal))
                return existing;

            if (existing.UsedCapacity != 0 || existing.ReservedCapacity != 0)
                throw new InvalidOperationException("工作台禁液规则已变化，但旧暂存区仍持有活动订单资源。");
            // 工作台能力替换后，空暂存区必须同步新的 ban 规则，不能继续使用旧配置。
            existing = new WorkshopLiquidStaging(workshop.LiquidStagingCapacity, workshop.Ban);
            _stagingByWorkshop[workshop] = existing;
            return existing;
        }
        // 新订单暂存区使用独立的全液种合计容量；旧 Workshop.LiquidStorage 仍保留 MaxLiquids 单液种语义。
        var staging = new WorkshopLiquidStaging(workshop.LiquidStagingCapacity, workshop.Ban);
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


/// <summary>一张活动订单在主/从台暂存区中的资源范围声明。</summary>
public sealed record WorldOrderExecutionScopeSnapshot(
    int DemandId,
    bool UsesSlave,
    bool MasterCapacityReserved = true,
    bool SlaveCapacityReserved = false);

/// <summary>准入器持有的活动暂存区与订单范围快照。</summary>
public sealed class WorldOrderExecutionAdmissionSnapshot
{
    public WorldOrderExecutionAdmissionSnapshot(
        WorkshopLiquidStagingSnapshot? masterStaging,
        WorkshopLiquidStagingSnapshot? slaveStaging,
        IEnumerable<WorldOrderExecutionScopeSnapshot> activeOrders)
    {
        MasterStaging = masterStaging;
        SlaveStaging = slaveStaging;
        ActiveOrders = Array.AsReadOnly(activeOrders.OrderBy(item => item.DemandId).ToArray());
    }

    public WorkshopLiquidStagingSnapshot? MasterStaging { get; }
    public WorkshopLiquidStagingSnapshot? SlaveStaging { get; }
    public IReadOnlyList<WorldOrderExecutionScopeSnapshot> ActiveOrders { get; }
}

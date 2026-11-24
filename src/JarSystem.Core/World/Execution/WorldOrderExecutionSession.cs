using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World.Execution;

/// <summary>
/// 一张已准入订单的真实执行会话。它只消费不可变计划，不重新求解、不选择替代罐子。
/// 每个非 MATCH 计划步骤与 B 类跨台转移各占一个显式配置的逻辑时间单位。
/// </summary>
public sealed class WorldOrderExecutionSession : IOrderInterruptionPort
{
    private readonly OrderDemandBoard _board;
    private readonly WorldOrderSolveBook _solveBook;
    private readonly WorldOrderExecutionAdmission _admission;
    private readonly WorkshopRoster _roster;
    private readonly Workshop _master;
    private readonly Workshop? _slave;
    private readonly WorldOrderResourceManifest _manifest;
    private readonly WorldOrderSolvePlan _plan;
    private readonly IReadOnlyList<WorldPlanStep> _masterSteps;
    private readonly IReadOnlyList<WorldPlanStep> _slaveSteps;
    private readonly IReadOnlyList<int> _deliveryJarIds;
    private readonly WorldPlanStepApplier _applier;
    private readonly OrderTransferService _transferService = new();
    private readonly OrderResourceSettlementService _settlementService = new();
    private readonly OrderLiquidScope? _masterLiquidScope;
    private readonly OrderLiquidScope? _slaveLiquidScope;
    private readonly long _unitDurationLogicalMilli;
    private readonly SlaveRollback _slaveRollback = new();
    private readonly ResourceLedger? _resourceLedger;
    private readonly Func<int>? _resourceLedgerTick;
    private readonly List<ExecutionLogEntry> _resourceSettlementLog = new();

    private int _masterIndex;
    private int _slaveIndex;
    private long _unitElapsedLogicalMilli;
    private bool _reservationReleased;
    private bool _slaveTransferCommitted;

    public int DemandId { get; }
    internal bool IsBoundTo(OrderDemandBoard board, WorkshopRoster roster) =>
        ReferenceEquals(_board, board) && ReferenceEquals(_roster, roster);
    public WorldOrderExecutionStage Stage { get; private set; }
    public bool IsTerminal =>
        Stage is WorldOrderExecutionStage.Completed or
            WorldOrderExecutionStage.Failed or WorldOrderExecutionStage.Interrupted or
            WorldOrderExecutionStage.Cancelled;
    public bool Succeeded => Stage == WorldOrderExecutionStage.Completed;
    /// <summary>Immutable resource transition produced by the committed B-class handoff, when exact Board identities are available.</summary>
    public RuleTransition? TransferTransition => _transferService.CommittedTransition;
    public string? Reason { get; private set; }
    public long UnitDurationLogicalMilli => _unitDurationLogicalMilli;
    public long UnitElapsedLogicalMilli => _unitElapsedLogicalMilli;
    public int CompletedMasterSteps => _masterIndex;
    public int CompletedSlaveSteps => _slaveIndex;
    /// <summary>本会话从台结算产生的丢弃解释日志（只读视图）；回流/丢弃数量以 ResourceLedger 为准。</summary>
    public IReadOnlyList<ExecutionLogEntry> ResourceSettlementLog => _resourceSettlementLog.AsReadOnly();

    /// <summary>
    /// 捕获会话的执行游标。Manifest/Plan 都是不可变纯值对象；
    /// 该快照描述执行进度，不单独包含工作台罐子状态，必须与世界状态快照配套使用。
    /// </summary>
    public WorldOrderExecutionSessionProgressSnapshot CaptureProgress() => new(
        DemandId, Stage, _masterIndex, _slaveIndex, _unitElapsedLogicalMilli,
        _unitDurationLogicalMilli, _slaveTransferCommitted, Reason, _manifest, _plan);

    private WorldOrderExecutionSession(
        int demandId,
        OrderDemandBoard board,
        WorldOrderSolveBook solveBook,
        WorldOrderExecutionAdmission admission,
        WorkshopRoster roster,
        WorldOrderResourceManifest manifest,
        WorldOrderSolvePlan plan,
        long unitDurationLogicalMilli,
        ResourceLedger? resourceLedger,
        Func<int>? resourceLedgerTick,
        bool initializeExecution = true)
    {
        DemandId = demandId;
        _board = board;
        _solveBook = solveBook;
        _admission = admission;
        _roster = roster;
        _master = roster.Master ?? throw new InvalidOperationException("执行开始时没有主工作台");
        _slave = manifest.IsPartitioned
            ? roster.Slave ?? throw new InvalidOperationException("B 类执行开始时没有从工作台")
            : null;
        _manifest = manifest;
        _plan = plan;
        _unitDurationLogicalMilli = unitDurationLogicalMilli;
        _resourceLedger = resourceLedger;
        _resourceLedgerTick = resourceLedgerTick;
        _applier = new WorldPlanStepApplier();
        var liquidScopes = admission.GetLiquidScopes(demandId);
        _masterLiquidScope = liquidScopes?.Master;
        _slaveLiquidScope = liquidScopes?.Slave;

        if (manifest.DemandId != demandId || manifest.IsPartitioned != plan.IsPartitioned)
            throw new InvalidOperationException("资源清单与求解计划不属于同一订单或执行类型");

        // 每个 SolverStep 都绑定一条需求。只有该步骤 After 中的目标罐子最终满足
        // RequirementShape/RequirementLiquid 时，它才是该需求的交付罐。
        // 不能只找 MATCH：POUR_IN / POUR_OUT / RESHAPE 也可能是满足需求的最后一步。
        _deliveryJarIds = Array.AsReadOnly(ReadDeliveryJarIds(plan).ToArray());
        // MATCH 是零逻辑时间步骤，但仍然必须经过 StepApplier/Workshop.MarkJar。
        // 保留完整路径，由 PumpInstantMatches 在正确的阶段边界立即应用。
        _masterSteps = Array.AsReadOnly(plan.MasterSteps.ToArray());
        _slaveSteps = Array.AsReadOnly(plan.SlaveSteps.ToArray());

        if (_deliveryJarIds.Distinct().Count() != _deliveryJarIds.Count)
            throw new InvalidOperationException("求解计划重复将同一只罐子作为交付物");
        if (plan.MasterSteps.Count + plan.SlaveSteps.Count > 0 && _deliveryJarIds.Count == 0)
            throw new InvalidOperationException("可执行计划没有任何终态满足需求的交付罐");
        if (manifest.IsPartitioned &&
            _deliveryJarIds.Any(id => !manifest.TransferJarIds.Contains(id)))
            throw new InvalidOperationException("B 类交付罐没有包含在从主台转移的资源清单中");
        if (!manifest.IsPartitioned &&
            _deliveryJarIds.Any(id => !manifest.MasterJarIds.Contains(id)))
            throw new InvalidOperationException("A 类交付罐没有包含在主台资源清单中");
        if (!manifest.IsPartitioned && _slaveSteps.Count != 0)
            throw new InvalidOperationException("单阶段执行计划不能包含从台步骤");

        Stage = _masterSteps.Count > 0
            ? WorldOrderExecutionStage.MasterSteps
            : manifest.IsPartitioned
                ? WorldOrderExecutionStage.Transfer
                : WorldOrderExecutionStage.MasterSteps;

        if (initializeExecution)
        {
            if (Stage == WorldOrderExecutionStage.MasterSteps)
                admission.Reservations.MarkMasterExecuting(demandId);
            else
                admission.Reservations.MarkTransferPending(demandId);

            // 先处理主台路径开头连续的零耗时 MATCH；后续 MATCH 会在前一个耗时步骤完成后处理。
            if (Stage == WorldOrderExecutionStage.MasterSteps)
                PumpInstantMatches(_master, _masterSteps, ref _masterIndex, "master", _masterLiquidScope);

            if (!IsTerminal && Stage == WorldOrderExecutionStage.MasterSteps &&
                _masterIndex == _masterSteps.Count)
            {
                if (manifest.IsPartitioned)
                {
                    Stage = WorldOrderExecutionStage.Transfer;
                    admission.Reservations.MarkTransferPending(demandId);
                }
                else
                    CompleteSuccess();
            }
        }
    }

    /// <summary>
    /// 校验会话游标与同一检查点中的工作台罐子状态一致。
    /// 用计划步骤的 Before/After 纯值状态验证已经完成的路径，不执行任何操作。
    /// </summary>
    internal static void ValidateProgressAgainstWorkshopSnapshots(
        WorldOrderExecutionSessionProgressSnapshot snapshot,
        WorkshopRuntimeStateSnapshot master,
        WorkshopRuntimeStateSnapshot? slave)
    {
        var plan = snapshot.Plan;
        var isPartitioned = snapshot.Manifest.IsPartitioned;
        var lockedIds = snapshot.Manifest.LockedJarIds.ToHashSet();
        if (isPartitioned && slave == null)
            throw new InvalidOperationException("B 类执行会话缺少从台工作台快照。");

        switch (snapshot.Stage)
        {
            case WorldOrderExecutionStage.MasterSteps:
            {
                var expected = snapshot.CompletedMasterSteps == 0
                    ? plan.MasterSteps[0].Before
                    : plan.MasterSteps[snapshot.CompletedMasterSteps - 1].After;
                EnsureWorkshopMatches(master, expected, lockedIds, null, "主台");
                if (isPartitioned && slave!.Jars.Count != 0)
                    throw new InvalidOperationException("主台阶段的 B 类检查点从台不应持有罐子。");
                break;
            }
            case WorldOrderExecutionStage.Transfer:
            {
                if (plan.MasterSteps.Count > 0)
                    EnsureWorkshopMatches(master, plan.MasterSteps[^1].After, lockedIds, null, "主台");
                else
                    EnsureTransferJarsRemainOnMaster(master, snapshot.Manifest.TransferJarIds);
                if (slave!.Jars.Count != 0)
                    throw new InvalidOperationException("泵送前的 B 类检查点从台不应持有罐子。");
                break;
            }
            case WorldOrderExecutionStage.SlaveSteps:
            {
                var expectedSlave = (snapshot.CompletedSlaveSteps == 0
                    ? plan.SlaveSteps[0].Before
                    : plan.SlaveSteps[snapshot.CompletedSlaveSteps - 1].After).ToArray();

                // B 类完成路径由从台统一交付。部分罐子可能已经在主台阶段完成需求，
                // 之后仍会随 TransferJarIds 移交给从台用于最终交付；这些罐子不会出现在
                // SlaveSteps 的 Before/After 中，但必须纳入从台检查点的预期状态。
                if (plan.MasterSteps.Count > 0)
                {
                    var transferIds = snapshot.Manifest.TransferJarIds.ToHashSet();
                    var expectedIds = expectedSlave
                        .Where(item => item.Id.HasValue && transferIds.Contains(item.Id.Value))
                        .Select(item => item.Id!.Value)
                        .ToHashSet();
                    var masterCompletedDeliveryJars = plan.MasterSteps[^1].After
                        .Where(item => item.Id.HasValue &&
                            transferIds.Contains(item.Id.Value) &&
                            !expectedIds.Contains(item.Id.Value))
                        // 交接只改变工作台归属，不重置本订单已经消耗的 Jar 操作资格。
                        .Select(item => new WorldPlanJarState(
                            item.Id, item.Shape, item.Liquid, item.IsMarked, item.MasterOrigin));
                    expectedSlave = expectedSlave.Concat(masterCompletedDeliveryJars).ToArray();
                }

                EnsureWorkshopMatches(slave!, expectedSlave,
                    snapshot.Manifest.TransferJarIds.ToHashSet(), null, "从台");
                if (plan.MasterSteps.Count > 0)
                    EnsureWorkshopMatches(master, plan.MasterSteps[^1].After,
                        snapshot.Manifest.MasterJarIds.ToHashSet(),
                        snapshot.Manifest.TransferJarIds.ToHashSet(), "主台");
                else
                    EnsureNoTransferJarsOnMaster(master, snapshot.Manifest.TransferJarIds);
                break;
            }
            default:
                throw new InvalidOperationException("活动执行检查点包含终态或未知阶段。");
        }
    }

    private static void EnsureWorkshopMatches(
        WorkshopRuntimeStateSnapshot workshop,
        IReadOnlyList<WorldPlanJarState> expected,
        HashSet<int> includedIds,
        HashSet<int>? excludedIds,
        string role)
    {
        var expectedById = expected
            .Where(item => item.Id.HasValue && includedIds.Contains(item.Id.Value) &&
                (excludedIds == null || !excludedIds.Contains(item.Id.Value)))
            .ToDictionary(item => item.Id!.Value);
        var actualById = workshop.Jars
            .Where(item => includedIds.Contains(item.Id!.Value) &&
                (excludedIds == null || !excludedIds.Contains(item.Id!.Value)))
            .ToDictionary(item => item.Id!.Value);
        if (actualById.Count != expectedById.Count)
            throw new InvalidOperationException(
                role + "参与罐子数量与执行计划游标不一致：expected=[" +
                string.Join(",", expectedById.Keys.OrderBy(id => id)) + "]，actual=[" +
                string.Join(",", actualById.Keys.OrderBy(id => id)) + "]。");
        foreach (var pair in expectedById)
        {
            if (!actualById.TryGetValue(pair.Key, out var actual) ||
                actual.Shape != pair.Value.Shape ||
                actual.Liquid != pair.Value.Liquid ||
                actual.IsMarked != pair.Value.IsMarked)
                throw new InvalidOperationException(role + "罐子 #" + pair.Key +
                    " 的状态与执行计划游标不一致。");
        }
    }

    private static void EnsureTransferJarsRemainOnMaster(
        WorkshopRuntimeStateSnapshot master, IReadOnlyList<int> transferJarIds)
    {
        var ids = master.Jars.Select(item => item.Id!.Value).ToHashSet();
        if (transferJarIds.Any(id => !ids.Contains(id)))
            throw new InvalidOperationException("泵送前主台缺少计划要求转移的罐子。");
    }

    private static void EnsureNoTransferJarsOnMaster(
        WorkshopRuntimeStateSnapshot master, IReadOnlyList<int> transferJarIds)
    {
        var ids = master.Jars.Select(item => item.Id!.Value).ToHashSet();
        if (transferJarIds.Any(ids.Contains))
            throw new InvalidOperationException("泵送后仍有应转移罐子留在主台。");
    }

    /// <summary>
    /// 从已恢复的资源/工作台状态重建会话游标。此路径不重新准入、不扣液、不泵送、
    /// 不执行 MATCH；它只恢复已发生的执行进度。
    /// </summary>
    internal static WorldOrderExecutionSession RestoreProgress(
        WorldOrderExecutionSessionProgressSnapshot snapshot,
        OrderDemandBoard board,
        WorldOrderSolveBook solveBook,
        WorldOrderExecutionAdmission admission,
        WorkshopRoster roster,
        WorldResourceReservationBook reservations,
        ResourceLedger? resourceLedger,
        Func<int>? resourceLedgerTick)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!admission.IsBoundTo(board, solveBook, roster))
            throw new ArgumentException("恢复会话的需求板、求解簿、准入器与岗位不属于同一世界。");
        if (board.Find(snapshot.DemandId) == null)
            throw new InvalidOperationException("恢复会话对应的订单不在需求板中。");
        if (snapshot.Stage is WorldOrderExecutionStage.Completed or WorldOrderExecutionStage.Failed or
            WorldOrderExecutionStage.Interrupted or WorldOrderExecutionStage.Cancelled)
            throw new InvalidOperationException("执行会话快照的阶段无效。");
        if (snapshot.UnitDurationLogicalMilli <= 0 ||
            snapshot.UnitElapsedLogicalMilli < 0 ||
            snapshot.UnitElapsedLogicalMilli >= snapshot.UnitDurationLogicalMilli)
            throw new InvalidOperationException("执行会话快照的逻辑时间进度无效。");
        ExecutionProgressInvariants.Validate(
            snapshot.Stage,
            snapshot.CompletedMasterSteps,
            snapshot.Plan.MasterSteps.Count,
            snapshot.CompletedSlaveSteps,
            snapshot.Plan.SlaveSteps.Count,
            snapshot.Manifest.IsPartitioned,
            snapshot.SlaveTransferCommitted,
            snapshot.Reason);

        var reservation = reservations.Find(snapshot.DemandId)
            ?? throw new InvalidOperationException("恢复执行会话缺少资源预留。");
        if (reservation.UsesSlave != snapshot.Manifest.IsPartitioned ||
            !string.Equals(reservation.WorldVersion, snapshot.Manifest.WorldVersion, StringComparison.Ordinal) ||
            !reservation.LockedJarIds.ToHashSet().SetEquals(snapshot.Manifest.LockedJarIds) ||
            !LiquidBalancesEqual(reservation.MasterInitialLiquidAmounts,
                snapshot.Manifest.MasterInitialLiquidAmounts) ||
            !LiquidBalancesEqual(reservation.TransferLiquidAmounts,
                snapshot.Manifest.TransferLiquids) ||
            reservation.MasterStagingPeakCapacity != snapshot.Manifest.MasterStagingPeakCapacity ||
            reservation.SlaveStagingPeakCapacity != snapshot.Manifest.SlaveStagingPeakCapacity)
            throw new InvalidOperationException("执行会话快照与资源租约的版本、罐子、液体交接或容量配额不一致。");
        var scopes = admission.GetLiquidScopes(snapshot.DemandId);
        if (!scopes.HasValue || scopes.Value.Master.OrderId != snapshot.DemandId)
            throw new InvalidOperationException("执行会话快照缺少订单主台液体范围。");

        var masterReservations = scopes.Value.Master.Staging.Capture().ReservedCapacityByOrder;
        var hasMasterReservation = masterReservations.TryGetValue(snapshot.DemandId, out var masterQuota);
        var expectsMasterReservation = snapshot.Stage != WorldOrderExecutionStage.SlaveSteps;
        if (hasMasterReservation != expectsMasterReservation ||
            (hasMasterReservation && masterQuota != snapshot.Manifest.MasterStagingPeakCapacity))
            throw new InvalidOperationException("执行会话阶段与主台容量预留状态不一致。");

        if (snapshot.Manifest.IsPartitioned)
        {
            if (scopes.Value.Slave == null || scopes.Value.Slave.OrderId != snapshot.DemandId ||
                !scopes.Value.Slave.Staging.Capture().ReservedCapacityByOrder.TryGetValue(
                    snapshot.DemandId, out var slaveQuota) ||
                slaveQuota != snapshot.Manifest.SlaveStagingPeakCapacity)
                throw new InvalidOperationException("B 类执行会话与从台容量预留不一致。");
        }
        else if (scopes.Value.Slave != null)
        {
            throw new InvalidOperationException("A 类执行会话不能恢复从台液体范围。");
        }

        var masterIndex = snapshot.CompletedMasterSteps;
        var slaveIndex = snapshot.CompletedSlaveSteps;
        var leaseStage = snapshot.Stage switch
        {
            WorldOrderExecutionStage.MasterSteps => OrderResourceLeaseStage.MasterExecuting,
            WorldOrderExecutionStage.Transfer => OrderResourceLeaseStage.TransferPending,
            WorldOrderExecutionStage.SlaveSteps => OrderResourceLeaseStage.SlaveExecuting,
            _ => throw new InvalidOperationException("无法从活动会话阶段恢复资源租约。")
        };
        if (reservation.Stage != leaseStage)
            throw new InvalidOperationException("执行会话阶段与资源租约生命周期阶段不一致。");

        var restored = new WorldOrderExecutionSession(
            snapshot.DemandId, board, solveBook, admission, roster,
            snapshot.Manifest, snapshot.Plan, snapshot.UnitDurationLogicalMilli,
            resourceLedger, resourceLedgerTick, initializeExecution: false)
        {
            Stage = snapshot.Stage,
            _masterIndex = snapshot.CompletedMasterSteps,
            _slaveIndex = snapshot.CompletedSlaveSteps,
            _unitElapsedLogicalMilli = snapshot.UnitElapsedLogicalMilli,
            _slaveTransferCommitted = snapshot.SlaveTransferCommitted,
            Reason = snapshot.Reason
        };
        return restored;
    }

    /// <summary>
    /// 先执行既有准入（重新验证快照并预留资源），再建立会话。
    /// logicalTimePerUnit 必须由世界装配/执行策略明确提供；本类不猜测旧 tick 与逻辑时间的换算。
    /// </summary>
    // 保留原内部调用形状；未配置账本的独立测试/宿主继续使用无账本执行。
    internal static WorldOrderExecutionStartResult TryStart(
        int demandId,
        OrderDemandBoard board,
        WorldOrderSolveBook solveBook,
        WorldOrderExecutionAdmission admission,
        WorkshopRoster roster,
        long logicalTimePerUnit) =>
        TryStart(demandId, board, solveBook, admission, roster, logicalTimePerUnit, null, null);

    internal static WorldOrderExecutionStartResult TryStart(
        int demandId,
        OrderDemandBoard board,
        WorldOrderSolveBook solveBook,
        WorldOrderExecutionAdmission admission,
        WorkshopRoster roster,
        long logicalTimePerUnit,
        ResourceLedger? resourceLedger,
        Func<int>? resourceLedgerTick)
    {
        if (board == null) throw new ArgumentNullException(nameof(board));
        if (solveBook == null) throw new ArgumentNullException(nameof(solveBook));
        if (admission == null) throw new ArgumentNullException(nameof(admission));
        if (roster == null) throw new ArgumentNullException(nameof(roster));
        if (logicalTimePerUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalTimePerUnit),
                "每个执行单位的逻辑时间必须大于零");

        if (!admission.IsBoundTo(board, solveBook, roster))
            throw new ArgumentException("需求板、求解簿、准入器与工作台岗位必须属于同一世界");

        var demand = board.Find(demandId);
        if (demand == null)
            return WorldOrderExecutionStartResult.Rejected("订单已不在需求板中");

        var admitted = admission.TryAdmit(demandId);
        if (!admitted.Success || admitted.Manifest == null)
            return WorldOrderExecutionStartResult.Rejected(admitted.Reason ?? "订单执行准入失败");

        try
        {
            var record = solveBook.Find(demandId);
            var result = record?.Result;
            if (record?.State != WorldOrderSolveState.Current ||
                result == null || !result.IsFeasible || result.Plan == null)
                throw new InvalidOperationException("准入后找不到当前有效的不可变求解计划");

            if (result.DemandId != demandId ||
                !string.Equals(result.WorldVersion, admitted.Manifest.WorldVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("准入资源清单与当前求解结果版本不一致");

            var session = new WorldOrderExecutionSession(
                demandId, board, solveBook, admission, roster,
                admitted.Manifest, result.Plan, logicalTimePerUnit,
                resourceLedger, resourceLedgerTick);

            // 只有新自机液库路径才记录这笔实际扣款；旧兼容准入不伪造新的资源流向。
            if (resourceLedger != null && admission.ShipLiquidInventory != null)
            {
                var tick = resourceLedgerTick?.Invoke() ?? 0;
                foreach (var item in admitted.Manifest.MasterInitialLiquidAmounts)
                    resourceLedger.Record(tick, ResourceCauses.Flow, ResourceKinds.Liquid,
                        item.Key, item.Value, from: "自机液库", to: roster.Master!.Name);
            }
            return WorldOrderExecutionStartResult.Started(session);
        }
        catch
        {
            admission.Release(demandId);
            throw;
        }
    }

    /// <summary>
    /// 用世界逻辑时间推进执行。时间不足一个单位时只累计进度，不提前执行。
    /// 大步长可连续完成多个单位，但每个单位仍按计划顺序执行。
    /// </summary>
    internal void AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));
        if (IsTerminal || deltaLogicalTimeMilli == 0) return;

        var remaining = deltaLogicalTimeMilli;
        while (remaining > 0 && !IsTerminal)
        {
            var needed = _unitDurationLogicalMilli - _unitElapsedLogicalMilli;
            var consumed = Math.Min(remaining, needed);
            _unitElapsedLogicalMilli = checked(_unitElapsedLogicalMilli + consumed);
            remaining -= consumed;

            if (_unitElapsedLogicalMilli < _unitDurationLogicalMilli)
                continue;

            _unitElapsedLogicalMilli = 0;
            try
            {
                AdvanceOneUnit();
            }
            catch
            {
                // 领域端口已成功但后置条件违约时，单步执行器会抛 invariant exception。
                // 先结束会话、清理临时从台资源并释放预留，再把异常交给宿主。
                TerminateFailure("执行过程中发生不变量异常");
                throw;
            }
        }
    }

    /// <summary>
    /// 会话已准入但尚未执行任何计划步骤时，因重复订单会话或工作台替换而取消。
    /// 不进入 CD，因为这张订单从未真正开始执行；只标记 Dirty 并释放刚取得的预留。
    /// </summary>
    internal bool CancelBeforeExecution(string reason)
    {
        if (IsTerminal) return true;
        if (_masterIndex != 0 || _slaveIndex != 0 || _slaveTransferCommitted)
            return false;

        if (_board.Find(DemandId) != null)
            _solveBook.MarkDirty(DemandId, reason);
        ReleaseReservation();
        Reason = reason;
        Stage = WorldOrderExecutionStage.Cancelled;
        return true;
    }

    /// <summary>
    /// 工作台替换的中断适配器。成功返回时会话已终止、从台临时资源已结算、订单已进入 CD。
    /// 已完成的加工与消耗不回滚。
    /// </summary>
    public (bool Success, string? Error) InterruptForWorkshopReplacement()
    {
        if (IsTerminal) return (true, null);

        try
        {
            var settlementTick = AcquireSettlementTick();
            CleanupTemporaryResources(settlementTick);
            _board.StartCooldown(DemandId);
            if (_board.Find(DemandId) != null &&
                _board.Find(DemandId)!.ReadyForSolve)
                _solveBook.MarkDirty(DemandId, "订单被工作台替换中断；重新检查当前世界");
            ReleaseReservation(settlementTick);
            Reason = "工作台替换导致订单执行中断";
            Stage = WorldOrderExecutionStage.Interrupted;
            return (true, null);
        }
        catch (Exception ex)
        {
            // 清理结果无法确认时不能向替换流程谎报成功；调用方会按不确定结果终止替换。
            throw new InvalidOperationException("订单中断清理失败，不能继续工作台替换: " + ex.Message, ex);
        }
    }

    private void AdvanceOneUnit()
    {
        if (_board.Find(DemandId) == null)
        {
            TerminateFailure("订单已从需求板移除");
            return;
        }

        if (!ReferenceEquals(_roster.Master, _master) ||
            (_manifest.IsPartitioned && !ReferenceEquals(_roster.Slave, _slave)))
        {
            TerminateFailure("订单执行期间主从工作台岗位发生变化");
            return;
        }

        switch (Stage)
        {
            case WorldOrderExecutionStage.MasterSteps:
                ApplyNextStep(_master!, _masterSteps, ref _masterIndex, "master");
                if (IsTerminal) return;
                if (_masterIndex == _masterSteps.Count)
                {
                    if (_manifest.IsPartitioned)
                    {
                        Stage = WorldOrderExecutionStage.Transfer;
                        _admission.Reservations.MarkTransferPending(DemandId);
                    }
                    else
                        CompleteSuccess();
                }
                break;

            case WorldOrderExecutionStage.Transfer:
                var transferError = TransferToSlave();
                if (transferError != null)
                {
                    TerminateFailure(transferError);
                    return;
                }
                // 交接只迁移 Jar 身份与所有权，不清除操作标记。
                // 已在主台操作过的 Jar 在本订单剩余阶段仍然不可再次操作。
                _admission.Reservations.MarkTransferCommitted(DemandId);
                Stage = WorldOrderExecutionStage.SlaveSteps;
                _admission.Reservations.MarkSlaveExecuting(DemandId);
                PumpInstantMatches(_slave!, _slaveSteps, ref _slaveIndex, "slave", _slaveLiquidScope);
                if (!IsTerminal && _slaveIndex == _slaveSteps.Count)
                    CompleteSuccess();
                break;

            case WorldOrderExecutionStage.SlaveSteps:
                ApplyNextStep(_slave!, _slaveSteps, ref _slaveIndex, "slave");
                if (IsTerminal) return;
                if (_slaveIndex == _slaveSteps.Count)
                    CompleteSuccess();
                break;

            default:
                throw new InvalidOperationException("当前执行阶段不能推进: " + Stage);
        }
    }

    private void ApplyNextStep(
        Workshop workshop,
        IReadOnlyList<WorldPlanStep> steps,
        ref int index,
        string phase)
    {
        if (index >= steps.Count)
            throw new InvalidOperationException(phase + " 执行索引越界");

        var scope = phase == "master" ? _masterLiquidScope : _slaveLiquidScope;
        var result = scope == null
            ? _applier.Apply(workshop, steps[index])
            : _applier.Apply(workshop, steps[index], scope);
        if (!result.Success)
        {
            TerminateFailure(phase + " 步骤 " + steps[index].Step + " 执行失败: " +
                (result.Reason ?? "领域操作拒绝但未提供原因"));
            return;
        }

        index++;
        PumpInstantMatches(workshop, steps, ref index, phase, scope);
    }

    /// <summary>按计划顺序立即应用当前位置连续的 MATCH；MATCH 不消耗逻辑时间。</summary>
    private void PumpInstantMatches(
        Workshop workshop,
        IReadOnlyList<WorldPlanStep> steps,
        ref int index,
        string phase,
        OrderLiquidScope? scope)
    {
        while (index < steps.Count && IsMatch(steps[index]))
        {
            var result = scope == null
                ? _applier.Apply(workshop, steps[index])
                : _applier.Apply(workshop, steps[index], scope);
            if (!result.Success)
            {
                TerminateFailure(phase + " MATCH 步骤 " + steps[index].Step + " 执行失败: " +
                    (result.Reason ?? "MATCH 领域操作拒绝但未提供原因"));
                return;
            }
            index++;
        }
    }

    /// <summary>
    /// B 类主台→从台交接：核对主台终态，再预检从台所需批次，最后执行 Jar 与液体转移。
    /// 任何预检失败都不移动一只罐子、不扣一份液体。
    /// </summary>
    private string? TransferToSlave() =>
        _transferService.Transfer(
            DemandId,
            _admission.Reservations.FindLease(DemandId)
                ?? throw new InvalidOperationException("B 类交接缺少订单资源租约"),
            _manifest,
            _master,
            _slave ?? throw new InvalidOperationException("B 类执行缺少从台"),
            _masterLiquidScope,
            _slaveLiquidScope,
            _resourceLedger,
            _resourceLedgerTick,
            () => _slaveTransferCommitted = true);

    private void CompleteSuccess()
    {
        var deliveryWorkshop = _manifest.IsPartitioned ? _slave! : _master;

        // 账本 tick 来源可能由 Runtime 管理，也可能由旧宿主显式提供。必须在任何交付罐标记/归属发生变化前取得，
        // 避免交付已移除后，结算才调用外部 tick provider 并抛出异常。
        int? settlementTick = AcquireSettlementTick();

        // 先完整验证交付集合，再统一移除，避免前几个交付罐已离台后才发现后续 ID 缺失。
        var deliveryJars = new List<Jar>();
        foreach (var id in _deliveryJarIds)
        {
            var matches = deliveryWorkshop.Jars.Where(jar => jar.Id == id).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"订单交付罐 #{id} 在交付工作台中不存在或身份重复");
            deliveryJars.Add(matches[0]);
        }

        // 交付罐也属于本订单锁定的参与资源。先通过工作台领域入口清除标记，
        // 再将罐子移出工作台；否则后续 ClearParticipantMarks 已经找不到它，
        // 会把 IsMarked 状态带到交付边界之外。
        foreach (var jar in deliveryJars)
        {
            var clear = deliveryWorkshop.ClearMark(jar);
            if (!clear.Success)
                throw new InvalidOperationException(
                    clear.Reason ?? $"无法清理交付罐 #{jar.Id} 的操作标记");
        }

        foreach (var jar in deliveryJars)
        {
            if (deliveryWorkshop.RemoveJar(jar) == null)
                throw new InvalidOperationException($"订单交付罐 #{jar.Id} 无法从工作台移除");
        }

        var completedDemand = _board.Find(DemandId)
            ?? throw new InvalidOperationException("执行成功时订单已不在需求板中");

        // 成功事件必须晚于所有资源结算与预留释放。这样下游只能观察到完整提交的周期，
        // 不会因释放阶段失败而提前触发 Space Buff/效果。
        CleanupTemporaryResources(settlementTick);
        ReleaseReservation(settlementTick);
        if (_board.RecordSuccessfulFulfillment(DemandId) == DemandFulfillmentResult.NotFound)
            throw new InvalidOperationException("执行成功提交时订单已不在需求板中");
        _board.AppendSuccessfulCycleEvent(completedDemand);

        Reason = null;
        Stage = WorldOrderExecutionStage.Completed;
    }

    private void TerminateFailure(string reason)
    {
        if (IsTerminal) return;
        int? settlementTick = null;
        try
        {
            settlementTick = AcquireSettlementTick();
            CleanupTemporaryResources(settlementTick);
            if (_board.Find(DemandId) != null)
                _solveBook.MarkDirty(DemandId, "订单执行失败，需按当前世界重新求解：" + reason);
        }
        finally
        {
            ReleaseReservation(settlementTick);
            Reason = reason;
            Stage = WorldOrderExecutionStage.Failed;
        }
    }

    private void CleanupTemporaryResources(int? ledgerTickOverride = null)
    {
        // 只有本会话已经通过从台所需液体批次预检并取得从台资源所有权，才允许处理从台回滚。
        // 若转移前发现从台被外部占用，失败路径必须原样保留那些外部资源与标记。
        if (_manifest.IsPartitioned && _slave != null && _slaveTransferCommitted)
        {
            if (_slave.HasResources)
            {
                Func<int>? tickOf = ledgerTickOverride.HasValue
                    ? () => ledgerTickOverride.Value
                    : _resourceLedgerTick;
                _slaveRollback.Execute(
                    _master, _slave,
                    entry => _resourceSettlementLog.Add(entry),
                    ledger: _resourceLedger,
                    tickOf: tickOf,
                    ledgerTickOverride: ledgerTickOverride);
            }
            _slaveTransferCommitted = false;
        }

        ClearParticipantMarks();
    }

    /// <summary>
    /// 只清理本订单资源清单中的罐子标记。其他订单/外部系统持有的罐子标记不属于本会话。
    /// B 类资源回流后，参与罐子可能全部位于主台；交付罐可能已被移除。
    /// </summary>
    private void ClearParticipantMarks()
    {
        foreach (var jarId in _manifest.LockedJarIds)
        {
            var masterMatches = _master.Jars.Where(jar => jar.Id == jarId).ToArray();
            var slaveMatches = _slave?.Jars.Where(jar => jar.Id == jarId).ToArray()
                ?? Array.Empty<Jar>();

            if (masterMatches.Length + slaveMatches.Length > 1)
                throw new InvalidOperationException($"清理标记时发现参与罐子 #{jarId} 在多个工作台重复存在");

            if (masterMatches.Length == 1)
            {
                var clear = _master.ClearMark(masterMatches[0]);
                if (!clear.Success)
                    throw new InvalidOperationException(clear.Reason ?? $"无法清理主台罐子 #{jarId} 的标记");
            }
            else if (slaveMatches.Length == 1)
            {
                var clear = _slave!.ClearMark(slaveMatches[0]);
                if (!clear.Success)
                    throw new InvalidOperationException(clear.Reason ?? $"无法清理从台罐子 #{jarId} 的标记");
            }
        }
    }

    private int? AcquireSettlementTick()
    {
        if (_resourceLedger == null) return null;
        var scopes = _admission.GetLiquidScopes(DemandId);
        var hasLiquidBalance = scopes.HasValue &&
            (scopes.Value.Master.Balance.Values.Any(value => value > 0) ||
             (scopes.Value.Slave?.Balance.Values.Any(value => value > 0) ?? false));
        var needsSlaveRollback = _manifest.IsPartitioned &&
            _slaveTransferCommitted && _slave?.HasResources == true;
        return hasLiquidBalance || needsSlaveRollback
            ? _resourceLedgerTick!()
            : null;
    }

    private void ReleaseReservation(int? settlementTickOverride = null)
    {
        if (_reservationReleased) return;
        var scopes = _admission.GetLiquidScopes(DemandId);
        var hasBalance = scopes.HasValue &&
            (scopes.Value.Master.Balance.Values.Any(value => value > 0) ||
             (scopes.Value.Slave?.Balance.Values.Any(value => value > 0) ?? false));
        var settlementTick = _resourceLedger != null && hasBalance
            ? settlementTickOverride ?? _resourceLedgerTick!()
            : (int?)null;

        _settlementService.SettleAndRelease(
            DemandId, _admission, _resourceLedger, settlementTick);
        _reservationReleased = true;
    }

    private static bool LiquidBalancesEqual(
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyDictionary<string, int> expected)
    {
        var keys = actual.Keys.Concat(expected.Keys).Distinct(StringComparer.Ordinal);
        return keys.All(key => actual.GetValueOrDefault(key) == expected.GetValueOrDefault(key));
    }

    private static bool IsMatch(WorldPlanStep step) =>
        string.Equals(step.Operation?.Type, "MATCH", StringComparison.Ordinal);

    private static IEnumerable<int> ReadDeliveryJarIds(WorldOrderSolvePlan plan)
    {
        foreach (var step in plan.MasterSteps.Concat(plan.SlaveSteps))
        {
            var index = step.Operation?.JarIndex;
            if (!index.HasValue || index.Value < 0 ||
                index.Value >= step.Before.Count || index.Value >= step.After.Count)
                throw new InvalidOperationException("求解步骤的 JarIndex 无效，无法识别交付罐");

            if (string.IsNullOrWhiteSpace(step.RequirementShape))
                throw new InvalidOperationException("求解步骤没有绑定需求形状，无法识别交付罐");

            var target = step.After[index.Value];
            if (target.Shape != step.RequirementShape ||
                target.Liquid != step.RequirementLiquid)
                continue;

            if (!target.IsMarked || target.Id is not > 0)
                throw new InvalidOperationException("满足需求的步骤终态缺少有效罐子 ID 或操作标记");
            yield return target.Id.Value;
        }
    }
}

public enum WorldOrderExecutionStage
{
    MasterSteps,
    Transfer,
    SlaveSteps,
    Completed,
    Failed,
    Interrupted,
    Cancelled
}

public sealed record WorldOrderExecutionStartResult(
    bool Success,
    string? Reason,
    WorldOrderExecutionSession? Session)
{
    internal static WorldOrderExecutionStartResult Rejected(string reason) =>
        new(false, reason, null);

    internal static WorldOrderExecutionStartResult Started(WorldOrderExecutionSession session) =>
        new(true, null, session);
}


/// <summary>订单会话执行游标快照；不包含罐子状态，不能单独作为完整世界存档。</summary>
public sealed class WorldOrderExecutionSessionProgressSnapshot
{
    internal WorldOrderExecutionSessionProgressSnapshot(
        int demandId,
        WorldOrderExecutionStage stage,
        int completedMasterSteps,
        int completedSlaveSteps,
        long unitElapsedLogicalMilli,
        long unitDurationLogicalMilli,
        bool slaveTransferCommitted,
        string? reason,
        WorldOrderResourceManifest manifest,
        WorldOrderSolvePlan plan)
    {
        DemandId = demandId;
        Stage = stage;
        CompletedMasterSteps = completedMasterSteps;
        CompletedSlaveSteps = completedSlaveSteps;
        UnitElapsedLogicalMilli = unitElapsedLogicalMilli;
        UnitDurationLogicalMilli = unitDurationLogicalMilli;
        SlaveTransferCommitted = slaveTransferCommitted;
        Reason = reason;
        Manifest = manifest;
        Plan = plan;
    }

    public int DemandId { get; }
    public WorldOrderExecutionStage Stage { get; }
    public int CompletedMasterSteps { get; }
    public int CompletedSlaveSteps { get; }
    public long UnitElapsedLogicalMilli { get; }
    public long UnitDurationLogicalMilli { get; }
    public bool SlaveTransferCommitted { get; }
    public string? Reason { get; }
    public WorldOrderResourceManifest Manifest { get; }
    public WorldOrderSolvePlan Plan { get; }
}

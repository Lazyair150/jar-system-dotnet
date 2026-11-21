using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 的持续运行时：统一推进逻辑时间、需求 CD、岗位替换、资源生产与已登记订单会话。
/// 替换活动期间继续生产，但关闭仓库到主台的流入；替换开始时先中断全部已登记订单会话。
/// </summary>
public sealed class ResourceWorldRuntime
{
    public WorldClock Clock { get; }
    public ResourceArea Resources { get; }
    public WorkshopRoster? Roster { get; }
    public OrderDemandBoard? DemandBoard { get; }
    public WorkshopReplacementProcess? ActiveReplacement { get; private set; }
    /// <summary>首次发生无法确认世界状态的异常后置为 true；运行时不会自动恢复。</summary>
    public bool IsFaulted => _faultException != null;
    /// <summary>世界故障的首次原因；故障状态为 sticky，不能由后续异常覆盖。</summary>
    public string? FaultReason { get; private set; }
    /// <summary>导致世界进入故障状态的首次异常。</summary>
    public Exception? FaultException => _faultException;
    private Exception? _faultException;
    internal bool BlocksOrderAdmission => IsFaulted || ReplacementBlocksWorld;

    private bool ReplacementBlocksWorld =>
        ActiveReplacement is { IsFinished: false } or
            { InterruptionOutcomeUncertain: true } or
            { PrefillRollbackOutcomeUncertain: true } or
            { PrefillCommitOutcomeUncertain: true };

    private readonly List<WorldOrderExecutionSession> _executionSessions = new();
    private readonly IReadOnlyList<WorldOrderExecutionSession> _executionSessionsView;
    /// <summary>当前仍在推进的订单执行会话（只读视图）。</summary>
    public IReadOnlyList<WorldOrderExecutionSession> ActiveExecutionSessions => _executionSessionsView;

    /// <summary>当前账本逻辑 tick 游标；检查点恢复后继续递增。</summary>
    internal int LedgerTick => _ledgerTick;

    /// <summary>
    /// 恢复活动执行会话。仅允许装入空 Runtime；会话游标由快照恢复，不调用 TryStart/准入。
    /// 所有会话先在局部构造并验证，成功后才一次性挂入 Runtime。
    /// </summary>
    internal void RestoreExecutionSessions(
        IEnumerable<WorldOrderExecutionSessionProgressSnapshot> snapshots,
        WorldOrderSolveBook solveBook,
        WorldOrderExecutionAdmission admission,
        WorldResourceReservationBook reservations,
        int ledgerTick)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (ledgerTick < 0) throw new ArgumentOutOfRangeException(nameof(ledgerTick));
        ThrowIfFaulted();
        if (_executionSessions.Count != 0 || ReplacementBlocksWorld)
            throw new InvalidOperationException("只能向没有活动会话或工作台替换的 Runtime 恢复执行。");
        if (Roster == null || DemandBoard == null)
            throw new InvalidOperationException("当前 Runtime 缺少需求板或工作台岗位，不能恢复会话。");

        var restored = new List<WorldOrderExecutionSession>();
        var demandIds = new HashSet<int>();
        foreach (var snapshot in snapshots.OrderBy(item => item.DemandId))
        {
            if (!demandIds.Add(snapshot.DemandId))
                throw new InvalidOperationException("执行检查点包含重复活动订单会话。");
            restored.Add(WorldOrderExecutionSession.RestoreProgress(
                snapshot, DemandBoard, solveBook, admission, Roster,
                reservations, _resourceLedger, _resourceLedgerTick));
        }

        _ledgerTick = ledgerTick;
        _executionSessions.AddRange(restored);
    }

    // 世界级资源服务由装配层提供，避免 Runtime 创建缺少账本/对象池依赖的迁徙事件。
    private readonly Func<Workshop, Workshop, WorkshopSwitchEvent>? _masterSwitchFactory;
    private readonly Func<Workshop, WorkshopPrefillTransaction>? _masterPrefill;
    private readonly ResourceLedger? _resourceLedger;
    private readonly Func<int>? _resourceLedgerTick;
    private int _ledgerTick;

    public ResourceWorldRuntime(
        ResourceArea resources,
        WorldClock? clock = null,
        WorkshopRoster? roster = null,
        Func<Workshop, Workshop, WorkshopSwitchEvent>? masterSwitchFactory = null,
        OrderDemandBoard? demandBoard = null,
        ResourceLedger? resourceLedger = null,
        Func<int>? resourceLedgerTick = null,
        Func<Workshop, WorkshopPrefillTransaction>? masterPrefill = null)
    {
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        Clock = clock ?? new WorldClock();
        Roster = roster;
        _masterSwitchFactory = masterSwitchFactory;
        _masterPrefill = masterPrefill;
        DemandBoard = demandBoard;
        if (resourceLedger == null && resourceLedgerTick != null)
            throw new ArgumentException("未配置资源账本时不能单独提供 tick 来源", nameof(resourceLedgerTick));
        _resourceLedger = resourceLedger;
        // 新 World v2 默认使用运行时逻辑推进序号作为账本 tick；
        // 旧宿主仍可显式注入自己的 tick 来源，不在内核中猜测旧 tick 换算。
        _resourceLedgerTick = resourceLedger == null
            ? null
            : resourceLedgerTick ?? (() => _ledgerTick);
        _executionSessionsView = _executionSessions.AsReadOnly();
    }

    /// <summary>
    /// 将已完成准入的订单会话交给世界运行时推进。
    /// 会话使用本次 WorldClock 换算出的同一份逻辑时间，不另设时钟。
    /// </summary>
    internal bool RegisterExecutionSession(WorldOrderExecutionSession session)
    {
        ThrowIfFaulted();
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (Roster == null || DemandBoard == null ||
            !session.IsBoundTo(DemandBoard, Roster))
            throw new ArgumentException("执行会话必须绑定到当前 Runtime 的需求板与工作台岗位", nameof(session));
        if (session.IsTerminal) return false;

        _executionSessions.RemoveAll(existing => existing.IsTerminal);
        if (ReplacementBlocksWorld)
        {
            if (!session.CancelBeforeExecution("工作台替换正在进行或结果不确定；本会话未启动"))
                throw new InvalidOperationException("工作台替换期间收到已经执行过步骤的未登记会话");
            return false;
        }

        if (_executionSessions.Any(existing => existing.DemandId == session.DemandId))
        {
            if (!session.CancelBeforeExecution("该订单已有活动执行会话；本会话未启动"))
                throw new InvalidOperationException("同一订单出现重复且已经推进的执行会话");
            return false;
        }

        _executionSessions.Add(session);
        return true;
    }

    /// <summary>
    /// 推荐的订单启动入口：在执行准入之前检查 Runtime 是否允许开始新会话，
    /// 避免在工作台迁徙或另一张订单执行期间先占用资源。
    /// </summary>
    public WorldOrderExecutionStartResult TryStartExecutionSession(
        int demandId,
        WorldOrderSolveBook solveBook,
        WorldOrderExecutionAdmission admission,
        long logicalTimePerUnit)
    {
        if (IsFaulted)
            return WorldOrderExecutionStartResult.Rejected("世界运行时已故障，不能启动订单：" + FaultReason);
        if (Roster == null || DemandBoard == null)
            return WorldOrderExecutionStartResult.Rejected("当前 Runtime 没有需求板或工作台岗位集合");
        if (solveBook == null) throw new ArgumentNullException(nameof(solveBook));
        if (admission == null) throw new ArgumentNullException(nameof(admission));
        if (logicalTimePerUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalTimePerUnit));
        if (!admission.IsBoundTo(DemandBoard, solveBook, Roster))
            throw new ArgumentException("求解簿、准入器与当前 Runtime 不属于同一世界", nameof(admission));
        if (ReplacementBlocksWorld)
            return WorldOrderExecutionStartResult.Rejected("工作台替换正在进行或结果不确定，不能启动订单");
        _executionSessions.RemoveAll(session => session.IsTerminal);

        try
        {
            var started = WorldOrderExecutionSession.TryStart(
                demandId, DemandBoard, solveBook, admission, Roster, logicalTimePerUnit,
                _resourceLedger, _resourceLedgerTick);
            if (started.Success && started.Session != null)
                RegisterExecutionSession(started.Session);
            return started;
        }
        catch (Exception ex)
        {
            LatchFault(ex, "订单执行会话启动发生异常");
            throw;
        }
    }

    /// <summary>基于已登记岗位推进世界。构造时未提供 Roster 则不能使用此重载。</summary>
    public ResourceWorldStep AdvanceWorldTime(long deltaWorldTimeMilli)
    {
        if (Roster == null)
            throw new InvalidOperationException("当前运行时没有工作台岗位集合");
        return AdvanceCore(deltaWorldTimeMilli, Roster.Master);
    }

    /// <summary>兼容第一阶段调用方式：显式指定本次资源流入目标；null 表示本次不向工作台流入。</summary>
    public ResourceWorldStep AdvanceWorldTime(long deltaWorldTimeMilli, Workshop? master) =>
        AdvanceCore(deltaWorldTimeMilli, master);

    /// <summary>请求主台或从台替换。同一时刻只允许一个岗位替换过程。</summary>
    public (bool Success, string? Error) BeginReplacement(
        WorkshopRole role,
        Workshop target,
        IOrderInterruptionPort interruptionPort,
        long phaseDurationMilli)
    {
        if (IsFaulted)
            return (false, "世界运行时已故障，不能替换工作台：" + FaultReason);
        if (Roster == null)
            return (false, "当前运行时没有工作台岗位集合");
        if (ReplacementBlocksWorld)
            return (false, "已有工作台替换过程正在进行，或上一替换结果仍不确定");

        // 已登记会话也是当前世界中的活动订单。先让外部适配器完成其负责的订单中断，
        // 再逐个中断本 Runtime 持有的会话；任何后续失败都按“不确定”处理，不自动重试。
        var combinedInterruption = new RuntimeOrderInterruptionPort(_executionSessions, interruptionPort);
        Func<Workshop, WorkshopPrefillTransaction>? prefillBeforeMigration = null;
        Action<WorkshopPrefillTransaction>? rollbackPrefill = null;
        if (role == WorkshopRole.Master && _masterPrefill != null)
        {
            prefillBeforeMigration = targetWorkshop =>
            {
                // 先取得 tick，再移动库存；tick provider 异常不能发生在资源扣款之后。
                var tick = _resourceLedger == null ? 0 : _resourceLedgerTick!();
                WorkshopPrefillTransaction transaction;
                try
                {
                    transaction = _masterPrefill(targetWorkshop);
                }
                catch (AggregateException creationRollbackError)
                {
                    throw new WorkshopPrefillRollbackException(
                        "主台预置账户创建失败，且内部扣款回滚结果不确定。",
                        creationRollbackError);
                }
                try
                {
                    if (_resourceLedger != null)
                    {
                        foreach (var item in transaction.Receipt.Accepted)
                            _resourceLedger.Record(tick, ResourceCauses.Flow, ResourceKinds.Liquid,
                                item.Key, item.Value, from: "自机液库", to: "主台预置",
                                reason: "主工作台切换前置补液");
                    }
                    return transaction;
                }
                catch (Exception prefillError)
                {
                    try
                    {
                        transaction.Rollback();
                    }
                    catch (Exception rollbackError)
                    {
                        throw new WorkshopPrefillRollbackException(
                            "主台前置补液账本记录失败，且库存回滚也失败。",
                            new AggregateException(prefillError, rollbackError));
                    }
                    throw;
                }
            };
            rollbackPrefill = transaction =>
            {
                // 回滚 tick 也必须在资源回滚前取得；容量预留保证生产不会占用返还空间。
                var tick = _resourceLedger == null ? 0 : _resourceLedgerTick!();
                transaction.Rollback();
                if (_resourceLedger != null)
                {
                    foreach (var item in transaction.Receipt.Accepted)
                        _resourceLedger.Record(tick, ResourceCauses.Return, ResourceKinds.Liquid,
                            item.Key, item.Value, from: "主台预置", to: "自机液库",
                            reason: "主工作台切换回滚");
                }
            };
        }
        var process = new WorkshopReplacementProcess(
            Roster, role, target, combinedInterruption, phaseDurationMilli,
            _masterSwitchFactory, prefillBeforeMigration, rollbackPrefill);
        var result = process.Begin();
        // 即使 Begin 失败，只要订单中断已提交或结果不确定，也保留终态过程供外部检查。
        if (result.Success || process.InterruptionCommitted || process.InterruptionOutcomeUncertain)
            ActiveReplacement = process;
        if (process.PrefillRollbackOutcomeUncertain)
            LatchFault(new InvalidOperationException(process.FailureReason ??
                "主台前置补液回滚结果不确定。"),
                "主台前置补液回滚失败；世界资源状态可能不完整");

        // 中断结果不确定或前置补液回滚结果不确定时，禁止继续推进世界。
        if (process.InterruptionOutcomeUncertain ||
            (process.InterruptionCommitted && !result.Success))
        {
            LatchFault(
                new InvalidOperationException(process.FailureReason ?? result.Error ?? "工作台替换提交边界异常"),
                "工作台替换发生不可恢复故障");
        }
        return result;
    }

    /// <summary>
    /// 取消当前尚未完成的工作台替换。迁徙状态由替换过程恢复；
    /// 已提交的订单中断是不可逆副作用，不会随迁徙回滚。
    /// </summary>
    public (bool Success, string? Error) CancelActiveReplacement(
        string reason = "工作台替换已取消")
    {
        if (IsFaulted)
            return (false, "世界运行时已故障，不能取消替换：" + FaultReason);
        if (ActiveReplacement == null)
            return (false, "当前没有工作台替换过程");
        if (ActiveReplacement.IsFinished)
            return (false, "工作台替换过程已经结束");

        try
        {
            ActiveReplacement.Cancel(reason);
            return ActiveReplacement.IsFinished && !ActiveReplacement.Succeeded
                ? (true, null)
                : (false, "替换过程未能确认取消结果");
        }
        catch (Exception ex)
        {
            LatchFault(ex, "取消工作台替换时迁徙回滚失败；世界状态可能不完整");
            return (false, FaultReason);
        }
    }

    private ResourceWorldStep AdvanceCore(long deltaWorldTimeMilli, Workshop? requestedMaster)
    {
        ThrowIfFaulted();
        if (deltaWorldTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaWorldTimeMilli));

        try
        {
            return AdvanceCoreUnsafe(deltaWorldTimeMilli, requestedMaster);
        }
        catch (Exception ex)
        {
            LatchFault(ex, "世界推进过程中发生异常；本次推进可能已部分生效");
            throw;
        }
    }

    private ResourceWorldStep AdvanceCoreUnsafe(long deltaWorldTimeMilli, Workshop? requestedMaster)
    {
        var logicalDelta = Clock.Advance(deltaWorldTimeMilli);
        if (logicalDelta > 0)
            _ledgerTick = checked(_ledgerTick + 1);

        // CD 与资源生产、订单执行使用同一份逻辑时间；暂停/零流速时 logicalDelta 为 0。
        DemandBoard?.AdvanceLogicalTime(logicalDelta);

        var replacementWasActive = ActiveReplacement is { IsFinished: false };
        if (replacementWasActive)
            ActiveReplacement!.AdvanceLogicalTime(logicalDelta);
        var replacementOutcomeUncertain = ActiveReplacement is { InterruptionOutcomeUncertain: true };
        var replacementBlocksWorld = replacementWasActive || replacementOutcomeUncertain;

        // 一个世界推进片段只要与替换过程有重叠，就全片段暂停罐子流入。
        // 生产仍然推进：罐子留在仓库，液体进入自机液库；替换完成后的下一片段才向新主台流入。
        // 不在这里重新读取 Roster.Master：无参重载已在入口处解析岗位，
        // 显式参数重载则必须保留调用者指定的目标（包括 null）。
        var effectiveMaster = replacementBlocksWorld ? null : requestedMaster;

        var resources = Resources.Advance(logicalDelta, effectiveMaster);
        RecordLiquidProduction(resources);

        // 执行会话与生产、CD 共用同一个逻辑时间增量。先推进本片段的生产，
        // 再推进已预留资源上的订单步骤；会话自身仍按步骤时长控制实际操作时点。
        if (!replacementBlocksWorld)
        {
            foreach (var session in _executionSessions.ToArray())
            {
                try
                {
                    session.AdvanceLogicalTime(logicalDelta);
                }
                finally
                {
                    if (session.IsTerminal)
                        _executionSessions.Remove(session);
                }
            }
        }

        return new ResourceWorldStep(logicalDelta, resources);
    }

    private void RecordLiquidProduction(ResourceAreaAdvanceResult result)
    {
        if (_resourceLedger == null || result.LiquidProductionReceipts.Count == 0)
            return;

        var tick = _resourceLedgerTick!();
        var destination = Resources.ShipLiquidInventory == null ? "中央仓库" : "自机液库";
        foreach (var receipt in result.LiquidProductionReceipts)
        {
            // 生产事件只记录真正进入库存的数量；未接收部分单独记 discard。
            // 两者之和等于 ProducedAmount，避免同一份液体被重复计量。
            _resourceLedger.Record(tick, ResourceCauses.Production, ResourceKinds.Liquid,
                receipt.Liquid, receipt.AcceptedAmount,
                from: "液体生产区", to: destination);
            _resourceLedger.Record(tick, ResourceCauses.Discard, ResourceKinds.Liquid,
                receipt.Liquid, receipt.DiscardedAmount,
                from: "液体生产区", to: null,
                reason: "自机液库或资源库存容量不足");
        }
    }

    private void ThrowIfFaulted()
    {
        if (IsFaulted)
            throw new InvalidOperationException(
                "世界运行时已故障并被冻结：" + FaultReason, _faultException);
    }

    private void LatchFault(Exception exception, string context)
    {
        if (_faultException != null) return;
        _faultException = exception ?? throw new ArgumentNullException(nameof(exception));
        FaultReason = context + "：" + exception.Message;
    }

    /// <summary>
    /// 把外部订单中断适配器与 Runtime 已登记的执行会话合并。
    /// 先调用外部适配器，确保它拒绝时本 Runtime 尚未产生中断副作用；
    /// 外部适配器成功后，若某个会话清理失败则抛异常，让替换流程按结果不确定处理。
    /// </summary>
    private sealed class RuntimeOrderInterruptionPort : IOrderInterruptionPort
    {
        private readonly List<WorldOrderExecutionSession> _sessions;
        private readonly IOrderInterruptionPort _downstream;

        public RuntimeOrderInterruptionPort(
            List<WorldOrderExecutionSession> sessions,
            IOrderInterruptionPort downstream)
        {
            _sessions = sessions;
            _downstream = downstream ?? throw new ArgumentNullException(nameof(downstream));
        }

        public (bool Success, string? Error) InterruptForWorkshopReplacement()
        {
            var external = _downstream.InterruptForWorkshopReplacement();
            if (!external.Success) return external;

            foreach (var session in _sessions.ToArray())
            {
                if (session.IsTerminal) continue;
                var result = session.InterruptForWorkshopReplacement();
                if (!result.Success)
                    throw new InvalidOperationException(result.Error ?? "订单执行会话中断失败");
            }

            _sessions.RemoveAll(session => session.IsTerminal);
            return (true, null);
        }
    }
}

/// <summary>一次世界推进的时间与资源结果。</summary>
public sealed class ResourceWorldStep
{
    public long LogicalTimeDeltaMilli { get; }
    public ResourceAreaAdvanceResult Resources { get; }

    internal ResourceWorldStep(long logicalTimeDeltaMilli, ResourceAreaAdvanceResult resources)
    {
        LogicalTimeDeltaMilli = logicalTimeDeltaMilli;
        Resources = resources;
    }
}

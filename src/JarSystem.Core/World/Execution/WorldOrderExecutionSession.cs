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
    private readonly IReadOnlyList<WorldPlanStep> _masterSteps;
    private readonly IReadOnlyList<WorldPlanStep> _slaveSteps;
    private readonly IReadOnlyList<int> _deliveryJarIds;
    private readonly WorldPlanStepApplier _applier;
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
    public string? Reason { get; private set; }
    public long UnitDurationLogicalMilli => _unitDurationLogicalMilli;
    public long UnitElapsedLogicalMilli => _unitElapsedLogicalMilli;
    public int CompletedMasterSteps => _masterIndex;
    public int CompletedSlaveSteps => _slaveIndex;
    /// <summary>本会话从台结算产生的丢弃解释日志（只读视图）；回流/丢弃数量以 ResourceLedger 为准。</summary>
    public IReadOnlyList<ExecutionLogEntry> ResourceSettlementLog => _resourceSettlementLog.AsReadOnly();

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
        Func<int>? resourceLedgerTick)
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

        // 先处理主台路径开头连续的零耗时 MATCH；后续 MATCH 会在前一个耗时步骤完成后处理。
        if (Stage == WorldOrderExecutionStage.MasterSteps)
            PumpInstantMatches(_master, _masterSteps, ref _masterIndex, "master", _masterLiquidScope);

        if (!IsTerminal && Stage == WorldOrderExecutionStage.MasterSteps &&
            _masterIndex == _masterSteps.Count)
        {
            if (manifest.IsPartitioned)
                Stage = WorldOrderExecutionStage.Transfer;
            else
                CompleteSuccess();
        }
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
            CleanupTemporaryResources();
            _board.StartCooldown(DemandId);
            if (_board.Find(DemandId) != null &&
                _board.Find(DemandId)!.ReadyForSolve)
                _solveBook.MarkDirty(DemandId, "订单被工作台替换中断；重新检查当前世界");
            ReleaseReservation();
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
                        Stage = WorldOrderExecutionStage.Transfer;
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
                // 从台在准入时为空；只清除本次转入罐子的主台阶段标记。
                foreach (var jarId in _manifest.TransferJarIds)
                {
                    var matches = _slave!.Jars.Where(jar => jar.Id == jarId).ToArray();
                    if (matches.Length != 1)
                        throw new InvalidOperationException($"转入从台的罐子 #{jarId} 丢失或身份重复");
                    var clear = _slave.ClearMark(matches[0]);
                    if (!clear.Success)
                        throw new InvalidOperationException(clear.Reason ?? $"无法清除罐子 #{jarId} 的阶段标记");
                }

                Stage = WorldOrderExecutionStage.SlaveSteps;
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
        PumpInstantMatches(workshop, steps, ref index, phase);
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
    /// B 类主台→从台转移：先全量预检，再执行所有移动。
    /// 任何预检失败都不移动一只罐子、不扣一份液体。
    /// </summary>
    private string? TransferToSlave()
    {
        var slave = _slave ?? throw new InvalidOperationException("B 类执行缺少从台");
        var ledgerTick = _resourceLedger == null ? 0 : _resourceLedgerTick!();
        if (slave.HasResources)
            return "B 类资源转移前，从工作台的旧版资源池必须为空";

        var jars = new Dictionary<int, Jar>();

        foreach (var id in _manifest.TransferJarIds)
        {
            var matches = _master.Jars.Where(jar => jar.Id == id).ToArray();
            if (matches.Length != 1)
                return matches.Length == 0
                    ? $"转移前主台找不到罐子 #{id}"
                    : $"转移前主台存在重复罐子 ID #{id}";
            if (slave.Jars.Any(jar => jar.Id == id))
                return $"从台已存在待转移罐子 #{id}";
            jars.Add(id, matches[0]);
        }

        if (_manifest.TransferJarIds.Count > slave.MaxJars - slave.Jars.Count)
            return "从台罐位不足，无法完整接收转移罐子清单";

        if (_masterLiquidScope == null)
        {
            foreach (var item in _manifest.TransferLiquids)
            {
                if (item.Value < 0)
                    return $"转移液体「{item.Key}」数量无效";
                if (item.Value == 0) continue;
                var available = _master.LiquidStorage.GetValueOrDefault(item.Key);
                if (available < item.Value)
                    return $"主台液体「{item.Key}」不足：需要 {item.Value}，当前 {available}";
                if (slave.IsBanned(item.Key))
                    return $"从台禁用液体「{item.Key}」，不能转移执行所需液体";
                var storage = slave.CanStoreLiquid(item.Key, item.Value);
                if (!storage.Granted)
                    return storage.Reason ?? $"从台液体「{item.Key}」容量不足";
            }
        }
        else
        {
            if (_slaveLiquidScope == null)
                throw new InvalidOperationException("新液体资源路径的 B 类会话缺少从台订单范围");
            var outgoing = _masterLiquidScope.Balance;
            foreach (var item in outgoing)
            {
                if (_masterLiquidScope.GetAmount(item.Key) != item.Value)
                    throw new InvalidOperationException("主台订单液体快照读取不一致");
                if (!_slaveLiquidScope.Staging.CanReceiveForTransfer(
                    DemandId, item.Key, item.Value))
                    return $"从台订单暂存区不能接收液体「{item.Key}」×{item.Value}";
            }
        }

        // 从这里开始从台资源由本会话独占。若后续提交阶段出现不变量异常，
        // 收尾才有权把本次转移到从台的临时资源回流；预检失败时绝不触碰外部从台库存。
        _slaveTransferCommitted = true;

        // 预检完成后，在单线程逻辑调度中连续提交。若端口与刚才的判定不一致，
        // 这是世界不变量破坏；不能将其降级为普通拒绝或尝试部分恢复。
        foreach (var id in _manifest.TransferJarIds)
        {
            var jar = jars[id];
            if (!ReferenceEquals(_master.TransferOut(jar), jar))
                throw new InvalidOperationException($"罐子 #{id} 在转移提交阶段无法从主台移出");

            if (!slave.TransferIn(jar))
            {
                // 接收端口意外拒绝时，源工作台刚释放了一个罐位；立即恢复该实体归属。
                if (!_master.TransferIn(jar))
                    throw new InvalidOperationException($"罐子 #{id} 转移失败且无法恢复主台归属");
                throw new InvalidOperationException($"罐子 #{id} 在转移提交阶段违反预检结果");
            }
            _resourceLedger?.Record(ledgerTick, ResourceCauses.Transfer,
                ResourceKinds.Jar, jar.Shape, 1, from: _master.Name, to: slave.Name);
        }

        if (_masterLiquidScope == null)
        {
            foreach (var item in _manifest.TransferLiquids)
            {
                if (item.Value == 0) continue;
                if (!slave.AddLiquid(item.Key, item.Value))
                    throw new InvalidOperationException($"从台液体「{item.Key}」在转移提交阶段违反预检结果");

                if (!_master.RemoveLiquid(item.Key, item.Value))
                {
                    if (!slave.RemoveLiquid(item.Key, item.Value))
                        throw new InvalidOperationException($"液体「{item.Key}」转移失败且无法撤销从台入账");
                    throw new InvalidOperationException($"主台液体「{item.Key}」在转移提交阶段违反预检结果");
                }
                _resourceLedger?.Record(ledgerTick, ResourceCauses.Transfer,
                    ResourceKinds.Liquid, item.Key, item.Value, from: _master.Name, to: slave.Name);
            }
        }
        else
        {
            var outgoing = _masterLiquidScope.Balance;
            if (!OrderLiquidTransfer.TryExecute(
                DemandId, _masterLiquidScope.Staging, _slaveLiquidScope!.Staging,
                outgoing, out var transferReason))
                throw new InvalidOperationException(
                    transferReason ?? "主从订单液体交接在提交阶段违反预检结果");
            foreach (var item in outgoing)
                _resourceLedger?.Record(ledgerTick, ResourceCauses.Transfer,
                    ResourceKinds.Liquid, item.Key, item.Value, from: _master.Name, to: slave.Name);
            _masterLiquidScope.Staging.ReleaseOrderCapacity(DemandId);
        }

        return null;
    }

    private void CompleteSuccess()
    {
        var deliveryWorkshop = _manifest.IsPartitioned ? _slave! : _master;

        // 账本 tick 来源是外部回调。必须在任何交付罐标记/归属发生变化前取得，
        // 避免交付已移除后，结算才调用外部 tick provider 并抛出异常。
        int? settlementTick = null;
        if (_resourceLedger != null && _manifest.IsPartitioned &&
            _slaveTransferCommitted && _slave!.HasResources)
            settlementTick = _resourceLedgerTick!();

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

        CleanupTemporaryResources(settlementTick);
        if (_board.RecordSuccessfulFulfillment(DemandId) == DemandFulfillmentResult.NotFound)
            throw new InvalidOperationException("执行成功时订单已不在需求板中");

        ReleaseReservation();
        Reason = null;
        Stage = WorldOrderExecutionStage.Completed;
    }

    private void TerminateFailure(string reason)
    {
        if (IsTerminal) return;
        try
        {
            CleanupTemporaryResources();
            if (_board.Find(DemandId) != null)
                _solveBook.MarkDirty(DemandId, "订单执行失败，需按当前世界重新求解：" + reason);
        }
        finally
        {
            ReleaseReservation();
            Reason = reason;
            Stage = WorldOrderExecutionStage.Failed;
        }
    }

    private void CleanupTemporaryResources(int? ledgerTickOverride = null)
    {
        // 只有本会话已经越过全量转移预检并取得从台资源所有权，才允许回流从台。
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

    private void ReleaseReservation()
    {
        if (_reservationReleased) return;
        var scopes = _admission.GetLiquidScopes(DemandId);
        if (scopes.HasValue)
        {
            var hasBalance = scopes.Value.Master.Balance.Values.Any(value => value > 0) ||
                (scopes.Value.Slave?.Balance.Values.Any(value => value > 0) ?? false);
            var settlementTick = _resourceLedger != null && hasBalance
                ? _resourceLedgerTick!()
                : 0;
            var settlement = _admission.SettleLiquidResources(DemandId);
            if (settlement != null)
            {
                foreach (var item in settlement.Accepted)
                    _resourceLedger?.Record(settlementTick, ResourceCauses.Return,
                        ResourceKinds.Liquid, item.Key, item.Value,
                        from: "订单暂存", to: "自机液库");
                foreach (var item in settlement.Discarded)
                    _resourceLedger?.Record(settlementTick, ResourceCauses.Discard,
                        ResourceKinds.Liquid, item.Key, item.Value,
                        from: "订单暂存", to: null, reason: "自机液库总容量不足");
            }
        }
        if (!_admission.Release(DemandId))
            throw new InvalidOperationException("执行会话结束时找不到对应的资源预留");
        _reservationReleased = true;
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

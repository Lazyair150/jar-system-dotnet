using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
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
    private readonly IReadOnlyList<int> _matchedJarIds;
    private readonly WorldPlanStepApplier _applier;
    private readonly long _unitDurationLogicalMilli;
    private readonly SlaveRollback _slaveRollback = new();

    private int _masterIndex;
    private int _slaveIndex;
    private long _unitElapsedLogicalMilli;
    private bool _reservationReleased;

    public int DemandId { get; }
    internal bool IsBoundTo(OrderDemandBoard board, WorkshopRoster roster) =>
        ReferenceEquals(_board, board) && ReferenceEquals(_roster, roster);
    public WorldOrderExecutionStage Stage { get; private set; }
    public bool IsTerminal =>
        Stage is WorldOrderExecutionStage.Completed or
            WorldOrderExecutionStage.Failed or WorldOrderExecutionStage.Interrupted;
    public bool Succeeded => Stage == WorldOrderExecutionStage.Completed;
    public string? Reason { get; private set; }
    public long UnitDurationLogicalMilli => _unitDurationLogicalMilli;
    public long UnitElapsedLogicalMilli => _unitElapsedLogicalMilli;
    public int CompletedMasterSteps => _masterIndex;
    public int CompletedSlaveSteps => _slaveIndex;

    private WorldOrderExecutionSession(
        int demandId,
        OrderDemandBoard board,
        WorldOrderSolveBook solveBook,
        WorldOrderExecutionAdmission admission,
        WorkshopRoster roster,
        WorldOrderResourceManifest manifest,
        WorldOrderSolvePlan plan,
        long unitDurationLogicalMilli)
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
        _applier = new WorldPlanStepApplier();

        if (manifest.DemandId != demandId || manifest.IsPartitioned != plan.IsPartitioned)
            throw new InvalidOperationException("资源清单与求解计划不属于同一订单或执行类型");

        // MATCH 是需求匹配/交付选择，不是加工动作，不占用执行时间。
        // 与旧 OrderExecutionSession 一致：保留其罐子 ID，成功时从交付工作台移除。
        _matchedJarIds = Array.AsReadOnly(ReadMatchedJarIds(plan).ToArray());
        _masterSteps = Array.AsReadOnly(plan.MasterSteps
            .Where(step => !IsMatch(step)).ToArray());
        _slaveSteps = Array.AsReadOnly(plan.SlaveSteps
            .Where(step => !IsMatch(step)).ToArray());

        if (_matchedJarIds.Distinct().Count() != _matchedJarIds.Count)
            throw new InvalidOperationException("求解计划重复将同一只罐子作为交付物");
        if (manifest.IsPartitioned &&
            _matchedJarIds.Any(id => !manifest.TransferJarIds.Contains(id)))
            throw new InvalidOperationException("B 类交付罐没有包含在从主台转移的资源清单中");
        if (!manifest.IsPartitioned &&
            _matchedJarIds.Any(id => !manifest.MasterJarIds.Contains(id)))
            throw new InvalidOperationException("A 类交付罐没有包含在主台资源清单中");
        if (!manifest.IsPartitioned && _slaveSteps.Count != 0)
            throw new InvalidOperationException("单阶段执行计划不能包含从台步骤");

        Stage = _masterSteps.Count > 0
            ? WorldOrderExecutionStage.MasterSteps
            : manifest.IsPartitioned
                ? WorldOrderExecutionStage.Transfer
                : WorldOrderExecutionStage.MasterSteps;

        // A 类纯 MATCH 计划没有耗时操作，准入后即可交付；先保持非终态，
        // 只有完成清理、需求板登记与预留释放后才进入 Completed。
        if (!manifest.IsPartitioned && _masterSteps.Count == 0)
            CompleteSuccess();
    }

    /// <summary>
    /// 先执行既有准入（重新验证快照并预留资源），再建立会话。
    /// logicalTimePerUnit 必须由世界装配/执行策略明确提供；本类不猜测旧 tick 与逻辑时间的换算。
    /// </summary>
    public static WorldOrderExecutionStartResult TryStart(
        int demandId,
        OrderDemandBoard board,
        WorldOrderSolveBook solveBook,
        WorldOrderExecutionAdmission admission,
        WorkshopRoster roster,
        long logicalTimePerUnit)
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
                admitted.Manifest, result.Plan, logicalTimePerUnit);
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
    public void AdvanceLogicalTime(long deltaLogicalTimeMilli)
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
                _slave!.ClearMarks();
                if (_slaveSteps.Count > 0)
                    Stage = WorldOrderExecutionStage.SlaveSteps;
                else
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

        var result = _applier.Apply(workshop, steps[index]);
        if (!result.Success)
        {
            TerminateFailure(phase + " 步骤 " + steps[index].Step + " 执行失败: " +
                (result.Reason ?? "领域操作拒绝但未提供原因"));
            return;
        }

        index++;
    }

    /// <summary>
    /// B 类主台→从台转移：先全量预检，再执行所有移动。
    /// 任何预检失败都不移动一只罐子、不扣一份液体。
    /// </summary>
    private string? TransferToSlave()
    {
        var slave = _slave ?? throw new InvalidOperationException("B 类执行缺少从台");
        if (slave.HasResources)
            return "B 类资源转移前，从工作台必须仍为空";

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
        }

        foreach (var item in _manifest.TransferLiquids)
        {
            if (item.Value == 0) continue;
            if (!slave.AddLiquid(item.Key, item.Value))
                throw new InvalidOperationException($"从台液体「{item.Key}」在转移提交阶段违反预检结果");

            if (!_master.RemoveLiquid(item.Key, item.Value))
            {
                // 主台扣账意外失败时撤销刚刚加入从台的这一笔；此前已提交的资源由会话收尾处理。
                if (!slave.RemoveLiquid(item.Key, item.Value))
                    throw new InvalidOperationException($"液体「{item.Key}」转移失败且无法撤销从台入账");
                throw new InvalidOperationException($"主台液体「{item.Key}」在转移提交阶段违反预检结果");
            }
        }

        return null;
    }

    private void CompleteSuccess()
    {
        var deliveryWorkshop = _manifest.IsPartitioned ? _slave! : _master;

        // 先完整验证交付集合，再统一移除，避免前几个交付罐已离台后才发现后续 ID 缺失。
        var deliveryJars = new List<Jar>();
        foreach (var id in _matchedJarIds)
        {
            var matches = deliveryWorkshop.Jars.Where(jar => jar.Id == id).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"订单交付罐 #{id} 在交付工作台中不存在或身份重复");
            deliveryJars.Add(matches[0]);
        }

        foreach (var jar in deliveryJars)
        {
            if (deliveryWorkshop.RemoveJar(jar) == null)
                throw new InvalidOperationException($"订单交付罐 #{jar.Id} 无法从工作台移除");
        }

        CleanupTemporaryResources();
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

    private void CleanupTemporaryResources()
    {
        if (_manifest.IsPartitioned && _slave != null && _slave.HasResources)
        {
            _slaveRollback.Execute(
                _master, _slave, _ => { });
        }

        _master.ClearMarks();
        _slave?.ClearMarks();
    }

    private void ReleaseReservation()
    {
        if (_reservationReleased) return;
        if (!_admission.Release(DemandId))
            throw new InvalidOperationException("执行会话结束时找不到对应的资源预留");
        _reservationReleased = true;
    }

    private static bool IsMatch(WorldPlanStep step) =>
        string.Equals(step.Operation?.Type, "MATCH", StringComparison.Ordinal);

    private static IEnumerable<int> ReadMatchedJarIds(WorldOrderSolvePlan plan)
    {
        foreach (var step in plan.MasterSteps.Concat(plan.SlaveSteps))
        {
            if (!IsMatch(step)) continue;
            var index = step.Operation!.JarIndex;
            if (!index.HasValue || index.Value < 0 || index.Value >= step.Before.Count)
                throw new InvalidOperationException("MATCH 步骤的 JarIndex 无效");
            var id = step.Before[index.Value].Id;
            if (id is not > 0)
                throw new InvalidOperationException("MATCH 步骤没有有效罐子 ID");
            yield return id.Value;
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
    Interrupted
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

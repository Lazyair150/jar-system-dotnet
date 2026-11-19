using System;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.World;

public enum WorkshopRole
{
    Master,
    Slave
}

/// <summary>
/// 主/从岗位替换过程。时间单位为逻辑时间的千分之一。
/// 主台替换复用旧 WorkshopSwitchEvent 的重组与资源转移规则；从台替换只在完成时提交岗位指向。
/// </summary>
public sealed class WorkshopReplacementProcess
{
    private readonly WorkshopRoster _roster;
    private readonly WorkshopRole _role;
    private readonly Workshop _oldWorkshop;
    private readonly Workshop _targetWorkshop;
    private readonly IOrderInterruptionPort _interruptionPort;
    private readonly long _phaseDurationMilli;
    private readonly WorkshopSwitchEvent? _masterSwitch;

    private long _phaseElapsedMilli;
    private int _phase;
    private bool _begun;
    private bool _finished;

    public bool IsFinished => _finished;
    public bool Succeeded { get; private set; }
    public string? FailureReason { get; private set; }
    public WorkshopRole Role => _role;
    public int CompletedPhases => _phase;

    public WorkshopReplacementProcess(
        WorkshopRoster roster,
        WorkshopRole role,
        Workshop targetWorkshop,
        IOrderInterruptionPort interruptionPort,
        long phaseDurationMilli,
        Func<Workshop, Workshop, WorkshopSwitchEvent>? masterSwitchFactory = null)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _role = role;
        _targetWorkshop = targetWorkshop ?? throw new ArgumentNullException(nameof(targetWorkshop));
        _interruptionPort = interruptionPort ?? throw new ArgumentNullException(nameof(interruptionPort));
        if (phaseDurationMilli <= 0)
            throw new ArgumentOutOfRangeException(nameof(phaseDurationMilli), "替换阶段时长必须大于零");
        _phaseDurationMilli = phaseDurationMilli;

        _oldWorkshop = role switch
        {
            WorkshopRole.Master => roster.Master ?? throw new InvalidOperationException("当前没有主工作台岗位"),
            WorkshopRole.Slave => roster.Slave ?? throw new InvalidOperationException("当前没有从工作台岗位"),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        ValidateTarget();

        if (role == WorkshopRole.Master)
            _masterSwitch = (masterSwitchFactory ?? ((oldMaster, newMaster) =>
                new WorkshopSwitchEvent(oldMaster, newMaster)))(_oldWorkshop, _targetWorkshop);
    }

    /// <summary>
    /// 开始替换。先验证目标，再中断订单；主台迁徙快照在订单清理之后建立。
    /// </summary>
    public (bool Success, string? Error) Begin()
    {
        if (_begun) return (false, "替换过程已经开始");
        if (_finished) return (false, "替换过程已经结束");

        try
        {
            ValidateTarget();
            var interruption = _interruptionPort.InterruptForWorkshopReplacement();
            if (!interruption.Success)
                return (false, interruption.Error ?? "订单中断失败");

            // 必须在订单中断与从台结算之后拍快照，确保回滚不会复活已中断订单的临时资源。
            _masterSwitch?.Begin();
            _begun = true;
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 用逻辑时间推进。主台替换包含重组、转移两个阶段；从台替换包含一个岗位提交阶段。
    /// 一次推进可以跨过多个阶段，余下时间继续计入后续阶段。
    /// </summary>
    public void AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));
        if (!_begun) throw new InvalidOperationException("必须先 Begin 才能推进替换过程");
        if (_finished || deltaLogicalTimeMilli == 0) return;

        var remaining = deltaLogicalTimeMilli;
        while (remaining > 0 && !_finished)
        {
            var needed = _phaseDurationMilli - _phaseElapsedMilli;
            var consumed = Math.Min(remaining, needed);
            _phaseElapsedMilli = checked(_phaseElapsedMilli + consumed);
            remaining -= consumed;

            if (_phaseElapsedMilli < _phaseDurationMilli)
                continue;

            _phaseElapsedMilli = 0;
            if (_role == WorkshopRole.Master)
            {
                var result = _phase == 0
                    ? _masterSwitch!.RunReshape()
                    : _masterSwitch!.RunTransfer();

                if (!result.Ok)
                {
                    Fail(result.Error ?? "主工作台迁徙失败");
                    return;
                }

                _phase++;
                if (_phase == 2)
                {
                    try
                    {
                        _roster.CommitMasterReplacement(_oldWorkshop, _targetWorkshop);
                        Succeed();
                    }
                    catch (Exception ex)
                    {
                        Fail(ex.Message);
                    }
                }
            }
            else
            {
                try
                {
                    _roster.CommitSlaveReplacement(_oldWorkshop, _targetWorkshop);
                    _phase = 1;
                    Succeed();
                }
                catch (Exception ex)
                {
                    Fail(ex.Message);
                }
            }
        }
    }

    /// <summary>取消尚未完成的替换。主台迁徙恢复快照；岗位指向尚未提交。</summary>
    public void Cancel(string reason = "工作台替换已取消")
    {
        if (!_begun || _finished) return;
        _masterSwitch?.Rollback();
        Fail(reason);
    }

    private void ValidateTarget()
    {
        var currentRoleWorkshop = _role == WorkshopRole.Master ? _roster.Master : _roster.Slave;
        if (!ReferenceEquals(currentRoleWorkshop, _oldWorkshop))
            throw new InvalidOperationException("岗位在替换开始前已经发生变化");
        if (!_roster.IsSpare(_targetWorkshop))
            throw new InvalidOperationException("目标必须是当前主从岗位之外的备用工作台");
        if (_role == WorkshopRole.Slave && _targetWorkshop.HasResources)
            throw new InvalidOperationException("备用工作台必须为空才能接任从工作台");
    }

    private void Fail(string reason)
    {
        _masterSwitch?.Rollback();
        FailureReason = reason;
        Succeeded = false;
        _finished = true;
    }

    private void Succeed()
    {
        Succeeded = true;
        FailureReason = null;
        _finished = true;
    }
}

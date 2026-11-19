using System;
using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Process;

/// <summary>
/// 把「工作台迁徙」接到过程系统上的**工作体**：一次换台 = 一个由 tick 驱动的过程。
///
/// <para><b>为什么换台要过程化。</b>换台是**使用者主动发起**的资源重组 —— 罐子要改形、要倒液、
/// 要搬到另一台。它跟"订单执行"是同一类劳动（都要按世界规则一格一格地做），而不是一次瞬间的世界跳变。
/// 旧实现把它做成同步原子事务（快照 + 全做 + 出错全回滚），这带来两个问题：
/// 一是**没有中间态**，任何一步没资源就整体不动，看不出"做到哪了"；
/// 二是**与过程系统脱节**，世界事件里记了一笔，过程日志里什么都没有。</para>
///
/// <para><b>为什么它不是订单。</b>它没有委托方、没有交付物、不产生 <c>orderEvent</c>。
/// 它只是"玩家决定换个台干活"。所以它走 <see cref="ProcessKinds.WorkshopMigration"/>，
/// 不走订单执行那条线。</para>
///
/// <para><b>成功/失败语义（与订单执行对齐之处与不之处）。</b>
/// 对齐：过程有 started / progressed / blocked / resumed / finished 五态，
/// 受 <c>graceTicks</c> 保护，可被 <c>cancelProcess</c> 取消，结果写进过程日志。
/// 不对齐（刻意）：**订单失败不回滚已付成本**（`规则.txt:33-37`），而换台**每一阶段仍然整体回滚** ——
/// 因为换台没有"已付出去的代价"这回事，它改的只是资源摆在哪，退回去不构成惩罚。
/// 把失败惩罚硬加到换台上，等于让玩家为"换个台"付费，那不是这个世界的意思。</para>
/// </summary>
internal sealed class SwitchProcessAdvancer : IProcessAdvancer
{
    private readonly WorkshopSwitchEvent _switchEvent;
    private readonly int _targetIndex;
    // 提交岗位指针/历史的回调必须遵守非抛异常契约；它不是可回滚的跨服务事务。
    private readonly Action<WorkshopSwitchEvent, int> _onCommitted;

    private int _phase;          // 0 = 重组分支，1 = 资源转移，2 = 完成
    private bool _done;
    private bool _completed;
    private string? _failure;

    public SwitchProcessAdvancer(WorkshopSwitchEvent switchEvent, int targetIndex,
        Action<WorkshopSwitchEvent, int> onCommitted)
    {
        _switchEvent = switchEvent;
        _targetIndex = targetIndex;
        _onCommitted = onCommitted;
    }

    /// <summary>本过程已经做完了几个阶段（诊断用）。</summary>
    public int Phase => _phase;

    public bool AdvanceOneUnit(IProcessRecorder recorder)
    {
        _ = recorder;
        if (_done) return false;

        // **一 tick 一个阶段**：换台只有两大块劳动（按新台禁令重组；把剩下的资源搬过去），
        // 拆太细没有意义 —— 每一块本身就是一串同质的操作，玩家看到的也是"重组完成 / 搬运完成"。
        switch (_phase)
        {
            case 0:
                if (!RunStage(_switchEvent.RunReshape)) return false;
                _phase = 1;
                return true;

            case 1:
                if (!RunStage(_switchEvent.RunTransfer)) return false;
                _phase = 2;
                _done = true;
                return false;

            default:
                _done = true;
                return false;
        }
    }

    private bool RunStage(Func<(bool Ok, string? Error)> stage)
    {
        var (ok, error) = stage();
        if (ok) return true;
        _failure = error;
        _done = true;
        return false;
    }

    /// <summary>
    /// 守卫：只读检查"现在这一步还能不能做"。
    /// 换台本身很轻，唯一的硬前置是**两台都还在、且确实是不同的台**。
    /// </summary>
    public GuardResult CheckGuard()
    {
        if (_done) return GuardResult.Ok();
        if (_targetIndex < 0) return GuardResult.Blocked("目标工作台已不存在");
        return GuardResult.Ok();
    }

    public bool Succeeded => _failure == null && _phase >= 2;

    public string? FailureReason => _failure;

    public void MarkFailed(string reason)
    {
        if (_failure != null) return;
        _failure = reason;
        _done = true;
    }

    /// <summary>
    /// 一次性收尾：成功则提交主台指针并写事件历史，**失败或被取消则把两台恢复到迁徙前**。必须幂等。
    ///
    /// 取消路径（`Cancel → MarkFailed → OnFinished → Abort`）与本类原先只处理异常的分支不同：
    /// `RunReshape` 可能已经改过旧台的罐子，若不回滚，世界会停在一个"改了一半"的工作台上。
    /// 这也是为什么**不**把订单的"失败不回滚"套到迁徙上 —— 迁徙没有已付代价，退回去不构成惩罚。
    /// </summary>
    public void OnFinished()
    {
        if (_completed) return;
        _completed = true;
        if (!Succeeded)
        {
            _switchEvent.Rollback();
            return;
        }
        // 回调应只执行已验证的本地岗位/历史提交，并遵守非抛异常契约。
        // 若回调抛出，过程已经进入一次性收尾，无法安全判断外部是否部分提交。
        _onCommitted(_switchEvent, _targetIndex);
        // _onCommitted 已提交主台指针；此时才允许写入账本/统计并归还被丢弃罐子。
        _switchEvent.CommitTransferEffects();
    }
}

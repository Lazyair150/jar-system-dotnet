using JarSystem.Core.Process;

/// <summary>
/// **过程观察者（Web 壳）**：把核心的过程变化翻成 5 条对外消息（P4）。
///
/// 定位：这是**表现适配器**，不是业务 —— 只做"读实例字段 → 组消息 → 广播"，
/// 既不改变世界、也不做判定。消息名与载荷键集合是对外契约
/// （冻结在 `过程系统/00_Phase0_接口规格.md` §3.3，并由 `--check-protocol` 守护）。
///
/// 广播刻意"发射即忘"（`_ = BroadcastAsync(...)`）：表现层发送失败不得影响世界推进
/// （<see cref="IProcessObserver"/> 的约定本来就要求实现方不得抛异常）。
/// 注：两条执行轨都会走这里 —— 同步轨的过程在一个调用内跑完，因此会连着发"开始→完成"；
/// 那是有意的（过程日志与消息口径一致），前端可以只对异步轨做进度展示。
/// </summary>
public sealed class ProcessObserver : IProcessObserver
{
    private readonly IWebSocketSink _sink;

    public ProcessObserver(IWebSocketSink sink) => _sink = sink;

    /// <inheritdoc />
    public void OnChanged(ProcessInstance p, string reason, int tick)
    {
        switch (reason)
        {
            case ProcessObserverReasons.Started:
                _ = _sink.BroadcastAsync(JsonArgs.J(new
                {
                    type = "processStarted",
                    data = new { id = p.Id, kind = p.Kind, subject = p.Subject, totalSteps = p.DeclaredSteps, deadlineTick = p.DeadlineTick }
                }));
                break;

            case ProcessObserverReasons.Progressed:
                _ = _sink.BroadcastAsync(JsonArgs.J(new
                {
                    type = "processProgressed",
                    data = new { id = p.Id, currentStep = p.AppliedSteps, totalSteps = p.DeclaredSteps, tick }
                }));
                break;

            case ProcessObserverReasons.Blocked:
                _ = _sink.BroadcastAsync(JsonArgs.J(new
                {
                    type = "processBlocked",
                    data = new { id = p.Id, currentStep = p.AppliedSteps, reason = p.BlockedReason, graceTicks = p.GraceTicks }
                }));
                break;

            case ProcessObserverReasons.Resumed:
                _ = _sink.BroadcastAsync(JsonArgs.J(new
                {
                    type = "processResumed",
                    data = new { id = p.Id, currentStep = p.AppliedSteps, tick }
                }));
                break;

            case ProcessObserverReasons.Finished:
                _ = _sink.BroadcastAsync(JsonArgs.J(new
                {
                    type = "processFinished",
                    data = new
                    {
                        id = p.Id,
                        outcome = p.Outcome.HasValue ? ProcessContract.Of(p.Outcome.Value) : null,
                        currentStep = p.AppliedSteps,
                        reason = p.FailureReason
                    }
                }));
                break;
        }
    }
}

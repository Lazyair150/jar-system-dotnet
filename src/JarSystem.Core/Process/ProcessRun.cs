using JarSystem.Core.Abstractions;

namespace JarSystem.Core.Process;

/// <summary>
/// 一次过程执行的句柄（**可插拔记账器**）：实现 <see cref="IProcessRecorder"/>，
/// 把内核报告的"又执行了一步"写进过程实例与过程日志。
///
/// 生命周期（P1 瞬时模式）：
/// <code>
/// Begin  ->  MarkReady
/// SetPlan ->  plan_accepted（日志）
/// 第一步/里程碑 ->  started（日志，惰性）
/// 每步         ->  step_applied（日志）+ 进度 +1
/// Finish       ->  succeeded / failed（日志）+ 进入终态
/// </code>
/// 全部发生在**同一个 tick** 内 —— 这正是"行为与重构前逐字相同"的来源；
/// P2 会把"一次推进"拆成"每 tick 至多一步"（<c>TickPipeline</c>）。
/// </summary>
public sealed class ProcessRun : IProcessRecorder
{
    private readonly ProcessJournal _journal;
    private readonly Func<int> _worldRevision;
    private readonly Action<ProcessRun>? _onClosed;
    private readonly Func<int> _tickSource;
    private readonly Action<ProcessInstance, string, int>? _notify;
    private bool _started;

    internal ProcessRun(ProcessInstance instance, ProcessJournal journal, Func<int> worldRevision,
        Func<int> tickSource, Action<ProcessRun>? onClosed, Action<ProcessInstance, string, int>? notify = null)
    {
        Instance = instance;
        _journal = journal;
        _worldRevision = worldRevision;
        _tickSource = tickSource;
        _onClosed = onClosed;
        _notify = notify;
    }

    /// <summary>
    /// **提交时**读取的逻辑 tick。
    /// 刻意不做成"建立时捕获的常量"：异步轨里一个过程的多个单位会落在**不同的 tick**，
    /// 只有读当前时刻才能让过程日志回答"这一步发生在世界的哪一刻"（同步 drain 下它等价于常量）。
    /// </summary>
    private int Tick => _tickSource();

    /// <summary>过程实例（状态迁移只经本类方法；外部只读）。</summary>
    public ProcessInstance Instance { get; }

    /// <summary>是否已终结。</summary>
    public bool IsClosed => Instance.IsTerminal;

    // ============================================================
    // IProcessRecorder（内核侧调用）
    // ============================================================

    /// <inheritdoc />
    public void SetPlan(string source, int declaredSteps)
    {
        if (IsClosed) return;
        Instance.NotePlan(source, declaredSteps);
        Commit(JournalKinds.PlanAccepted,
            summary: $"计划：来源 {Instance.PlanSource}，声明步数 {Instance.DeclaredSteps}，步间隔 {Instance.TicksPerStep} 刻");
    }

    /// <inheritdoc />
    public void StepApplied(string operation, int? jarId = null, string? summary = null)
    {
        if (IsClosed) return;
        EnsureStarted();
        Instance.NoteStepApplied();
        var text = summary ?? operation;
        if (jarId.HasValue) text = "#" + jarId.Value + " " + text;
        Commit(JournalKinds.StepApplied, stepIndex: Instance.AppliedSteps, operation: operation,
            summary: text);
    }

    /// <inheritdoc />
    public void Milestone(string name, string? summary = null)
    {
        if (IsClosed) return;
        EnsureStarted();
        // 内核侧里程碑：条目 kind 恒为 phase，具体名字落在 operation 字段
        // （好处：过程日志的 kind 取值表保持封闭，而内核词汇可以独立演进）。
        Commit(JournalKinds.Phase, operation: name, summary: summary ?? name);
    }

    // ============================================================
    // 生命周期（只由宿主/门面调用）
    // ============================================================

    /// <summary>结束过程：成功或失败（**幂等**，已终结时直接返回）。</summary>
    public void Finish(bool success, string? reason)
    {
        if (IsClosed) return;
        if (!_started)
        {
            // 一步都没走就结束了（例：前置校验在开始推进前就判定失败）：
            // 仍然要给出一条 started，否则日志里会出现"没有开始就结束"的断层。
            EnsureStarted();
        }
        Commit(success ? JournalKinds.Succeeded : JournalKinds.Failed, reason: reason,
            summary: success
                ? $"成功：{Instance.Subject}（{Instance.AppliedSteps} 步）"
                : $"失败：{Instance.Subject}（{reason ?? "未给出原因"}）");
        Close(success ? ProcessOutcome.Succeeded : ProcessOutcome.Failed, reason);
    }

    /// <summary>取消过程（已完成的部分不回滚；P4 的 cancelProcess 用）。</summary>
    public void Abort(string? reason)
    {
        if (IsClosed) return;
        if (!_started) EnsureStarted();
        Commit(JournalKinds.Aborted, reason: reason, summary: $"取消：{Instance.Subject}");
        Close(ProcessOutcome.Aborted, reason);
    }

    /// <summary>记录"被阻断"（守卫判定前置条件暂时不成立）。</summary>
    public void NoteBlocked(string reason)
    {
        if (IsClosed || Instance.State == ProcessState.Blocked) return;
        // 可能发生在**第一个单位之前**（流程刚建立、世界就已经不满足前置条件）：
        // 那样也应当先"开始"再"阻断" —— 否则过程日志里会缺 started，状态机也不允许 Ready → Blocked。
        EnsureStarted();
        Instance.Block(Tick, reason);
        Commit(JournalKinds.Blocked, reason: reason, summary: $"被阻断：{Instance.Subject}（{reason}）");
        _notify?.Invoke(Instance, ProcessObserverReasons.Blocked, Tick);
    }

    /// <summary>记录"从阻断恢复"。</summary>
    public void NoteResumed()
    {
        if (IsClosed || Instance.State != ProcessState.Blocked) return;
        var reason = Instance.BlockedReason;
        Instance.Resume();
        Commit(JournalKinds.Resumed, reason: reason, summary: $"恢复：{Instance.Subject}（原阻断：{reason}）");
        _notify?.Invoke(Instance, ProcessObserverReasons.Resumed, Tick);
    }

    /// <summary>记录"宽限窗口耗尽"（随后会有一条 failed 结尾）。</summary>
    public void NoteGraceExpired(string reason)
    {
        if (IsClosed) return;
        Commit(JournalKinds.GraceExpired, reason: reason, summary: $"宽限窗口耗尽：{Instance.Subject}（{reason}）");
    }

    // ============================================================
    // 内部
    // ============================================================

    private void EnsureStarted()
    {
        if (_started) return;
        Instance.Start(Tick);
        _started = true;
        Commit(JournalKinds.Started, summary: $"开始：{Instance.Subject}");
        _notify?.Invoke(Instance, ProcessObserverReasons.Started, Tick);
    }

    private void Close(ProcessOutcome outcome, string? reason)
    {
        Instance.Conclude(outcome, Tick, reason);
        _notify?.Invoke(Instance, ProcessObserverReasons.Finished, Tick);
        _onClosed?.Invoke(this);
    }

    private void Commit(string kind, int? stepIndex = null, string? operation = null,
        string? modifier = null, string? reason = null, string? summary = null)
    {
        var entry = _journal.Append(Tick, Instance.Id, kind, stepIndex, operation, modifier,
            reason, _worldRevision(), summary);
        Instance.NoteJournalSeq(entry.Seq);
    }
}

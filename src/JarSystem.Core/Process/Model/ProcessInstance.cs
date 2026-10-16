namespace JarSystem.Core.Process;

/// <summary>
/// 过程实例（**运行时状态**）：状态迁移只经本类的意图方法，字段一律不对外可写
/// （沿用 <c>Order</c> / <c>OrderEvent</c> 的做法）。
///
/// 允许的迁移见 <see cref="ProcessState"/>；两条边界：
/// <list type="bullet">
/// <item>**推进事件**打在终态上 ⇒ 抛 <see cref="InvalidOperationException"/>（编程错误：过程已结束还在推）；</item>
/// <item>**重复终结** ⇒ 返回 false 且不改状态（宿主/事件源重试必须幂等，不得抛）。</item>
/// </list>
/// </summary>
public sealed class ProcessInstance
{
    private int _appliedSteps;

    internal ProcessInstance(int id, ProcessDefinition definition, int startTick, int ticksPerStep)
    {
        Id = id;
        Kind = definition.Kind;
        Subject = definition.Subject;
        SubjectId = definition.SubjectId;
        Priority = definition.Priority;
        StartTick = startTick;
        TicksPerStep = ticksPerStep;
        DeadlineTick = definition.DeadlineTicks.HasValue ? startTick + definition.DeadlineTicks.Value : null;
        GraceTicks = definition.GraceTicks;
        NextUnitTick = startTick + 1;   // 异步轨：启动后的下一个 tick 就能推进第一个单位
    }

    /// <summary>过程 id（进程内从 1 单调递增）。</summary>
    public int Id { get; }

    /// <summary>过程种类（取值见 <see cref="ProcessKinds"/>）。</summary>
    public string Kind { get; }

    /// <summary>主体描述（世界语言，可直接显示）。</summary>
    public string Subject { get; }

    /// <summary>结构化主体标识。</summary>
    public int? SubjectId { get; }

    /// <summary>同 tick 推进顺序（P2 起生效）。</summary>
    public int Priority { get; }

    /// <summary>创建时所在的逻辑 tick。</summary>
    public int StartTick { get; }

    /// <summary>绝对时限（= 创建 tick + 相对时限）；null = 无时限。</summary>
    public int? DeadlineTick { get; private set; }

    /// <summary>宽限窗口（P3 起生效）。</summary>
    public int GraceTicks { get; }

    /// <summary>步间隔（刻/步）：本过程的每一步要占用几个 tick（由 <see cref="StepAllocator"/> 解析后的值）。</summary>
    public int TicksPerStep { get; }

    /// <summary>当前状态。</summary>
    public ProcessState State { get; private set; } = ProcessState.Created;

    /// <summary>结尾（未终结为 null）。</summary>
    public ProcessOutcome? Outcome { get; private set; }

    /// <summary>计划来源（只进日志，不下发）。</summary>
    public string? PlanSource { get; private set; }

    /// <summary>声明的总步数（0 = 未声明；瞬时模式下只是提示）。</summary>
    public int DeclaredSteps { get; private set; }

    /// <summary>已应用的步数（= 过程进度）。</summary>
    public int AppliedSteps => _appliedSteps;

    /// <summary>已推进的**单位**数（一个单位 = 一个计划步骤或一个兜底批次；异步轨按它计步间隔）。</summary>
    public int AdvancedUnits { get; private set; }

    /// <summary>下一个可推进单位的 tick（异步轨用；同步轨不读它）。</summary>
    public int NextUnitTick { get; private set; }

    /// <summary>开始推进的 tick（未开始为 null）。</summary>
    public int? StartedAtTick { get; private set; }

    /// <summary>结尾所在的 tick（未终结为 null）。</summary>
    public int? FinishedAtTick { get; private set; }

    /// <summary>失败/取消原因（世界语言）。</summary>
    public string? FailureReason { get; private set; }

    /// <summary>被阻断的原因（P3 起使用）。</summary>
    public string? BlockedReason { get; private set; }

    /// <summary>被阻断时的 tick（用于宽限窗口倒计时；未阻断为 null）。</summary>
    public int? BlockedAtTick { get; private set; }

    /// <summary>本过程最后一条过程日志的序号（0 = 尚无）。</summary>
    public int LastJournalSeq { get; private set; }

    /// <summary>是否已到终态。</summary>
    public bool IsTerminal => State is ProcessState.Succeeded or ProcessState.Failed or ProcessState.Aborted;

    /// <summary>接受计划（Created 到 Ready）。</summary>
    internal void MarkReady()
    {
        if (State != ProcessState.Created)
            throw new InvalidOperationException($"过程 {Id} 状态为 {State}，不能接受计划（要求 Created）");
        State = ProcessState.Ready;
    }

    /// <summary>开始推进（Ready 到 Running）。</summary>
    internal void Start(int tick)
    {
        if (State != ProcessState.Ready)
            throw new InvalidOperationException($"过程 {Id} 状态为 {State}，不能开始（要求 Ready）");
        StartedAtTick = tick;
        State = ProcessState.Running;
    }

    /// <summary>记录计划声明（可多次，后者覆盖）。</summary>
    internal void NotePlan(string source, int declaredSteps)
    {
        RequireNotTerminal("声明计划");
        PlanSource = source;
        DeclaredSteps = declaredSteps < 0 ? 0 : declaredSteps;
    }

    /// <summary>记录"已应用一步"（进度 +1）。</summary>
    internal void NoteStepApplied()
    {
        if (IsTerminal)
            throw new InvalidOperationException($"过程 {Id} 已终结（{State}），不能再推进");
        if (State != ProcessState.Running)
            throw new InvalidOperationException($"过程 {Id} 状态为 {State}，不在推进中");
        _appliedSteps++;
    }

    /// <summary>记录过程日志序号（由进程侧在写日志后回填）。</summary>
    internal void NoteJournalSeq(int seq)
    {
        if (seq > LastJournalSeq) LastJournalSeq = seq;
    }

    /// <summary>记录"已推进一个单位"，并按步间隔排定下一次推进的 tick（异步轨用）。</summary>
    internal void NoteUnitAdvanced(int tick)
    {
        AdvancedUnits++;
        NextUnitTick = tick + TicksPerStep;
    }

    /// <summary>进入阻断（P3 起由守护触发）。</summary>
    internal void Block(int tick, string reason)
    {
        if (State != ProcessState.Running)
            throw new InvalidOperationException($"过程 {Id} 状态为 {State}，不能进入阻断（要求 Running）");
        BlockedReason = reason;
        BlockedAtTick = tick;
        State = ProcessState.Blocked;
    }

    /// <summary>从阻断恢复（P3 起用）。</summary>
    internal void Resume()
    {
        if (State != ProcessState.Blocked)
            throw new InvalidOperationException($"过程 {Id} 状态为 {State}，不能恢复（要求 Blocked）");
        BlockedReason = null;
        BlockedAtTick = null;
        State = ProcessState.Running;
    }

    /// <summary>
    /// 进入终态。**幂等**：已终结时返回 false 且不改状态（宿主重试不得导致异常）。
    /// </summary>
    internal bool Conclude(ProcessOutcome outcome, int tick, string? reason)
    {
        if (IsTerminal) return false;
        Outcome = outcome;
        FailureReason = outcome == ProcessOutcome.Succeeded ? null : reason;
        FinishedAtTick = tick;
        State = outcome switch
        {
            ProcessOutcome.Succeeded => ProcessState.Succeeded,
            ProcessOutcome.Failed => ProcessState.Failed,
            ProcessOutcome.Aborted => ProcessState.Aborted,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "未知过程结尾")
        };
        return true;
    }

    private void RequireNotTerminal(string what)
    {
        if (IsTerminal)
            throw new InvalidOperationException($"过程 {Id} 已终结（{State}），不能再{what}");
    }

    public override string ToString() =>
        $"过程#{Id}[{Kind}]{Subject} {ProcessContract.Of(State)} {_appliedSteps}/{DeclaredSteps}";
}

using JarSystem.Core.Domain;
using JarSystem.Core.Rules;
namespace JarSystem.Core.Process;

/// <summary>
/// **过程系统**（宿主唯一入口 / 门面）：创建过程、把实例登记在册、并在终结后收进"最近过程"。
///
/// P1 的边界（刻意的）：
/// <list type="bullet">
/// <item>只有"瞬时模式"——过程的全部步骤在同一次宿主意图内完成（<see cref="ProcessRun"/> 不发时间）；</item>
/// <item>不投影进 <c>GetState()</c>（活动过程在瞬时模式下恒为空，投影没有信息量，留到 P2/P4）；</item>
/// <item>不做修饰器与守护（P3）。</item>
/// </list>
///
/// 线程模型：与内核一致——**非线程安全**，由宿主的串行闸门（<c>JarSystemService.Gate</c>）保证互斥。
/// </summary>
public sealed class ProcessSystem
{
    /// <summary>最近过程保留条数（有界，避免长跑宿主无界增长；默认值集中在 <see cref="ProcessDefaults"/>）。</summary>
    public const int RecentCapacity = ProcessDefaults.RecentCapacity;

    private readonly ProcessJournal _journal = new();
    private readonly Func<int> _worldRevision;
    private readonly LogicalClock _clock = new();
    private readonly ModifierStack _modifiers;
    private readonly List<ProcessInstance> _active = new();
    private readonly List<ProcessInstance> _recent = new();
    private readonly IReadOnlyList<ProcessInstance> _activeView;
    private readonly IReadOnlyList<ProcessInstance> _recentView;
    private readonly Dictionary<int, (ProcessRun Run, IProcessAdvancer Advancer)> _work = new();
    private int _nextId;

    // ---- 主体 → 过程 的指针表（B2「步骤可观测」）----
    // 为什么需要它：帧只带引用（`PresentationFrame.Subject` / `.SubjectId`），而过程日志按
    // `JournalEntry.ProcessId` 组织。两者之间此前**没有任何公开映射**，于是"消费方拿着帧
    // 回查过程细节"这件事做不到。这张表就是缺的那一半指针。
    // 有界：与 ProcessJournal 同容量、同样的"满则成批丢最旧一半"（摊销 O(1)）。
    private readonly Dictionary<(string Subject, int SubjectId), int> _processBySubject = new();
    private readonly Queue<((string Subject, int SubjectId) Key, int ProcessId)> _subjectOrder = new();

    /// <param name="worldRevision">世界修订号提供者（P1 = 宿主世界意图的序号；见 <see cref="JournalEntry.WorldRevision"/>）。</param>
    /// <param name="allocator">步间隔分配器；null = 用默认（2 刻/步，见裁决 2026-10-16）。</param>
    /// <param name="modifiers">世界的修饰器栈（与工作台共用同一实例）；null = 自建一个空栈。</param>
    public ProcessSystem(Func<int> worldRevision, StepAllocator? allocator = null, ModifierStack? modifiers = null)
    {
        _worldRevision = worldRevision;
        _modifiers = modifiers ?? new ModifierStack();
        Allocator = allocator ?? new StepAllocator();
        Pipeline = new TickPipeline(_clock, _modifiers, DrainEvents, AdvanceActive);
        _activeView = _active.AsReadOnly();
        _recentView = _recent.AsReadOnly();
    }

    /// <summary>逻辑时钟（世界推进的唯一权威）。</summary>
    public LogicalClock Clock => _clock;

    /// <summary>tick 管线（宿主每推进一个 tick 调一次；见 <see cref="TickPipeline.AdvanceOneTick"/>）。</summary>
    public TickPipeline Pipeline { get; }

    /// <summary>步间隔分配器（默认 2 刻/步；可按过程种类或单次过程覆盖）。</summary>
    public StepAllocator Allocator { get; }

    /// <summary>世界的修饰器栈（与工作台共用同一实例）。</summary>
    public ModifierStack Modifiers => _modifiers;

    /// <summary>
    /// **世界事件源**（可选）：宿主接上它，事件就会在每个 tick 的"过程推进之前"被执行。
    /// null（默认）= 没有事件源 ⇒ 该步是空操作，行为与今天一致。
    /// </summary>
    public IWorldEventSource? EventSource { get; set; }

    /// <summary>
    /// **过程观察者**（可选）：宿主接它来驱动表现层（进度条/动画/日志）。
    /// null（默认）= 不通知，行为与今天一致。实现方不得抛异常、不得反向调用系统。
    /// </summary>
    public IProcessObserver? Observer { get; set; }

    /// <summary>
    /// **过程信号的第二个出口**（可选）：同一份通知原样再送给它，用来汇入世界总线。
    ///
    /// 为什么两个出口而不是把 <see cref="Observer"/> 换掉：`Observer` 是宿主的既有槽位
    /// （Web 层在 `Web/ProcessObserver.cs` 里把它翻成对外消息），换掉会静默改变宿主行为；
    /// 而总线是**内核**要用的通道。两个出口各接各的线，谁都不必知道对方存在。
    /// null（默认）= 只有 `Observer` 一个出口，行为与接入总线之前逐字节一致。
    ///
    /// 调用约定与 <see cref="Observer"/> 相同：**不得抛异常**，不得反向调用系统。
    /// </summary>
    public Action<ProcessInstance, string, int>? SignalSink { get; set; }

    /// <summary>管线第 ① 步：取出本 tick 的事件并执行（事件只改世界，不改过程状态）。</summary>
    internal void DrainEvents()
    {
        var source = EventSource;
        if (source == null) return;
        foreach (var e in source.DrainForTick(_clock.Tick))
            e.Effect?.Invoke();
    }

    /// <summary>过程日志（只读视图）。</summary>
    public ProcessJournal Journal => _journal;

    /// <summary>活动过程（P1 瞬时模式下通常为空——过程在同一 tick 内就结束了）。</summary>
    public IReadOnlyList<ProcessInstance> ActiveProcesses => _activeView;

    /// <summary>最近终结的过程（最近的在前，最多 <see cref="RecentCapacity"/> 条）。</summary>
    public IReadOnlyList<ProcessInstance> RecentProcesses => _recentView;

    /// <summary>累计创建过的过程数（含已终结）。</summary>
    public int TotalCreated { get; private set; }

    /// <summary>
    /// 主体索引的容量上限（与 <see cref="ProcessJournal.DefaultCapacity"/> 同口径）。
    /// 超出即成批淘汰最旧的一半 —— 长跑世界（例：策略实验 25 万单）不会把这张表撑爆。
    /// </summary>
    public const int SubjectIndexCapacity = ProcessJournal.DefaultCapacity;

    /// <summary>
    /// **按主体反查过程 id** —— 帧只带引用，这是配上它的那一半。
    ///
    /// 语义边界（刻意的）：
    /// <list type="bullet">
    /// <item>覆盖**已结束**的过程（映射不随 <c>Finish</c>/<c>Abort</c>/<c>Cancel</c> 移除，
    /// 否则"过程刚跑完就再也查不到它的步骤"）；</item>
    /// <item>同一主体被重新登记（例：同一目标工作台再次迁徙）时**保留最新一次**，
    /// 旧指针被覆盖而不计作丢弃 —— 主体仍然可查，只是指向最近那次过程；</item>
    /// <item>返回 <c>false</c> 有两种可能：该主体从未起过过程，或它的指针已被容量淘汰
    /// （见 <see cref="DroppedSubjectMappings"/>）。消费方必须把"查不到"读成
    /// "没有**保留的**指针"，而不是"没发生过"。</item>
    /// </list>
    /// </summary>
    public bool TryGetProcessId(string subject, int subjectId, out int processId) =>
        _processBySubject.TryGetValue((subject, subjectId), out processId);

    /// <summary>主体索引里现存的指针条数（恒 ≤ <see cref="SubjectIndexCapacity"/>）。</summary>
    public int IndexedSubjectCount => _processBySubject.Count;

    /// <summary>
    /// 被容量淘汰掉的主体指针条数（"丢了要能看出来"）。
    ///
    /// 与 <see cref="IndexedSubjectCount"/>、<see cref="IndexedSubjectKeys"/> 满足恒等式
    /// <c>IndexedSubjectCount + DroppedSubjectMappings == IndexedSubjectKeys</c>，
    /// 与 <see cref="ProcessJournal.FirstSeq"/> 的 <c>FirstSeq == DroppedEntries + 1</c> 同一手法：
    /// 消费方据此判断"我手上这份指针表是完整的、缺口正好在它前面"。
    /// </summary>
    public int DroppedSubjectMappings { get; private set; }

    /// <summary>
    /// 累计进入过索引的主体键数（同一主体在淘汰后重新登记会**再计一次**）。
    /// 它存在的唯一理由是让上面那条恒等式成立 —— 它同时也是"这张表一共服务过多少主体"的读数。
    /// </summary>
    public int IndexedSubjectKeys { get; private set; }

    /// <summary>
    /// 创建一个过程并返回其执行句柄（此时实例处于 <c>Ready</c>：已建立，但还没有开始推进）。
    /// </summary>
    public ProcessRun Begin(ProcessDefinition definition)
    {
        // 步间隔在这里解析（过程定义可覆盖分配器）——解析后写进实例与过程日志，
        // 于是"这个过程按什么节奏走"在日志里可查，而不是散在配置里。
        // 优先级：单次过程的显式覆盖 > 修饰器改拍（Retime）> 分配器默认
        int ticksPerStep = definition.TicksPerStep
            ?? _modifiers.TicksPerStepOverride(definition.Kind)
            ?? Allocator.For(definition.Kind);
        var instance = new ProcessInstance(++_nextId, definition, _clock.Tick, ticksPerStep);
        instance.MarkReady();
        _active.Add(instance);
        IndexSubject(instance);
        TotalCreated++;
        return new ProcessRun(instance, _journal, _worldRevision, tickSource: () => _clock.Tick,
            onClosed: Close, notify: (inst, reason, tick) =>
            {
                Observer?.OnChanged(inst, reason, tick);
                SignalSink?.Invoke(inst, reason, tick);
            });
    }

    /// <summary>把工作体挂到过程上（异步轨；<see cref="TickPipeline"/> 每个到达间隔的 tick 推它一个单位）。</summary>
    internal void AttachAdvancer(ProcessRun run, IProcessAdvancer advancer) =>
        _work[run.Instance.Id] = (run, advancer);

    /// <summary>
    /// **同步轨（drain）**：一个调用里把工作跑到完（不等待刻），随后收尾并结束过程。
    /// 与异步轨共用同一个工作体，因此"计划怎么走、失败怎么算"只有一份定义。
    /// </summary>
    internal void AdvanceToCompletion(ProcessRun run, IProcessAdvancer advancer)
    {
        while (advancer.AdvanceOneUnit(run)) { }
        advancer.OnFinished();
        run.Finish(advancer.Succeeded, advancer.FailureReason);
    }

    /// <summary>
    /// **异步轨**：每个到达步间隔的 tick 推进一个单位；单位用尽即收尾（交付 / 落账）并结束过程。
    /// 每个单位执行前先过**守卫**（世界是否还容得下这个计划）；守卫不成立则进 `Blocked` 并等宽限窗口。
    /// 遍历前先取键快照：推进可能使过程终结并从册上移除（改集合不能在遍历中进行）。
    /// </summary>
    private void AdvanceActive()
    {
        if (_work.Count == 0) return;
        var tick = _clock.Tick;
        // 同 tick 内的推进顺序按 Priority 升序（同优先级再按创建顺序）—— 这是 ProcessDefinition.Priority 的语义。
        // 此前它被声明却从未被读取（审查时实测发现），而"谁先推进"在同 tick 内是可观测的顺序。
        var ordered = _work.Keys.ToList();
        ordered.Sort((a, b) =>
        {
            var pa = _work[a].Run.Instance.Priority;
            var pb = _work[b].Run.Instance.Priority;
            return pa != pb ? pa.CompareTo(pb) : a.CompareTo(b);
        });
        foreach (var id in ordered)
        {
            var (run, advancer) = _work[id];
            var instance = run.Instance;
            if (instance.IsTerminal) { _work.Remove(id); continue; }

            // 时限：超过 deadline 即失败（闭区间：tick == deadline 仍算及时）。
            // 此前 DeadlineTick 只被投影给客户端、从不参与判定（审查时实测发现）。
            if (instance.DeadlineTick.HasValue && tick > instance.DeadlineTick.Value)
            {
                FailBlocked(run, advancer, "时限已到（未能在 " + instance.DeadlineTick.Value + " 刻前完成）", id);
                continue;
            }

            // 已阻断：每 tick 都重新问一次守卫（可能恢复，也可能熬到宽限耗尽）
            if (instance.State == ProcessState.Blocked)
            {
                var recheck = advancer.CheckGuard();
                if (recheck.Status == GuardStatus.Ok)
                {
                    run.NoteResumed();
                    // 恢复即继续：若本 tick 已到点就顺手推一个单位（不必再等一个间隔）
                    if (tick >= instance.NextUnitTick) AdvanceUnit(run, advancer, instance, tick, id);
                    continue;
                }
                bool expired = instance.BlockedAtTick.HasValue
                    && tick - instance.BlockedAtTick.Value >= instance.GraceTicks;
                if (recheck.Status == GuardStatus.Fatal || expired)
                {
                    var reason = recheck.Reason ?? instance.BlockedReason ?? "前置条件不再成立";
                    if (expired) run.NoteGraceExpired(reason);
                    FailBlocked(run, advancer, reason, id);
                }
                continue;
            }

            if (tick < instance.NextUnitTick) continue;

            var guard = advancer.CheckGuard();
            if (guard.Status != GuardStatus.Ok)
            {
                if (guard.Status == GuardStatus.Fatal)
                {
                    FailBlocked(run, advancer, guard.Reason ?? "前置条件永久不成立", id);
                    continue;
                }
                run.NoteBlocked(guard.Reason ?? "前置条件不成立");
                continue;
            }

            AdvanceUnit(run, advancer, instance, tick, id);
        }
    }

    /// <summary>
    /// **过程状态变化的唯一出口**。
    ///
    /// 为什么必须收成一个方法：Observer（宿主槽位，传活的 ProcessInstance）
    /// 与 SignalSink（世界总线的过程线，传轻量信号）是两个订阅面，但它们必须永远同步 ——
    /// 否则"总线打通了"只是说法，某一条路径漏接就静默失真。
    /// 这条约束不是推测：本类原先在 AdvanceUnit 里直接调了 Observer?.OnChanged(...)，
    /// 绕过了 Begin 传入的 notify 回调，于是 progressed 从来没上过总线。
    /// 一条"两槽位收到的事件集合必须相同"的测试当场把它抓了出来
    /// （test/JarSystem.Tests/ProcessLineParityTests.cs）。
    ///
    /// 新增任何过程状态变化时只能走这里，不要再直接碰 Observer。
    /// </summary>
    private void Notify(ProcessInstance instance, string reason, int tick)
    {
        Observer?.OnChanged(instance, reason, tick);
        SignalSink?.Invoke(instance, reason, tick);
    }

    /// <summary>推进一个单位；单位用尽时收尾并结束过程（含"交付 / 落账"）。</summary>
    private void AdvanceUnit(ProcessRun run, IProcessAdvancer advancer, ProcessInstance instance, int tick, int id)
    {
        bool more = advancer.AdvanceOneUnit(run);
        instance.NoteUnitAdvanced(tick);
        Notify(instance, ProcessObserverReasons.Progressed, tick);
        if (more) return;
        advancer.OnFinished();
        run.Finish(advancer.Succeeded, advancer.FailureReason);
        _work.Remove(id);
    }

    /// <summary>守卫判定失败：按失败路径收尾（已完成的部分不回滚 —— "失败要付代价"）。</summary>
    private void FailBlocked(ProcessRun run, IProcessAdvancer advancer, string reason, int id)
    {
        advancer.MarkFailed(reason);
        advancer.OnFinished();
        run.Finish(false, reason);
        _work.Remove(id);
    }

    /// <summary>
    /// **取消一个进行中的过程**（P4）：已完成的部分不回滚（"失败要付代价"）；
    /// 过程以 aborted 结尾，而它承载的工作（例：订单）按**失败**收尾（由工作体的 OnFinished 负责）。
    /// </summary>
    public (bool Cancelled, string? Error) Cancel(int processId, string reason)
    {
        if (!_work.TryGetValue(processId, out var work)) return (false, "没有这个进行中的过程");
        var (run, advancer) = work;
        advancer.MarkFailed(reason);
        advancer.OnFinished();
        run.Abort(reason);
        _work.Remove(processId);
        return (true, null);
    }

    /// <summary>
    /// 清空过程运行时（仅供世界重来这类入口调用；正常流程不调用）。
    /// 活动/最近过程、过程日志与逻辑时钟清理，修饰器窗口时钟重新锚定到 0；
    /// 过程 id 和累计创建数保持单调，这样旧句柄/旧客户端 id 不会在重置后误命中新过程。
    /// </summary>
    public void Clear()
    {
        _work.Clear();
        _active.Clear();
        _recent.Clear();
        _journal.Clear();
        // 主体索引与过程日志同生共死：日志清了还留着指针，等于给出一串指不到任何条目的死指针。
        // 计数器一并归零（与 ProcessJournal.Clear 把 DroppedEntries 归零同一口径）。
        _processBySubject.Clear();
        _subjectOrder.Clear();
        DroppedSubjectMappings = 0;
        IndexedSubjectKeys = 0;
        _clock.Reset();
        _modifiers.ResetClock();
    }

    /// <summary>
    /// 登记主体索引（只在 <see cref="Begin"/> 里发生一次）。
    ///
    /// 刻意**不**在 <c>Finish</c>/<c>Abort</c>/<c>Cancel</c> 里移除：过程一结束就查不到它的步骤，
    /// 等于"刚发生的事立刻不可观测"，与 B2 的目的相反。
    /// </summary>
    private void IndexSubject(ProcessInstance instance)
    {
        if (instance.SubjectId is not { } subjectId) return;   // 无结构化标识 ⇒ 无从反查，不登记
        var key = (instance.Subject, subjectId);
        if (!_processBySubject.ContainsKey(key)) IndexedSubjectKeys++;
        _processBySubject[key] = instance.Id;
        _subjectOrder.Enqueue((key, instance.Id));
        TrimSubjectIndex();
    }

    /// <summary>
    /// 主体索引的有界化：满则成批丢最旧的一半（与 <see cref="ProcessJournal"/> 同一口径）。
    ///
    /// 队列里的条目可能**陈旧**（同一主体被重新登记过，旧指针已被覆盖）：
    /// 出队时用"值仍是这条"判定它是不是现役指针，陈旧条目直接丢掉、不计入淘汰数
    /// —— 否则计数会把"覆盖"（主体照样可查）误报成"丢弃"。
    /// </summary>
    private void TrimSubjectIndex()
    {
        if (_processBySubject.Count < SubjectIndexCapacity)
        {
            // 没满：只清理队列头部的陈旧条目（防止"同一主体被反复登记"把队列撑大）
            if (_subjectOrder.Count > SubjectIndexCapacity * 2) DropStaleSubjectEntries();
            return;
        }

        int target = Math.Max(1, SubjectIndexCapacity / 2);
        int dropped = 0;
        while (dropped < target && _subjectOrder.Count > 0)
        {
            var (key, processId) = _subjectOrder.Dequeue();
            if (!_processBySubject.TryGetValue(key, out var current) || current != processId) continue;
            _processBySubject.Remove(key);
            dropped++;
        }
        DroppedSubjectMappings += dropped;
    }

    /// <summary>只丢队列头部的陈旧条目，遇到现役指针就停（不动容量、不计入淘汰数）。</summary>
    private void DropStaleSubjectEntries()
    {
        while (_subjectOrder.Count > 0)
        {
            var (key, processId) = _subjectOrder.Peek();
            if (_processBySubject.TryGetValue(key, out var current) && current == processId) return;
            _subjectOrder.Dequeue();
        }
    }

    private void Close(ProcessRun run)
    {
        if (!_active.Remove(run.Instance)) return;
        _recent.Insert(0, run.Instance);
        if (_recent.Count > RecentCapacity)
            _recent.RemoveRange(RecentCapacity, _recent.Count - RecentCapacity);
    }
}

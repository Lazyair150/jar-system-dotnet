namespace JarSystem.Core.Presentation;

/// <summary>
/// **表现层帧流** —— 核心对"世界如何被看见"唯一承诺的东西。
///
/// 它解决的问题（2026-11 审计的结论）：核心原本有两条**原语**流，
/// 但**没有一条是"给表现层用的"**：
/// <list type="bullet">
/// <item><c>WorldBus</c>：广播，语义是"世界变了"，订阅方可反应（可变世界），也可观察（只读）；</item>
/// <item><c>ProcessJournal</c>：过程级步进的追加日志，有 seq、有界、可查缺口。</item>
/// </list>
/// 两条都不是"按时间排好、带帧号、可被慢放消费"的东西，于是每个宿主各自造了一份适配层
/// （<c>JarSystem.Web</c> 的 <c>ProcessObserver</c> 五条消息、demo 的 12 种事件）。
/// 这不是重复劳动的问题，而是**契约不在核心里**：三个宿主三套语义，谁也不知道自己漏了什么。
///
/// 本类的定位很窄，且刻意窄：
/// <list type="number">
/// <item>**它是投影，不是第二个真相**。它只订阅总线、只存帧，从不回写世界；
///       删掉它，世界的行为与回放字节完全不变（由 <c>PresentationFeedTests</c> 守着）。</item>
/// <item>**它只管顺序与时间，不管内容**。帧里没有状态（见 <see cref="PresentationFrame"/>）。</item>
/// <item>**它不定节奏**。核心只保证"帧按发生顺序排好、帧号单调"；
///       "多久播一帧"是宿主的参数，与 <c>TickIntervalMs</c> 一样属于表现层，不属于内核。</item>
/// </list>
///
/// 有界保留沿用 <c>ProcessJournal</c> 的裁决与手法（满时成批丢最旧一半，摊销 O(1)），
/// 理由也相同：长时间跑的世界不可能把所有帧留在内存里；而**缺口必须可查**，
/// 否则"没收到"会被误读成"没发生"。恒等式：<c>FirstSeq == DroppedFrames + 1</c>（有保留时）。
/// </summary>
public sealed class PresentationFeed
{
    /// <summary>默认保留帧数。与 <c>ProcessJournal.DefaultCapacity</c> 取同量级：够回看一段过程史，又不至于长跑爆内存。</summary>
    public const int DefaultCapacity = 4096;

    private readonly List<PresentationFrame> _frames = new();
    private readonly IReadOnlyList<PresentationFrame> _view;
    private readonly List<IPresentationSink> _sinks = new();
    private readonly int _capacity;
    private int _seq;

    /// <param name="capacity">保留帧数上限；≤0 表示不限制（仅供测试/实验显式声明）。</param>
    public PresentationFeed(int capacity = DefaultCapacity)
    {
        _capacity = capacity;
        _view = _frames.AsReadOnly();
    }

    /// <summary>当前保留的全部帧（只读活视图）。</summary>
    public IReadOnlyList<PresentationFrame> Frames => _view;

    /// <summary>保留帧数。</summary>
    public int Count => _frames.Count;

    /// <summary>因容量上限被丢弃的最旧帧数。</summary>
    public int DroppedFrames { get; private set; }

    /// <summary>保留帧里的最小帧号（无帧时为 0）。</summary>
    public int FirstSeq => _frames.Count > 0 ? _frames[0].Seq : 0;

    /// <summary>保留帧里的最大帧号（无帧时为 0）。</summary>
    public int LastSeq => _frames.Count > 0 ? _frames[^1].Seq : 0;

    /// <summary>
    /// 历史是否已被裁剪。为 <c>true</c> 时消费方**不得**假设"从第 1 帧开始就能看全"，
    /// 必须把"没有这一帧"读成"没有**保留的**这一帧"。
    /// </summary>
    public bool IsTruncated => DroppedFrames > 0;

    /// <summary>注册一个接收方（按注册顺序同步收到此后产生的帧）。</summary>
    public void Subscribe(IPresentationSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!_sinks.Contains(sink)) _sinks.Add(sink);
    }

    /// <summary>注销接收方（世界重来时宿主可能换了）。</summary>
    public bool Unsubscribe(IPresentationSink sink) => _sinks.Remove(sink);

    /// <summary>
    /// 游标式读取：返回帧号**大于** <paramref name="afterSeq"/> 的保留帧。
    ///
    /// 这是宿主"慢放"的全部机制：宿主记住自己播到哪一帧（<c>afterSeq</c>），
    /// 按时来取新的；取多取少、多快取，都是宿主的事。核心不睡眠、不排队、不回调定时器。
    /// 若 <paramref name="afterSeq"/> 落后于 <see cref="FirstSeq"/>，说明中间有帧已被裁剪 ——
    /// 调用方应先看 <see cref="IsTruncated"/> 再决定是"接着播"还是"重新对齐"。
    /// </summary>
    public IReadOnlyList<PresentationFrame> Since(int afterSeq)
    {
        if (_frames.Count == 0) return Array.Empty<PresentationFrame>();
        // 帧号连续 ⇒ 下标可以直接算，不必线性扫。
        int start = afterSeq - _frames[0].Seq + 1;
        if (start < 0) start = 0;
        if (start >= _frames.Count) return Array.Empty<PresentationFrame>();
        var slice = new PresentationFrame[_frames.Count - start];
        for (int i = 0; i < slice.Length; i++) slice[i] = _frames[start + i];
        return slice;
    }

    /// <summary>按帧号取一帧（已被裁剪时返回 false —— 这不是异常，是"历史太久了"）。</summary>
    public bool TryGet(int seq, out PresentationFrame frame)
    {
        if (_frames.Count > 0)
        {
            int index = seq - _frames[0].Seq;
            if (index >= 0 && index < _frames.Count)
            {
                frame = _frames[index];
                return true;
            }
        }

        frame = default;
        return false;
    }

    /// <summary>清空（仅供 <c>JarSystem.Reset()</c> 这类世界重来的入口）：帧、丢弃计数、帧号一并归零；接收方保留。</summary>
    public void Clear()
    {
        _frames.Clear();
        DroppedFrames = 0;
        _seq = 0;
    }

    /// <summary>
    /// 追加一帧并同步派发给接收方。
    ///
    /// **只允许核心里发帧的那一处调用**（<c>World.BusPresentationBridge</c>）：
    /// 任何"宿主自己往帧流里塞一条"的做法，都会让帧流不再是世界的投影。
    /// </summary>
    internal void Append(PresentationKind kind, string op, string? subject, int? subjectId,
        int tick, int revision)
    {
        // 有界保留：满时成批丢最旧的一半（与 ProcessJournal 同一手法与理由：逐条搬列表会退化成 O(n²)）。
        if (_capacity > 0 && _frames.Count >= _capacity)
        {
            int drop = Math.Min(Math.Max(1, _capacity / 2), _frames.Count);
            _frames.RemoveRange(0, drop);
            DroppedFrames += drop;
        }

        _seq++;
        var frame = new PresentationFrame(_seq, kind, op, subject, subjectId, tick, revision);
        _frames.Add(frame);

        // 同步派发，按注册顺序。接收方不得抛异常（见 IPresentationSink 的纪律）；
        // 这里不 try/catch 兜底 —— 兜底会把"宿主违反了纪律"变成静默，而那正是要暴露的东西。
        for (int i = 0; i < _sinks.Count; i++) _sinks[i].OnFrame(in frame);
    }
}

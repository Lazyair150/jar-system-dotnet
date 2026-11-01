namespace JarSystem.Core.Process;

/// <summary>
/// **过程日志**（追加写）：与既有的 <c>WorldCommand</c> 日志构成两层——
/// <list type="bullet">
/// <item><c>WorldCommand</c>：**世界级意图**（宿主下过什么命令）；</item>
/// <item>本日志：**过程级步进**（意图内部一步步发生了什么）。</item>
/// </list>
/// 两层粒度不同，因此分开：世界回放不必背过程细节，过程日志也能独立裁剪。
///
/// 约束：**append-only**（只 <see cref="Append"/>，没有删除/修改单个条目的入口）；
/// 对外只给只读包装（沿用 <c>CommandExecutor.History</c> / <c>SwitchOperationLog.Entries</c> 的手法）。
///
/// **有界保留（2026-10 裁决）**：长时间运行的世界（例：策略实验一次跑 25 万单）不可能把每一步
/// 都留在内存里，因此日志按"最近保留"策略裁剪：容量 <paramref name="capacity"/> 满时**丢弃最旧的一半**
/// （摊销 O(1) —— 每条目最多被搬动两次，而不是每次追加都搬整个列表）。
/// 序号 <c>seq</c> 仍然单调，因此消费方可以据此发现缺口；丢弃条数记在 <see cref="DroppedEntries"/>。
/// </summary>
public sealed class ProcessJournal
{
    /// <summary>默认保留条数（够覆盖一段可读的过程史，又不会长跑爆内存）。</summary>
    public const int DefaultCapacity = 4096;

    private readonly List<JournalEntry> _entries = new();
    private readonly IReadOnlyList<JournalEntry> _view;
    private readonly int _capacity;
    private int _seq;

    /// <param name="capacity">保留条数上限；≤0 表示不限制（仅供测试/实验显式声明）。</param>
    public ProcessJournal(int capacity = DefaultCapacity)
    {
        _capacity = capacity;
        _view = _entries.AsReadOnly();
    }

    /// <summary>全部条目（只读视图，活视图）。</summary>
    public IReadOnlyList<JournalEntry> Entries => _view;

    /// <summary>条目数。</summary>
    public int Count => _entries.Count;

    /// <summary>因容量上限被丢弃的最旧条目数（序号仍然是单调的，缺口据此可查）。</summary>
    public int DroppedEntries { get; private set; }

    /// <summary>
    /// 保留条目里的最小序号（无条目时为 0）。
    /// 与 <see cref="DroppedEntries"/> 满足恒等式 <c>FirstSeq == DroppedEntries + 1</c>（有保留时），
    /// 因此消费方可以用它验证"我手上这段历史是连续的、缺口正好在它前面"。
    /// </summary>
    public int FirstSeq => _entries.Count > 0 ? _entries[0].Seq : 0;

    /// <summary>保留条目里的最大序号（无条目时为 0）。</summary>
    public int LastSeq => _entries.Count > 0 ? _entries[^1].Seq : 0;

    /// <summary>
    /// 历史是否已被裁剪。为 <c>true</c> 时"从头回看这条过程"是不可能的：
    /// 消费方必须据此把"没有记录"读成"没有**保留的**记录"，而不是"没发生过"。
    /// </summary>
    public bool IsTruncated => DroppedEntries > 0;

    /// <summary>追加一条（返回落盘后的条目，便于调用方回填过程的日志游标）。</summary>
    public JournalEntry Append(int tick, int processId, string kind, int? stepIndex = null,
        string? operation = null, string? modifier = null, string? reason = null,
        int worldRevision = 0, string? summary = null)
    {
        // 有界保留：满时**成批**丢弃最旧的一半（摊销 O(1)；逐条搬整个列表会让长跑退化成 O(n²)）。
        if (_capacity > 0 && _entries.Count >= _capacity)
        {
            int drop = Math.Min(Math.Max(1, _capacity / 2), _entries.Count);
            _entries.RemoveRange(0, drop);
            DroppedEntries += drop;
        }

        _seq++;
        var entry = new JournalEntry
        {
            Seq = _seq,
            Tick = tick,
            ProcessId = processId,
            Kind = kind,
            StepIndex = stepIndex,
            Operation = operation,
            Modifier = modifier,
            Reason = reason,
            WorldRevision = worldRevision,
            Summary = summary
        };
        _entries.Add(entry);
        return entry;
    }

    /// <summary>清空（仅供世界重来这类入口调用，例 <c>JarSystem.Reset()</c>）：条目与丢弃计数一并归零。</summary>
    public void Clear()
    {
        _entries.Clear();
        DroppedEntries = 0;
        _seq = 0;
    }
}

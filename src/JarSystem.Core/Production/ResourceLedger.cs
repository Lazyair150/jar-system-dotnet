using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// 资源变化的**唯一原因词表**（规格：<c>SPEC-demo-campaign.md</c> 的 No Mysterious Resources Contract）。
///
/// 每一个资源增减都必须能指到这里的**一个**词：说不出来源的变化就是"神秘的资源"，
/// 而表现层不许猜、不许补 —— 它只能照着这个词把事实讲出来。
///
/// 词是稳定字符串（会进契约/日志），不随实现改名：
/// <list type="bullet">
/// <item><c>production</c> 世界自己产（形状产线出罐、液体产能出液）</item>
/// <item><c>stock</c> 开局库存（关卡文件里写死的初始量）</item>
/// <item><c>flow</c> 外部资源库存流入工作台资源空间：生产区 Warehouse 或自机液库进入 Workshop 的 Jar/游离液体/订单/预置账户</item>
/// <item><c>transfer</c> 工作台账户之间的资源转移：包括主台 → 从台及订单子账/游离液体/Jar 存储账户之间的显式连接</item>
/// <item><c>return</c> 资源归还：订单结算剩余液体返回自机液库，或主台预置事务回滚至自机液库</item>
/// <item><c>discard</c> 容量溢出被丢弃</item>
/// </list>
/// </summary>
public static class ResourceCauses
{
    public const string Production = "production";
    public const string Stock = "stock";
    public const string Flow = "flow";
    public const string Transfer = "transfer";
    public const string Return = "return";
    public const string Discard = "discard";

    /// <summary>全部原因（顺序即本文件的文档顺序；测试用它钉"没有第七种没登记的来源"）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Production, Stock, Flow, Transfer, Return, Discard
    };
}

/// <summary>资源的两种可计数量纲。罐子记只数，液体记份数 —— 它们不混在一个账上。</summary>
public static class ResourceKinds
{
    public const string Jar = "jar";
    public const string Liquid = "liquid";
}

/// <summary>
/// 一条资源变化事实：**谁、因为什么、从哪到哪、多少**。
///
/// 它是只读事实，不是指令：读它不会改变世界（与 <c>PresentationFeed</c> 同一纪律）。
/// <see cref="Seq"/> 在整个账本生命周期内单调递增（清空也不回绕）——
/// 于是"中间丢过事件"这件事可被读者一眼看出，而不是假装连续。
/// </summary>
public sealed class ResourceEvent
{
    public long Seq { get; init; }
    public int Tick { get; init; }
    public string Cause { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public int Amount { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Reason { get; init; }

    public override string ToString() =>
        $"#{Seq}@{Tick} {Cause} {Kind} {Name}×{Amount}{(From == null ? "" : " " + From + "→" + To)}" +
        (Reason == null ? "" : " (" + Reason + ")");
}

/// <summary>
/// 有界资源账本：按发生顺序记 <see cref="ResourceEvent"/>，满了丢最旧的（并计数）。
///
/// **为什么有界**：这是一个长跑世界，账本不能无限膨胀；而"丢了"必须是**可见的事实**
/// （<see cref="Dropped"/> 与 <see cref="Retained"/> 一起读），不能静默截断 ——
/// 否则读者会把"没记"误当成"没发生"。容量口径照 <c>ProcessJournal</c>：成批丢最旧一半，
/// 摊还到每次记录是 O(1)。
///
/// 归属：世界（<see cref="JarSystem.ResourceLedger"/>）持有一个，写入点只有资源真正变动的地方
/// （产出 / 流入 / 丢弃 / 装配库存），读取点可以有很多。
/// </summary>
public sealed class ResourceLedger
{
    /// <summary>默认容量（条）。够一局看完整因果链，又不至于长跑吃内存。</summary>
    public const int DefaultCapacity = 512;

    private readonly object _gate = new();
    private readonly List<ResourceEvent> _entries = new();
    private readonly HashSet<string> _operationIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceTransferReceipt> _transferReceipts = new(StringComparer.Ordinal);
    private long _seq;
    private int _dropped;

    public ResourceLedger(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new DomainValidationException("资源账本容量必须是正整数: " + capacity);
        Capacity = capacity;
    }

    /// <summary>容量（条）。</summary>
    public int Capacity { get; }

    /// <summary>因容量不足已丢弃的条数（单调递增）。</summary>
    public int Dropped { get { lock (_gate) return _dropped; } private set => _dropped = value; }

    /// <summary>当前保留的条数（≤ <see cref="Capacity"/>）。</summary>
    public int Retained { get { lock (_gate) return _entries.Count; } }

    /// <summary>最后一条的序号；空账本为 0。读者用它判断"我是不是漏看了"。</summary>
    public long LastSeq { get { lock (_gate) return _seq; } }

    /// <summary>保留的事件（按发生顺序，最旧在前）。只读视图。</summary>
    public IReadOnlyList<ResourceEvent> Entries
    {
        get
        {
            lock (_gate)
                return Array.AsReadOnly(_entries.Select(Clone).ToArray());
        }
    }

    /// <summary>捕获完整账本游标、裁剪计数和当前保留事件。</summary>
    public ResourceLedgerSnapshot Capture()
    {
        lock (_gate)
            return new ResourceLedgerSnapshot(Capacity, _seq, Dropped, _entries, _operationIds, _transferReceipts.Values);
    }

    /// <summary>
    /// 从快照恢复账本。保留原始 Seq/Dropped，不重新编号历史事件，
    /// 因而增量消费者可在恢复后继续使用旧游标。
    /// </summary>
    public static ResourceLedger Restore(ResourceLedgerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Capacity < 1 || snapshot.LastSeq < 0 || snapshot.Dropped < 0 ||
            snapshot.Entries.Count > snapshot.Capacity ||
            snapshot.OperationIds.Any(string.IsNullOrWhiteSpace) ||
            snapshot.OperationIds.Distinct(StringComparer.Ordinal).Count() != snapshot.OperationIds.Count)
            throw new DomainValidationException("资源账本快照的容量或游标无效。");

        long previousSeq = 0;
        foreach (var item in snapshot.Entries)
        {
            if (item.Seq <= previousSeq || item.Seq > snapshot.LastSeq ||
                item.Tick < 0 || item.Amount <= 0 ||
                !ResourceCauses.All.Contains(item.Cause, StringComparer.Ordinal) ||
                (item.Kind != ResourceKinds.Jar && item.Kind != ResourceKinds.Liquid) ||
                string.IsNullOrWhiteSpace(item.Name))
                throw new DomainValidationException("资源账本快照包含无效或乱序事件。");
            previousSeq = item.Seq;
        }
        if (snapshot.Entries.Count > 0 && snapshot.Entries[^1].Seq != snapshot.LastSeq)
            throw new DomainValidationException("资源账本快照的最后事件序号与 LastSeq 不一致。");
        if (snapshot.TransferReceipts.Any(item =>
                item is null ||
                !snapshot.OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                item.Outcome is not (ResourceTransferOutcome.Transferred or ResourceTransferOutcome.PartiallyTransferred)) ||
            snapshot.TransferReceipts.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count() != snapshot.TransferReceipts.Count)
            throw new DomainValidationException("资源账本快照的转移回执与 OperationId 索引不一致。");
        foreach (var receipt in snapshot.TransferReceipts) receipt.Validate();

        var ledger = new ResourceLedger(snapshot.Capacity)
        {
            _seq = snapshot.LastSeq,
            Dropped = snapshot.Dropped
        };
        ledger._entries.AddRange(snapshot.Entries.Select(Clone));
        foreach (var operationId in snapshot.OperationIds)
            ledger._operationIds.Add(operationId);
        foreach (var receipt in snapshot.TransferReceipts)
            ledger._transferReceipts.Add(receipt.OperationId, receipt with { });
        return ledger;
    }

    /// <summary>将已验证账本快照装入同容量账本，保留原始序号与裁剪游标。</summary>
    public void RestoreFrom(ResourceLedgerSnapshot snapshot)
    {
        var restored = Restore(snapshot);
        if (restored.Capacity != Capacity)
            throw new DomainValidationException("资源账本快照容量与当前账本不一致。");

        lock (_gate)
        {
            // Reserve every backing store before replacing any live state.
            _entries.EnsureCapacity(restored._entries.Count);
            _operationIds.EnsureCapacity(restored._operationIds.Count);
            _transferReceipts.EnsureCapacity(restored._transferReceipts.Count);

            _entries.Clear();
            _entries.AddRange(restored._entries);
            _seq = restored._seq;
            Dropped = restored.Dropped;

            _operationIds.Clear();
            foreach (var operationId in restored._operationIds)
                _operationIds.Add(operationId);

            _transferReceipts.Clear();
            foreach (var item in restored._transferReceipts)
                _transferReceipts.Add(item.Key, item.Value with { });
        }
    }

    private static ResourceEvent Clone(ResourceEvent item) => new()
    {
        Seq = item.Seq,
        Tick = item.Tick,
        Cause = item.Cause,
        Kind = item.Kind,
        Name = item.Name,
        Amount = item.Amount,
        From = item.From,
        To = item.To,
        Reason = item.Reason
    };

    /// <summary>
    /// 记一条事实。<paramref name="amount"/> ≤ 0 时**不记**（"没变化"不是事实），返回 null。
    /// 不校验 cause 是否在 <see cref="ResourceCauses.All"/> 里（调用方是内核自己），
    /// 但测试会钉住"内核只产出已登记的原因"。
    /// </summary>
    public ResourceEvent? Record(int tick, string cause, string kind, string name, int amount,
        string? from = null, string? to = null, string? reason = null)
    {
        if (amount <= 0) return null;
        lock (_gate)
        {
            var nextSeq = checked(_seq + 1);
            _entries.EnsureCapacity(checked(_entries.Count + 1));
            var e = new ResourceEvent
            {
                Seq = nextSeq, Tick = tick, Cause = cause, Kind = kind, Name = name,
                Amount = amount, From = from, To = to, Reason = reason
            };
            _entries.Add(e);
            _seq = nextSeq;
            Trim();
            return e;
        }
    }

    /// <summary>
    /// 以稳定操作 ID 原子记录一组账本事实。重复操作 ID 不会再次写入任何事件；
    /// 操作 ID 与账本事件一起进入快照，事件被容量裁剪后去重标记仍保留。
    /// </summary>
    public bool RecordBatchOnce(
        string operationId, int tick, IEnumerable<ResourceLedgerMutation> mutations)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("账本操作 ID 不能为空。", nameof(operationId));
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        ArgumentNullException.ThrowIfNull(mutations);
        lock (_gate)
        {
        if (_operationIds.Contains(operationId)) return false;

        var batch = mutations.Where(item => item.Amount > 0).ToArray();
        if (batch.Length == 0)
            throw new DomainValidationException("账本批次至少必须包含一条正数量资源事实。");
        foreach (var item in batch)
        {
            if (string.IsNullOrWhiteSpace(item.Cause) ||
                string.IsNullOrWhiteSpace(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Name) ||
                (item.Kind != ResourceKinds.Jar && item.Kind != ResourceKinds.Liquid))
                throw new DomainValidationException("账本批次包含无效资源事实。");
        }

        // 先构造全部事件，只有整批成功构造后才推进游标并发布。
        var nextSeq = _seq;
        var events = new List<ResourceEvent>(batch.Length);
        foreach (var item in batch)
        {
            nextSeq = checked(nextSeq + 1);
            events.Add(new ResourceEvent
            {
                Seq = nextSeq,
                Tick = tick,
                Cause = item.Cause,
                Kind = item.Kind,
                Name = item.Name,
                Amount = item.Amount,
                From = item.From,
                To = item.To,
                Reason = item.Reason
            });
        }
        // Allocate all backing storage before publishing either the operation marker or
        // its facts. After these reservations, publication is a bounded in-memory append;
        // this avoids a capacity-growth exception splitting the event batch from its ID.
        _entries.EnsureCapacity(checked(_entries.Count + events.Count));
        _operationIds.EnsureCapacity(checked(_operationIds.Count + 1));
        if (!_operationIds.Add(operationId)) return false;
        _entries.AddRange(events);
        _seq = nextSeq;
        Trim();
        return true;
        }
    }

    /// <summary>
    /// Atomically publishes a transfer's event batch, OperationId and final validated receipt.
    /// A repeated OperationId is a no-op only when it is already bound to the same receipt.
    /// </summary>
    public bool RecordTransferOnce(
        string operationId,
        int tick,
        IEnumerable<ResourceLedgerMutation> mutations,
        ResourceTransferReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("账本操作 ID 不能为空。", nameof(operationId));
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        if (!string.Equals(operationId, receipt.OperationId, StringComparison.Ordinal))
            throw new DomainValidationException("Transfer receipt OperationId does not match its ledger key.");
        if (receipt.Outcome is not (ResourceTransferOutcome.Transferred or ResourceTransferOutcome.PartiallyTransferred))
            throw new DomainValidationException("Only a committed transfer can enter the receipt journal.");

        lock (_gate)
        {
            if (_operationIds.Contains(operationId))
            {
                if (_transferReceipts.TryGetValue(operationId, out var prior) && prior != receipt)
                    throw new InvalidOperationException("OperationId is already bound to a different transfer receipt.");
                return false;
            }

            var batch = mutations.ToArray();
            if (batch.Length == 0 || batch.Any(item =>
                    item.Amount <= 0 ||
                    !ResourceCauses.All.Contains(item.Cause, StringComparer.Ordinal) ||
                    (item.Kind != ResourceKinds.Jar && item.Kind != ResourceKinds.Liquid) ||
                    string.IsNullOrWhiteSpace(item.Name)))
                throw new DomainValidationException("Transfer ledger batch contains an invalid resource fact.");
            if (batch.Sum(item => (long)item.Amount) != receipt.TransferredAmount)
                throw new DomainValidationException("Transfer ledger amount does not match the final receipt.");

            var nextSeq = _seq;
            var events = new List<ResourceEvent>(batch.Length);
            foreach (var item in batch)
            {
                nextSeq = checked(nextSeq + 1);
                events.Add(new ResourceEvent
                {
                    Seq = nextSeq, Tick = tick, Cause = item.Cause, Kind = item.Kind,
                    Name = item.Name, Amount = item.Amount, From = item.From, To = item.To, Reason = item.Reason
                });
            }

            // All potentially allocating work happens before the first visible mutation.
            _entries.EnsureCapacity(checked(_entries.Count + events.Count));
            _operationIds.EnsureCapacity(checked(_operationIds.Count + 1));
            _transferReceipts.EnsureCapacity(checked(_transferReceipts.Count + 1));

            if (!_operationIds.Add(operationId)) return false;
            _entries.AddRange(events);
            _transferReceipts.Add(operationId, receipt with { });
            _seq = nextSeq;
            Trim();
            return true;
        }
    }

    public bool TryGetTransferReceipt(string operationId, out ResourceTransferReceipt? receipt)
    {
        lock (_gate)
        {
            if (_transferReceipts.TryGetValue(operationId, out var stored))
            {
                receipt = stored with { };
                return true;
            }
            receipt = null;
            return false;
        }
    }

    public bool HasRecordedOperation(string operationId)
    {
        lock (_gate) return _operationIds.Contains(operationId);
    }

    /// <summary>清空保留的事件（序号不回绕，于是"清过"也能从序号跳变看出）。</summary>
    public void Clear() { lock (_gate) _entries.Clear(); }

    /// <summary>按原因筛选（如"这回合的产出都有哪些"）。顺序保持发生顺序。</summary>
    public IReadOnlyList<ResourceEvent> For(string cause)
    {
        lock (_gate) return _entries.Where(e => e.Cause == cause).Select(Clone).ToList();
    }

    /// <summary>从 <paramref name="afterSeq"/> 之后的事件（供增量读、断线重连补差）。</summary>
    public IReadOnlyList<ResourceEvent> Since(long afterSeq)
    {
        lock (_gate) return _entries.Where(e => e.Seq > afterSeq).Select(Clone).ToList();
    }

    private void Trim()
    {
        if (_entries.Count <= Capacity) return;
        var drop = Math.Min(Math.Max(1, Capacity / 2), _entries.Count);
        _entries.RemoveRange(0, drop);
        Dropped += drop;
    }
}


/// <summary>资源账本纯值快照，包含增量读取所需的连续性元数据。</summary>
public sealed class ResourceLedgerSnapshot
{
    public ResourceLedgerSnapshot(
        int capacity, long lastSeq, int dropped, IEnumerable<ResourceEvent> entries,
        IEnumerable<string>? operationIds = null,
        IEnumerable<ResourceTransferReceipt>? transferReceipts = null)
    {
        Capacity = capacity;
        LastSeq = lastSeq;
        Dropped = dropped;
        Entries = Array.AsReadOnly(entries.Select(item => new ResourceEvent
        {
            Seq = item.Seq,
            Tick = item.Tick,
            Cause = item.Cause,
            Kind = item.Kind,
            Name = item.Name,
            Amount = item.Amount,
            From = item.From,
            To = item.To,
            Reason = item.Reason
        }).ToArray());
        OperationIds = Array.AsReadOnly((operationIds ?? Array.Empty<string>())
            .OrderBy(item => item, StringComparer.Ordinal).ToArray());
        TransferReceipts = Array.AsReadOnly((transferReceipts ?? Array.Empty<ResourceTransferReceipt>())
            .OrderBy(item => item.OperationId, StringComparer.Ordinal)
            .Select(item => item with { }).ToArray());
    }

    public int Capacity { get; }
    public long LastSeq { get; }
    public int Dropped { get; }
    public IReadOnlyList<ResourceEvent> Entries { get; }
    public IReadOnlyList<string> OperationIds { get; }
    public IReadOnlyList<ResourceTransferReceipt> TransferReceipts { get; }
    public int Retained => Entries.Count;
}


/// <summary>单条资源账本事实的不可变输入；由 RecordBatchOnce 作为一个操作整体提交。</summary>
public sealed record ResourceLedgerMutation(
    string Cause, string Kind, string Name, int Amount,
    string? From = null, string? To = null, string? Reason = null);

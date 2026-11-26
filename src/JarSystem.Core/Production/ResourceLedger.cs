using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// <item><c>transfer</c> 工作台账户之间的资源转移：包括主台 → 从台及订单子账/游离液体/Jar 存储账户之间的显式连接；B 类订单交付 Jar 进入明确的 <c>order.delivery</c> ExternalSink 也记为 transfer</item>
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
    /// <summary>稳定机器身份；旧版历史事件可为 null，绝不从展示名称推断。</summary>
    public string? ResourceIdentity { get; init; }
    /// <summary>产生该事实的幂等操作；普通历史记录与旧事件可为 null。</summary>
    public string? OperationId { get; init; }
    /// <summary>Legacy display alias; consumers should prefer DisplayName.</summary>
    public string Name { get; init; } = "";
    /// <summary>Localized presentation text; never participates in resource identity or operation fingerprints.</summary>
    public string? DisplayName { get; init; }
    public int Amount { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    /// <summary>Canonical source account identity; null denotes legacy/unknown provenance.</summary>
    public string? FromAccountId { get; init; }
    /// <summary>Canonical target account identity; null denotes legacy/unknown provenance.</summary>
    public string? ToAccountId { get; init; }
    public ResourceEndpoint FromEndpoint { get; init; } = ResourceEndpoint.LegacyUnknown;
    public ResourceEndpoint ToEndpoint { get; init; } = ResourceEndpoint.LegacyUnknown;
    public string? Reason { get; init; }

    public override string ToString() =>
        $"#{Seq}@{Tick} {Cause} {Kind} {DisplayName ?? Name}×{Amount}{(From == null ? "" : " " + From + "→" + To)}" +
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
    private readonly Dictionary<string, string> _operationFingerprints = new(StringComparer.Ordinal);
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
            return new ResourceLedgerSnapshot(Capacity, _seq, Dropped, _entries, _operationIds, _transferReceipts.Values, _operationFingerprints);
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
            snapshot.OperationIds.Distinct(StringComparer.Ordinal).Count() != snapshot.OperationIds.Count ||
            snapshot.OperationFingerprints.Any(item => string.IsNullOrWhiteSpace(item.Key) ||
                !snapshot.OperationIds.Contains(item.Key, StringComparer.Ordinal) ||
                item.Value == null || item.Value.Length != 64 || item.Value.Any(character => !Uri.IsHexDigit(character))))
            throw new DomainValidationException("资源账本快照的容量或游标无效。");

        long previousSeq = 0;
        foreach (var item in snapshot.Entries)
        {
            if (item.Seq <= previousSeq || item.Seq > snapshot.LastSeq ||
                item.Tick < 0 || item.Amount <= 0 ||
                !item.FromEndpoint.IsValid || !item.ToEndpoint.IsValid ||
                (item.FromAccountId != null && item.FromEndpoint != ResourceEndpoint.ForAccount(item.FromAccountId)) ||
                (item.ToAccountId != null && item.ToEndpoint != ResourceEndpoint.ForAccount(item.ToAccountId)) ||
                (item.OperationId != null && !snapshot.OperationIds.Contains(item.OperationId, StringComparer.Ordinal)) ||
                (item.OperationId != null && !snapshot.OperationFingerprints.ContainsKey(item.OperationId)) ||
                (item.OperationId != null && string.IsNullOrWhiteSpace(item.ResourceIdentity)) ||
                (item.FromAccountId != null && string.IsNullOrWhiteSpace(item.FromAccountId)) ||
                (item.ToAccountId != null && string.IsNullOrWhiteSpace(item.ToAccountId)) ||
                (item.OperationId != null && item.Kind == ResourceKinds.Jar && !IsCanonicalJarId(item.ResourceIdentity)) ||
                !ResourceCauses.All.Contains(item.Cause, StringComparer.Ordinal) ||
                (item.Kind != ResourceKinds.Jar && item.Kind != ResourceKinds.Liquid) ||
                string.IsNullOrWhiteSpace(item.Name) ||
                (item.DisplayName != null && string.IsNullOrWhiteSpace(item.DisplayName)))
                throw new DomainValidationException("资源账本快照包含无效或乱序事件。");
            ValidateAccountEndpointWorldScope(item.FromEndpoint, item.ToEndpoint);
            previousSeq = item.Seq;
        }
        if (snapshot.Entries.Count > 0 && snapshot.Entries[^1].Seq != snapshot.LastSeq)
            throw new DomainValidationException("资源账本快照的最后事件序号与 LastSeq 不一致。");
        if (snapshot.TransferReceipts.Any(item =>
                item is null ||
                !snapshot.OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                !snapshot.OperationFingerprints.ContainsKey(item.OperationId) ||
                item.Outcome is not (ResourceTransferOutcome.Transferred or ResourceTransferOutcome.PartiallyTransferred)) ||
            snapshot.TransferReceipts.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count() != snapshot.TransferReceipts.Count)
            throw new DomainValidationException("资源账本快照的转移回执与 OperationId 索引不一致。");
        foreach (var receipt in snapshot.TransferReceipts)
        {
            receipt.Validate();
            ValidateTransferReceiptResourceIdentity(receipt);
        }

        var ledger = new ResourceLedger(snapshot.Capacity)
        {
            _seq = snapshot.LastSeq,
            Dropped = snapshot.Dropped
        };
        ledger._entries.AddRange(snapshot.Entries.Select(Clone));
        foreach (var operationId in snapshot.OperationIds)
            ledger._operationIds.Add(operationId);
        foreach (var item in snapshot.OperationFingerprints)
            ledger._operationFingerprints.Add(item.Key, item.Value);
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
            _operationFingerprints.EnsureCapacity(restored._operationFingerprints.Count);

            _entries.Clear();
            _entries.AddRange(restored._entries);
            _seq = restored._seq;
            Dropped = restored.Dropped;

            _operationIds.Clear();
            foreach (var operationId in restored._operationIds)
                _operationIds.Add(operationId);

            _transferReceipts.Clear();
            foreach (var item in restored._transferReceipts)
                _transferReceipts.Add(item.Key, item.Value);

            _operationFingerprints.Clear();
            foreach (var item in restored._operationFingerprints)
                _operationFingerprints.Add(item.Key, item.Value);
        }
    }

    private static string ComputeOperationFingerprint(
        IReadOnlyList<ResourceLedgerMutation> mutations,
        ResourceTransferReceipt? receipt)
    {
        // ResourceIdentity is the stable machine key. Name/DisplayName are presentation-only.
        var canonicalMutations = mutations.Select(item => new
        {
            item.Cause, item.Kind, item.ResourceIdentity, item.Amount,
            item.Reason, item.FromAccountId, item.ToAccountId, item.FromEndpoint, item.ToEndpoint
        }).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            Mutations = canonicalMutations,
            Receipt = receipt
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    // Schema 8 fingerprints included the legacy Name field and tagged endpoints, but not DisplayName.
    private static string ComputeSchema8OperationFingerprint(
        IReadOnlyList<ResourceLedgerMutation> mutations,
        ResourceTransferReceipt? receipt)
    {
        var schema8Mutations = mutations.Select(item => new
        {
            item.Cause, item.Kind, item.Name, item.Amount, item.From, item.To,
            item.Reason, item.ResourceIdentity, item.FromAccountId, item.ToAccountId,
            item.FromEndpoint, item.ToEndpoint
        }).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            Mutations = schema8Mutations,
            Receipt = receipt
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    // Schema 3-7 fingerprints predate tagged endpoints. Preserve their exact payload shape.
    private static string ComputeLegacyOperationFingerprint(
        IReadOnlyList<ResourceLedgerMutation> mutations,
        ResourceTransferReceipt? receipt)
    {
        var legacyMutations = mutations.Select(item => new
        {
            item.Cause, item.Kind, item.Name, item.Amount, item.From, item.To,
            item.Reason, item.ResourceIdentity, item.FromAccountId, item.ToAccountId
        }).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            Mutations = legacyMutations,
            Receipt = receipt
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static void ValidateAccountEndpointWorldScope(
        ResourceEndpoint source,
        ResourceEndpoint target)
    {
        if (source.Kind != ResourceEndpointKind.Account ||
            target.Kind != ResourceEndpointKind.Account)
            return;

        var sourceIsV2 = source.AccountId!.StartsWith("account:v2:", StringComparison.Ordinal);
        var targetIsV2 = target.AccountId!.StartsWith("account:v2:", StringComparison.Ordinal);
        if (sourceIsV2 != targetIsV2)
            throw new DomainValidationException("Resource ledger cannot connect legacy and World-scoped account identities.");
        if (sourceIsV2 &&
            AccountIdentity.ParseV2(source.AccountId!).Owner.WorldInstanceId !=
            AccountIdentity.ParseV2(target.AccountId!).Owner.WorldInstanceId)
            throw new DomainValidationException("Resource ledger cannot connect accounts from different Worlds.");
    }

    private static ResourceLedgerMutation NormalizeMutationIdentity(ResourceLedgerMutation item)
    {
        // In the current World Vocabulary, a liquid's canonical identity is its vocabulary key.
        // Jar identities are never inferred from Shape/Name and must be supplied explicitly.
        // Legacy mutations that do not opt into DisplayName retain their old vocabulary-key
        // fallback. New presentation-separated writes must provide ResourceIdentity explicitly.
        var normalized = item.ResourceIdentity == null &&
            item.DisplayName == null && item.Kind == ResourceKinds.Liquid
                ? item with { ResourceIdentity = item.Name }
                : item;
        return normalized with
        {
            DisplayName = normalized.DisplayName ?? normalized.Name,
            FromEndpoint = normalized.FromEndpoint ??
                (normalized.FromAccountId == null ? ResourceEndpoint.LegacyUnknown : ResourceEndpoint.ForAccount(normalized.FromAccountId)),
            ToEndpoint = normalized.ToEndpoint ??
                (normalized.ToAccountId == null ? ResourceEndpoint.LegacyUnknown : ResourceEndpoint.ForAccount(normalized.ToAccountId))
        };
    }

    private static bool IsCanonicalJarId(string? identity) =>
        int.TryParse(identity, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var jarId) && jarId > 0 &&
        jarId.ToString(System.Globalization.CultureInfo.InvariantCulture) == identity;

    private static void ValidateTransferReceiptResourceIdentity(ResourceTransferReceipt receipt)
    {
        if (receipt.ResourceKind == RuleResourceKind.Jar &&
            (receipt.RequestedAmount != 1 || receipt.TransferredAmount != 1 ||
             receipt.RetainedAtSourceAmount != 0 || receipt.DiscardedAmount != 0 ||
             !int.TryParse(receipt.ResourceIdentity, System.Globalization.NumberStyles.None,
                 System.Globalization.CultureInfo.InvariantCulture, out var jarId) ||
             jarId <= 0 ||
             jarId.ToString(System.Globalization.CultureInfo.InvariantCulture) != receipt.ResourceIdentity))
            throw new DomainValidationException(
                "Jar transfer receipts must identify one canonical positive JarId and transfer exactly one Jar.");
    }

    private static ResourceEvent Clone(ResourceEvent item) => new()
    {
        Seq = item.Seq,
        Tick = item.Tick,
        Cause = item.Cause,
        Kind = item.Kind,
        Name = item.DisplayName ?? item.Name,
        DisplayName = item.DisplayName ?? item.Name,
        Amount = item.Amount,
        From = item.From,
        To = item.To,
        FromAccountId = item.FromAccountId,
        ToAccountId = item.ToAccountId,
        Reason = item.Reason,
        ResourceIdentity = item.ResourceIdentity,
        OperationId = item.OperationId,
        FromEndpoint = item.FromEndpoint,
        ToEndpoint = item.ToEndpoint
    };

    /// <summary>
    /// 记一条事实。<paramref name="amount"/> ≤ 0 时**不记**（"没变化"不是事实），返回 null。
    /// 不校验 cause 是否在 <see cref="ResourceCauses.All"/> 里（调用方是内核自己），
    /// 但测试会钉住"内核只产出已登记的原因"。
    /// </summary>
    public ResourceEvent? Record(int tick, string cause, string kind, string name, int amount,
        string? from = null, string? to = null, string? reason = null,
        string? resourceIdentity = null, string? fromAccountId = null, string? toAccountId = null,
        ResourceEndpoint? fromEndpoint = null, ResourceEndpoint? toEndpoint = null,
        string? displayName = null)
    {
        if (amount <= 0) return null;
        if (displayName != null && string.IsNullOrWhiteSpace(displayName))
            throw new DomainValidationException("Resource DisplayName cannot be blank.");
        if (displayName != null && string.IsNullOrWhiteSpace(resourceIdentity))
            throw new DomainValidationException("Explicit DisplayName requires a canonical ResourceIdentity.");
        lock (_gate)
        {
            var nextSeq = checked(_seq + 1);
            _entries.EnsureCapacity(checked(_entries.Count + 1));
            var e = new ResourceEvent
            {
                Seq = nextSeq, Tick = tick, Cause = cause, Kind = kind,
                Name = displayName ?? name, DisplayName = displayName ?? name,
                ResourceIdentity = resourceIdentity, Amount = amount, From = from, To = to,
                FromAccountId = fromAccountId, ToAccountId = toAccountId, Reason = reason,
                FromEndpoint = fromEndpoint ?? (fromAccountId == null
                    ? ResourceEndpoint.LegacyUnknown : ResourceEndpoint.ForAccount(fromAccountId)),
                ToEndpoint = toEndpoint ?? (toAccountId == null
                    ? ResourceEndpoint.LegacyUnknown : ResourceEndpoint.ForAccount(toAccountId))
            };
            e.FromEndpoint.Validate();
            e.ToEndpoint.Validate();
            if ((fromAccountId != null && e.FromEndpoint != ResourceEndpoint.ForAccount(fromAccountId)) ||
                (toAccountId != null && e.ToEndpoint != ResourceEndpoint.ForAccount(toAccountId)))
                throw new DomainValidationException("Resource event account ID conflicts with its tagged endpoint.");
            ValidateAccountEndpointWorldScope(e.FromEndpoint, e.ToEndpoint);
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
        var batch = mutations.Where(item => item.Amount > 0)
            .Select(NormalizeMutationIdentity).ToArray();
        var fingerprint = ComputeOperationFingerprint(batch, receipt: null);
        if (_operationIds.Contains(operationId))
        {
            if (_operationFingerprints.TryGetValue(operationId, out var priorFingerprint) &&
                !string.Equals(priorFingerprint, fingerprint, StringComparison.Ordinal) &&
                !string.Equals(priorFingerprint, ComputeSchema8OperationFingerprint(batch, receipt: null), StringComparison.Ordinal) &&
                !string.Equals(priorFingerprint, ComputeLegacyOperationFingerprint(batch, receipt: null), StringComparison.Ordinal))
                throw new InvalidOperationException("OperationId is already bound to a different ledger batch payload.");
            return false; // Legacy tombstones have no fingerprint and remain non-replayable no-ops.
        }
        // A zero-resource settlement advances its domain lease, not the resource ledger.
        // Do not create a phantom OperationId/fingerprint when there are no facts to publish.
        if (batch.Length == 0) return false;

        foreach (var item in batch)
        {
            if (string.IsNullOrWhiteSpace(item.Cause) ||
                !ResourceCauses.All.Contains(item.Cause, StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Name) ||
                (item.DisplayName != null && string.IsNullOrWhiteSpace(item.DisplayName)) ||
                string.IsNullOrWhiteSpace(item.ResourceIdentity) ||
                (item.FromAccountId != null && string.IsNullOrWhiteSpace(item.FromAccountId)) ||
                (item.ToAccountId != null && string.IsNullOrWhiteSpace(item.ToAccountId)) ||
                !item.FromEndpoint!.Value.IsValid || !item.ToEndpoint!.Value.IsValid ||
                (item.FromAccountId != null && item.FromEndpoint.Value != ResourceEndpoint.ForAccount(item.FromAccountId)) ||
                (item.ToAccountId != null && item.ToEndpoint.Value != ResourceEndpoint.ForAccount(item.ToAccountId)) ||
                (item.Kind != ResourceKinds.Jar && item.Kind != ResourceKinds.Liquid) ||
                (item.Kind == ResourceKinds.Jar &&
                    (item.Amount != 1 || !IsCanonicalJarId(item.ResourceIdentity))))
                throw new DomainValidationException("账本批次包含无效资源事实。");
            ValidateAccountEndpointWorldScope(item.FromEndpoint!.Value, item.ToEndpoint!.Value);
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
                Name = item.DisplayName ?? item.Name,
                DisplayName = item.DisplayName ?? item.Name,
                ResourceIdentity = item.ResourceIdentity,
                OperationId = operationId,
                Amount = item.Amount,
                From = item.From,
                To = item.To,
                FromAccountId = item.FromAccountId,
                ToAccountId = item.ToAccountId,
                FromEndpoint = item.FromEndpoint!.Value,
                ToEndpoint = item.ToEndpoint!.Value,
                Reason = item.Reason
            });
        }
        // Allocate all backing storage before publishing either the operation marker or
        // its facts. After these reservations, publication is a bounded in-memory append;
        // this avoids a capacity-growth exception splitting the event batch from its ID.
        _entries.EnsureCapacity(checked(_entries.Count + events.Count));
        _operationIds.EnsureCapacity(checked(_operationIds.Count + 1));
        _operationFingerprints.EnsureCapacity(checked(_operationFingerprints.Count + 1));
        if (!_operationIds.Add(operationId)) return false;
        _operationFingerprints.Add(operationId, fingerprint);
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
        ValidateTransferReceiptResourceIdentity(receipt);

        lock (_gate)
        {
            var sourceAccountId = receipt.SourceAccount.AccountId;
            var targetAccountId = receipt.TargetAccount.AccountId;
            var batch = mutations.Select(NormalizeMutationIdentity)
                .Select(item => item with
                {
                    FromAccountId = item.FromAccountId ?? sourceAccountId,
                    ToAccountId = item.ToAccountId ?? targetAccountId
                }).ToArray();
            var expectedKind = receipt.ResourceKind == RuleResourceKind.Jar
                ? ResourceKinds.Jar
                : ResourceKinds.Liquid;
            if (batch.Length == 0 ||
                (receipt.ResourceKind == RuleResourceKind.Jar && batch.Length != 1) ||
                batch.Any(item =>
                    item.Amount <= 0 ||
                    (item.Cause != ResourceCauses.Transfer && item.Cause != ResourceCauses.Flow) ||
                    item.Kind != expectedKind ||
                    string.IsNullOrWhiteSpace(item.Name) ||
                    (item.DisplayName != null && string.IsNullOrWhiteSpace(item.DisplayName)) ||
                    item.ResourceIdentity != receipt.ResourceIdentity ||
                    item.From != receipt.SourceAccount.OwnerId ||
                    item.To != receipt.TargetAccount.OwnerId ||
                    item.FromAccountId != sourceAccountId ||
                    item.ToAccountId != targetAccountId ||
                    (receipt.ResourceKind == RuleResourceKind.Jar &&
                        (item.Amount != 1 || !IsCanonicalJarId(item.ResourceIdentity)))))
                throw new DomainValidationException("Transfer ledger batch does not describe the receipt's exact resource and account endpoints.");
            if (batch.Sum(item => (long)item.Amount) != receipt.TransferredAmount)
                throw new DomainValidationException("Transfer ledger amount does not match the final receipt.");
            var fingerprint = ComputeOperationFingerprint(batch, receipt);
            if (_operationIds.Contains(operationId))
            {
                if (_transferReceipts.TryGetValue(operationId, out var prior) && prior != receipt)
                    throw new InvalidOperationException("OperationId is already bound to a different transfer receipt.");
                if (_operationFingerprints.TryGetValue(operationId, out var priorFingerprint) &&
                    !string.Equals(priorFingerprint, fingerprint, StringComparison.Ordinal) &&
                    !string.Equals(priorFingerprint, ComputeSchema8OperationFingerprint(batch, receipt), StringComparison.Ordinal) &&
                    !string.Equals(priorFingerprint, ComputeLegacyOperationFingerprint(batch, receipt), StringComparison.Ordinal))
                    throw new InvalidOperationException("OperationId is already bound to a different transfer payload.");
                return false;
            }

            // Clone before any visible mutation so the final journal publication does not allocate.
            var receiptCopy = receipt with { };
            var nextSeq = _seq;
            var events = new List<ResourceEvent>(batch.Length);
            foreach (var item in batch)
            {
                nextSeq = checked(nextSeq + 1);
                events.Add(new ResourceEvent
                {
                    Seq = nextSeq, Tick = tick, Cause = item.Cause, Kind = item.Kind,
                    Name = item.DisplayName ?? item.Name,
                    DisplayName = item.DisplayName ?? item.Name,
                    ResourceIdentity = item.ResourceIdentity, OperationId = operationId,
                    Amount = item.Amount, From = item.From, To = item.To,
                    FromAccountId = item.FromAccountId, ToAccountId = item.ToAccountId,
                    FromEndpoint = item.FromEndpoint!.Value,
                    ToEndpoint = item.ToEndpoint!.Value,
                    Reason = item.Reason
                });
            }

            // All potentially allocating work happens before the first visible mutation.
            _entries.EnsureCapacity(checked(_entries.Count + events.Count));
            _operationIds.EnsureCapacity(checked(_operationIds.Count + 1));
            _transferReceipts.EnsureCapacity(checked(_transferReceipts.Count + 1));
            _operationFingerprints.EnsureCapacity(checked(_operationFingerprints.Count + 1));

            if (!_operationIds.Add(operationId)) return false;
            _entries.AddRange(events);
            _transferReceipts.Add(operationId, receiptCopy);
            _operationFingerprints.Add(operationId, fingerprint);
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
        IEnumerable<ResourceTransferReceipt>? transferReceipts = null,
        IReadOnlyDictionary<string, string>? operationFingerprints = null)
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
            Name = item.DisplayName ?? item.Name,
            DisplayName = item.DisplayName ?? item.Name,
            Amount = item.Amount,
            From = item.From,
            To = item.To,
            FromAccountId = item.FromAccountId,
            ToAccountId = item.ToAccountId,
            FromEndpoint = item.FromEndpoint.Kind == ResourceEndpointKind.LegacyUnknown && item.FromAccountId != null
                ? ResourceEndpoint.ForAccount(item.FromAccountId) : item.FromEndpoint,
            ToEndpoint = item.ToEndpoint.Kind == ResourceEndpointKind.LegacyUnknown && item.ToAccountId != null
                ? ResourceEndpoint.ForAccount(item.ToAccountId) : item.ToEndpoint,
            Reason = item.Reason,
            ResourceIdentity = item.ResourceIdentity,
            OperationId = item.OperationId
        }).ToArray());
        OperationIds = Array.AsReadOnly((operationIds ?? Array.Empty<string>())
            .OrderBy(item => item, StringComparer.Ordinal).ToArray());
        TransferReceipts = Array.AsReadOnly((transferReceipts ?? Array.Empty<ResourceTransferReceipt>())
            .OrderBy(item => item.OperationId, StringComparer.Ordinal)
            .Select(item => item with { }).ToArray());
        OperationFingerprints = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            (operationFingerprints ?? new Dictionary<string, string>())
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
    }

    public int Capacity { get; }
    public long LastSeq { get; }
    public int Dropped { get; }
    public IReadOnlyList<ResourceEvent> Entries { get; }
    public IReadOnlyList<string> OperationIds { get; }
    public IReadOnlyList<ResourceTransferReceipt> TransferReceipts { get; }
    public IReadOnlyDictionary<string, string> OperationFingerprints { get; }
    public int Retained => Entries.Count;
}


/// <summary>单条资源账本事实的不可变输入；由 RecordBatchOnce 作为一个操作整体提交。</summary>
public sealed record ResourceLedgerMutation(
    string Cause, string Kind, string Name, int Amount,
    string? From = null, string? To = null, string? Reason = null,
    string? ResourceIdentity = null,
    string? FromAccountId = null, string? ToAccountId = null,
    ResourceEndpoint? FromEndpoint = null, ResourceEndpoint? ToEndpoint = null,
    string? DisplayName = null);

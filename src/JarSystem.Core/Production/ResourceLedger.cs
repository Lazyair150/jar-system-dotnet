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
    /// <summary>若事件属于复合资源转移批次，则指向该批次的共同 OperationId。</summary>
    public string? BatchOperationId { get; init; }
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
    private readonly Dictionary<string, ResourceLedgerTransferBatchReceipt> _transferBatchReceipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceLedgerProducedJarImportReceipt> _producedJarImportReceipts = new(StringComparer.Ordinal);
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
            return new ResourceLedgerSnapshot(Capacity, _seq, Dropped, _entries, _operationIds,
                _transferReceipts.Values, _operationFingerprints, _transferBatchReceipts.Values,
                _producedJarImportReceipts.Values);
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
        if (snapshot.TransferBatchReceipts.Any(item => item == null) ||
            snapshot.TransferBatchReceipts.Select(item => item.OperationId)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.TransferBatchReceipts.Count)
            throw new DomainValidationException("资源账本快照包含重复或空的复合转移回执。");

        if (snapshot.ProducedJarImportReceipts.Any(item => item is null) ||
            snapshot.ProducedJarImportReceipts.Select(item => item.LedgerOperationId)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.ProducedJarImportReceipts.Count ||
            snapshot.ProducedJarImportReceipts.Select(item => item.OperationId)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.ProducedJarImportReceipts.Count)
            throw new DomainValidationException("资源账本快照包含重复或空的外部 Jar 导入回执。");
        foreach (var receipt in snapshot.ProducedJarImportReceipts)
        {
            receipt.Validate();
            if (!snapshot.OperationIds.Contains(receipt.LedgerOperationId, StringComparer.Ordinal) ||
                !snapshot.OperationFingerprints.ContainsKey(receipt.LedgerOperationId))
                throw new DomainValidationException("外部 Jar 导入回执与账本 OperationId / 指纹索引不一致。");
        }

        var batchIds = new HashSet<string>(StringComparer.Ordinal);
        var batchLegOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var transferReceiptsById = snapshot.TransferReceipts.ToDictionary(item => item.OperationId, StringComparer.Ordinal);
        foreach (var batchReceipt in snapshot.TransferBatchReceipts)
        {
            batchReceipt.Validate();
            if (!batchIds.Add(batchReceipt.OperationId) ||
                !snapshot.OperationIds.Contains(batchReceipt.OperationId, StringComparer.Ordinal) ||
                !snapshot.OperationFingerprints.ContainsKey(batchReceipt.OperationId))
                throw new DomainValidationException("资源账本复合回执与批次 OperationId 索引不一致.");

            foreach (var legReceipt in batchReceipt.LegReceipts)
            {
                if (!batchLegOwners.TryAdd(legReceipt.OperationId, batchReceipt.OperationId) ||
                    !snapshot.OperationIds.Contains(legReceipt.OperationId, StringComparer.Ordinal) ||
                    !snapshot.OperationFingerprints.ContainsKey(legReceipt.OperationId) ||
                    !transferReceiptsById.TryGetValue(legReceipt.OperationId, out var storedLeg) ||
                    storedLeg != legReceipt)
                    throw new DomainValidationException("资源账本复合回执缺少精确的 Leg OperationId / Receipt 索引。");
            }
            if (!HasValidTransferBatchFingerprint(batchReceipt, snapshot.OperationFingerprints))
                throw new DomainValidationException("资源账本复合回执父指纹与批次/Leg 指纹不一致。");
        }
        if (batchLegOwners.Keys.Any(batchIds.Contains))
            throw new DomainValidationException("资源账本的批次 OperationId 与其他批次 Leg OperationId 发生冲突。");
        foreach (var item in snapshot.Entries)
        {
            var expectedBatchId = item.OperationId == null
                ? null : batchLegOwners.GetValueOrDefault(item.OperationId);
            var isBatchLeg = expectedBatchId != null;
            if ((item.BatchOperationId != null &&
                    (!isBatchLeg || !string.Equals(item.BatchOperationId, expectedBatchId, StringComparison.Ordinal))) ||
                (item.BatchOperationId == null && isBatchLeg))
                throw new DomainValidationException("资源账本事件引用了不存在、缺失或不匹配的复合转移批次。");
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
        foreach (var receipt in snapshot.TransferBatchReceipts)
            ledger._transferBatchReceipts.Add(receipt.OperationId, receipt.DeepCopy());
        foreach (var receipt in snapshot.ProducedJarImportReceipts)
            ledger._producedJarImportReceipts.Add(receipt.LedgerOperationId, receipt.DeepCopy());
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
            _transferBatchReceipts.EnsureCapacity(restored._transferBatchReceipts.Count);
            _producedJarImportReceipts.EnsureCapacity(restored._producedJarImportReceipts.Count);
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

            _transferBatchReceipts.Clear();
            foreach (var item in restored._transferBatchReceipts)
                _transferBatchReceipts.Add(item.Key, item.Value);

            _producedJarImportReceipts.Clear();
            foreach (var item in restored._producedJarImportReceipts)
                _producedJarImportReceipts.Add(item.Key, item.Value);

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

    internal static bool HasValidTransferBatchFingerprint(
        ResourceLedgerTransferBatchReceipt receipt,
        IReadOnlyDictionary<string, string> operationFingerprints)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(operationFingerprints);
        if (!operationFingerprints.TryGetValue(receipt.OperationId, out var parentFingerprint) ||
            receipt.LegReceipts.Any(item => !operationFingerprints.ContainsKey(item.OperationId)))
            return false;
        return string.Equals(parentFingerprint,
            ComputeTransferBatchFingerprint(receipt, operationFingerprints),
            StringComparison.Ordinal);
    }

    private static string ComputeTransferBatchFingerprint(
        ResourceLedgerTransferBatchReceipt receipt,
        IReadOnlyDictionary<string, string> legFingerprints)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Receipt = receipt,
            Legs = receipt.LegReceipts.Select(item => new
            {
                item.OperationId,
                Fingerprint = legFingerprints[item.OperationId]
            }).ToArray()
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
        BatchOperationId = item.BatchOperationId,
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
    /// Atomically publishes an externally-produced Jar batch, its resource facts, idempotency
    /// marker/fingerprint, and the exact per-shape capacity outcome used by later replays.
    /// Existing legacy operation markers without this receipt remain outcome-unknown.
    /// </summary>
    public bool RecordProducedJarImportOnce(
        string ledgerOperationId,
        int tick,
        IEnumerable<ResourceLedgerMutation> mutations,
        ResourceLedgerProducedJarImportReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(ledgerOperationId))
            throw new ArgumentException("账本操作 ID 不能为空。", nameof(ledgerOperationId));
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        if (!string.Equals(ledgerOperationId, receipt.LedgerOperationId, StringComparison.Ordinal))
            throw new DomainValidationException("外部 Jar 导入回执的账本操作 ID 与提交键不一致。");

        lock (_gate)
        {
            if (_operationIds.Contains(ledgerOperationId))
            {
                if (_producedJarImportReceipts.TryGetValue(ledgerOperationId, out var prior) &&
                    !prior.HasSamePayload(receipt))
                    throw new InvalidOperationException("OperationId is already bound to a different produced-Jar import receipt.");
                return false;
            }

            var storedReceipt = receipt.DeepCopy();
            _producedJarImportReceipts.EnsureCapacity(checked(_producedJarImportReceipts.Count + 1));
            if (!RecordBatchOnce(ledgerOperationId, tick, mutations))
                return false;

            _producedJarImportReceipts.Add(ledgerOperationId, storedReceipt);
            return true;
        }
    }

    /// <summary>读取首次提交时持久保存的外部 Jar 导入结果；不依赖可能被容量裁剪的事件。</summary>
    public bool TryGetProducedJarImportReceipt(
        string ledgerOperationId,
        out ResourceLedgerProducedJarImportReceipt? receipt)
    {
        if (string.IsNullOrWhiteSpace(ledgerOperationId))
            throw new ArgumentException("账本操作 ID 不能为空。", nameof(ledgerOperationId));
        lock (_gate)
        {
            if (_producedJarImportReceipts.TryGetValue(ledgerOperationId, out var stored))
            {
                receipt = stored.DeepCopy();
                return true;
            }
            receipt = null;
            return false;
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

    /// <summary>
    /// Atomically publishes one composite batch marker, every leg OperationId/receipt and all
    /// leg events. No leg becomes independently durable before the complete batch is published.
    /// </summary>
    public bool RecordTransferBatchOnce(
        string operationId,
        int tick,
        IEnumerable<ResourceLedgerTransferBatchLeg> legs,
        ResourceLedgerTransferBatchReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("账本批次操作 ID 不能为空。", nameof(operationId));
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(receipt);
        receipt.Validate();
        if (!string.Equals(operationId, receipt.OperationId, StringComparison.Ordinal))
            throw new DomainValidationException("Transfer batch receipt OperationId does not match its ledger key.");

        var suppliedLegs = legs.ToArray();
        if (suppliedLegs.Length == 0 ||
            suppliedLegs.Any(item => item == null || item.Receipt == null || item.Mutations == null) ||
            suppliedLegs.Length != receipt.LegReceipts.Count ||
            !suppliedLegs.Select(item => item.Receipt).SequenceEqual(receipt.LegReceipts))
            throw new DomainValidationException("Transfer batch legs do not match the composite receipt order and identity.");

        lock (_gate)
        {
            var prepared = new List<(ResourceTransferReceipt Receipt, ResourceLedgerMutation[] Mutations, string Fingerprint)>(suppliedLegs.Length);
            var childIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var leg in suppliedLegs)
            {
                var legReceipt = leg.Receipt;
                legReceipt.Validate();
                if (legReceipt.Outcome is not (ResourceTransferOutcome.Transferred or ResourceTransferOutcome.PartiallyTransferred))
                    throw new DomainValidationException("Only committed transfer legs can enter the resource ledger.");
                ValidateTransferReceiptResourceIdentity(legReceipt);
                if (!childIds.Add(legReceipt.OperationId) ||
                    string.Equals(legReceipt.OperationId, operationId, StringComparison.Ordinal))
                    throw new DomainValidationException("Transfer batch contains a duplicate or self-referential leg OperationId.");

                var sourceAccountId = legReceipt.SourceAccount.AccountId;
                var targetAccountId = legReceipt.TargetAccount.AccountId;
                var expectedKind = legReceipt.ResourceKind == RuleResourceKind.Jar
                    ? ResourceKinds.Jar
                    : ResourceKinds.Liquid;
                var batch = leg.Mutations.Select(NormalizeMutationIdentity)
                    .Select(item => item with
                    {
                        FromAccountId = item.FromAccountId ?? sourceAccountId,
                        ToAccountId = item.ToAccountId ?? targetAccountId,
                        FromEndpoint = item.FromAccountId == null &&
                            item.FromEndpoint == ResourceEndpoint.LegacyUnknown
                                ? ResourceEndpoint.ForAccount(sourceAccountId) : item.FromEndpoint,
                        ToEndpoint = item.ToAccountId == null &&
                            item.ToEndpoint == ResourceEndpoint.LegacyUnknown
                                ? ResourceEndpoint.ForAccount(targetAccountId) : item.ToEndpoint
                    }).ToArray();

                if (batch.Length == 0 ||
                    (legReceipt.ResourceKind == RuleResourceKind.Jar && batch.Length != 1) ||
                    batch.Any(item =>
                        item.Amount <= 0 ||
                        (item.Cause != ResourceCauses.Transfer && item.Cause != ResourceCauses.Flow) ||
                        item.Kind != expectedKind ||
                        string.IsNullOrWhiteSpace(item.Name) ||
                        (item.DisplayName != null && string.IsNullOrWhiteSpace(item.DisplayName)) ||
                        item.ResourceIdentity != legReceipt.ResourceIdentity ||
                        item.From != legReceipt.SourceAccount.OwnerId ||
                        item.To != legReceipt.TargetAccount.OwnerId ||
                        item.FromAccountId != sourceAccountId ||
                        item.ToAccountId != targetAccountId ||
                        !item.FromEndpoint!.Value.IsValid || !item.ToEndpoint!.Value.IsValid ||
                        item.FromEndpoint.Value != ResourceEndpoint.ForAccount(sourceAccountId) ||
                        item.ToEndpoint.Value != ResourceEndpoint.ForAccount(targetAccountId) ||
                        (legReceipt.ResourceKind == RuleResourceKind.Jar &&
                            (item.Amount != 1 || !IsCanonicalJarId(item.ResourceIdentity)))))
                    throw new DomainValidationException("Transfer batch leg mutations do not describe the receipt's exact resource and account endpoints.");
                if (batch.Sum(item => (long)item.Amount) != legReceipt.TransferredAmount)
                    throw new DomainValidationException("Transfer batch leg amount does not match its final receipt.");

                foreach (var item in batch)
                    ValidateAccountEndpointWorldScope(item.FromEndpoint!.Value, item.ToEndpoint!.Value);

                prepared.Add((legReceipt with { }, batch,
                    ComputeOperationFingerprint(batch, legReceipt)));
            }

            var receiptCopy = receipt.DeepCopy();
            var legFingerprints = prepared.ToDictionary(
                item => item.Receipt.OperationId, item => item.Fingerprint, StringComparer.Ordinal);
            var batchFingerprint = ComputeTransferBatchFingerprint(receiptCopy, legFingerprints);

            if (_operationIds.Contains(operationId))
            {
                if (!_transferBatchReceipts.TryGetValue(operationId, out var priorBatch) ||
                    !priorBatch.HasSamePayload(receiptCopy) ||
                    !_operationFingerprints.TryGetValue(operationId, out var priorBatchFingerprint) ||
                    !string.Equals(priorBatchFingerprint, batchFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("OperationId is already bound to a different or incomplete transfer batch.");
                foreach (var leg in prepared)
                {
                    if (!_operationIds.Contains(leg.Receipt.OperationId) ||
                        !_transferReceipts.TryGetValue(leg.Receipt.OperationId, out var priorReceipt) ||
                        priorReceipt != leg.Receipt ||
                        !_operationFingerprints.TryGetValue(leg.Receipt.OperationId, out var priorLegFingerprint) ||
                        !string.Equals(priorLegFingerprint, leg.Fingerprint, StringComparison.Ordinal))
                        throw new InvalidOperationException("A previously recorded transfer batch has an incomplete or conflicting leg index.");
                }
                return false;
            }

            if (prepared.Any(item => _operationIds.Contains(item.Receipt.OperationId)))
                throw new InvalidOperationException("A transfer batch leg OperationId is already committed outside this batch.");

            var nextSeq = _seq;
            var events = new List<ResourceEvent>(prepared.Sum(item => item.Mutations.Length));
            foreach (var leg in prepared)
            {
                foreach (var item in leg.Mutations)
                {
                    nextSeq = checked(nextSeq + 1);
                    events.Add(new ResourceEvent
                    {
                        Seq = nextSeq, Tick = tick, Cause = item.Cause, Kind = item.Kind,
                        Name = item.DisplayName ?? item.Name,
                        DisplayName = item.DisplayName ?? item.Name,
                        ResourceIdentity = item.ResourceIdentity,
                        OperationId = leg.Receipt.OperationId,
                        BatchOperationId = operationId,
                        Amount = item.Amount, From = item.From, To = item.To,
                        FromAccountId = item.FromAccountId, ToAccountId = item.ToAccountId,
                        FromEndpoint = item.FromEndpoint!.Value,
                        ToEndpoint = item.ToEndpoint!.Value,
                        Reason = item.Reason
                    });
                }
            }

            // Reserve every backing store before publishing any part of the composite transaction.
            _entries.EnsureCapacity(checked(_entries.Count + events.Count));
            _operationIds.EnsureCapacity(checked(_operationIds.Count + prepared.Count + 1));
            _operationFingerprints.EnsureCapacity(checked(_operationFingerprints.Count + prepared.Count + 1));
            _transferReceipts.EnsureCapacity(checked(_transferReceipts.Count + prepared.Count));
            _transferBatchReceipts.EnsureCapacity(checked(_transferBatchReceipts.Count + 1));

            _operationIds.Add(operationId);
            _operationFingerprints.Add(operationId, batchFingerprint);
            foreach (var leg in prepared)
            {
                _operationIds.Add(leg.Receipt.OperationId);
                _operationFingerprints.Add(leg.Receipt.OperationId, leg.Fingerprint);
                _transferReceipts.Add(leg.Receipt.OperationId, leg.Receipt with { });
            }
            _transferBatchReceipts.Add(operationId, receiptCopy);
            _entries.AddRange(events);
            _seq = nextSeq;
            Trim();
            return true;
        }
    }

    public bool TryGetTransferBatchReceipt(
        string operationId,
        out ResourceLedgerTransferBatchReceipt? receipt)
    {
        lock (_gate)
        {
            if (_transferBatchReceipts.TryGetValue(operationId, out var stored))
            {
                receipt = stored.DeepCopy();
                return true;
            }
            receipt = null;
            return false;
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

    /// <summary>
    /// Read-only verification that an existing OperationId is bound to this exact normalized
    /// batch payload. Unlike RecordBatchOnce, this never creates an operation or appends events.
    /// Legacy tombstones without fingerprints intentionally cannot satisfy this check.
    /// </summary>
    internal bool HasRecordedBatch(
        string operationId, IEnumerable<ResourceLedgerMutation> mutations)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("账本操作 ID 不能为空。", nameof(operationId));
        ArgumentNullException.ThrowIfNull(mutations);
        var batch = mutations.Where(item => item.Amount > 0)
            .Select(NormalizeMutationIdentity).ToArray();
        var fingerprint = ComputeOperationFingerprint(batch, receipt: null);
        var schema8Fingerprint = ComputeSchema8OperationFingerprint(batch, receipt: null);
        var legacyFingerprint = ComputeLegacyOperationFingerprint(batch, receipt: null);
        lock (_gate)
        {
            if (!_operationIds.Contains(operationId) ||
                !_operationFingerprints.TryGetValue(operationId, out var recorded))
                return false;
            return string.Equals(recorded, fingerprint, StringComparison.Ordinal) ||
                string.Equals(recorded, schema8Fingerprint, StringComparison.Ordinal) ||
                string.Equals(recorded, legacyFingerprint, StringComparison.Ordinal);
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



/// <summary>
/// Versioned durable receipt for one composite resource-transfer transaction.
/// The batch ID and all leg receipts are one replay and recovery unit.
/// </summary>
public sealed class ResourceLedgerTransferBatchReceipt
{
    public const int CurrentSchemaVersion = 1;

    public ResourceLedgerTransferBatchReceipt(
        string operationId,
        IReadOnlyList<ResourceTransferReceipt> legReceipts,
        int schemaVersion = CurrentSchemaVersion)
    {
        OperationId = operationId;
        SchemaVersion = schemaVersion;
        ArgumentNullException.ThrowIfNull(legReceipts);
        LegReceipts = Array.AsReadOnly(legReceipts.Select(item => item is null
            ? throw new ArgumentException("Batch receipt cannot contain a null leg receipt.", nameof(legReceipts))
            : item with { }).ToArray());
    }

    public string OperationId { get; }
    public int SchemaVersion { get; }
    public IReadOnlyList<ResourceTransferReceipt> LegReceipts { get; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OperationId) ||
            !string.Equals(OperationId, OperationId.Trim(), StringComparison.Ordinal) ||
            SchemaVersion != CurrentSchemaVersion ||
            LegReceipts == null || LegReceipts.Count == 0 ||
            LegReceipts.Any(item => item == null) ||
            LegReceipts.Any(item => string.Equals(item.OperationId, OperationId, StringComparison.Ordinal)) ||
            LegReceipts.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count() != LegReceipts.Count)
            throw new DomainValidationException("Resource transfer batch receipt has an invalid identity, version or leg set.");

        foreach (var leg in LegReceipts)
        {
            leg.Validate();
            if (leg.Outcome is not (ResourceTransferOutcome.Transferred or ResourceTransferOutcome.PartiallyTransferred))
                throw new DomainValidationException("A committed transfer batch can contain only committed leg receipts.");
            if (leg.ResourceKind == RuleResourceKind.Jar &&
                (leg.RequestedAmount != 1 || leg.TransferredAmount != 1 ||
                 leg.RetainedAtSourceAmount != 0 || leg.DiscardedAmount != 0 ||
                 !int.TryParse(leg.ResourceIdentity, System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture, out var jarId) ||
                 jarId <= 0 ||
                 jarId.ToString(System.Globalization.CultureInfo.InvariantCulture) != leg.ResourceIdentity))
                throw new DomainValidationException("A Jar transfer batch leg must identify exactly one canonical Jar.");
        }

        var accountIds = LegReceipts
            .SelectMany(item => new[] { item.SourceAccount.AccountId, item.TargetAccount.AccountId })
            .ToArray();
        var worldScoped = accountIds.Select(id =>
            id.StartsWith("account:v2:", StringComparison.Ordinal)).Distinct().ToArray();
        if (worldScoped.Length > 1)
            throw new DomainValidationException("A resource transfer batch cannot mix World-scoped and legacy accounts.");
        if (worldScoped.Length == 1 && worldScoped[0])
        {
            var world = AccountIdentity.ParseV2(accountIds[0]).Owner.WorldInstanceId;
            if (accountIds.Skip(1).Any(id =>
                    AccountIdentity.ParseV2(id).Owner.WorldInstanceId != world))
                throw new DomainValidationException("A resource transfer batch cannot span multiple Worlds.");
        }
    }

    public ResourceLedgerTransferBatchReceipt DeepCopy() =>
        new(OperationId, LegReceipts.Select(item => item with { }).ToArray(), SchemaVersion);

    public bool HasSamePayload(ResourceLedgerTransferBatchReceipt other) =>
        other != null &&
        SchemaVersion == other.SchemaVersion &&
        string.Equals(OperationId, other.OperationId, StringComparison.Ordinal) &&
        LegReceipts.SequenceEqual(other.LegReceipts);
}

/// <summary>One physical transfer leg and the exact ledger facts it contributes to its parent batch.</summary>
public sealed record ResourceLedgerTransferBatchLeg(
    ResourceTransferReceipt Receipt,
    IReadOnlyList<ResourceLedgerMutation> Mutations);

/// <summary>
/// External finite-parser Jar import receipt. Unlike the bounded event stream, this receipt
/// is retained for the lifetime of the operation ID so an idempotent replay can return the
/// original per-shape stored/discarded outcome even after old resource events are trimmed.
/// </summary>
public sealed record ResourceLedgerProducedJarImportReceipt(
    int SchemaVersion,
    string OperationId,
    string LedgerOperationId,
    string PayloadFingerprint,
    IReadOnlyDictionary<string, int> RequestedJarCounts,
    IReadOnlyDictionary<string, int> StoredJarCounts,
    IReadOnlyDictionary<string, int> DiscardedJarCounts)
{
    public const int CurrentSchemaVersion = 1;

    public int SubmittedJarCount => RequestedJarCounts?.Values.Sum() ?? 0;
    public int StoredJarCount => StoredJarCounts?.Values.Sum() ?? 0;
    public int DiscardedJarCount => DiscardedJarCounts?.Values.Sum() ?? 0;

    public void Validate()
    {
        static bool IsSha256(string? value) =>
            value is { Length: 64 } && value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');

        if (SchemaVersion != CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(OperationId) || OperationId.Length > 128 ||
            OperationId.Contains('|') || string.IsNullOrWhiteSpace(PayloadFingerprint) ||
            !IsSha256(PayloadFingerprint) ||
            !string.Equals(LedgerOperationId,
                "space-produced-jar-import|" + OperationId + "|" + PayloadFingerprint,
                StringComparison.Ordinal) ||
            RequestedJarCounts == null || RequestedJarCounts.Count == 0 ||
            StoredJarCounts == null || DiscardedJarCounts == null ||
            RequestedJarCounts.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value <= 0) ||
            StoredJarCounts.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value <= 0 ||
                !RequestedJarCounts.ContainsKey(item.Key)) ||
            DiscardedJarCounts.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value <= 0 ||
                !RequestedJarCounts.ContainsKey(item.Key)))
            throw new DomainValidationException("外部 Jar 导入回执的身份、指纹或逐形状数量无效。");

        long submitted = 0;
        long stored = 0;
        long discarded = 0;
        foreach (var item in RequestedJarCounts)
        {
            submitted = checked(submitted + item.Value);
            var storedForShape = StoredJarCounts.GetValueOrDefault(item.Key);
            var discardedForShape = DiscardedJarCounts.GetValueOrDefault(item.Key);
            if (storedForShape + (long)discardedForShape != item.Value)
                throw new DomainValidationException("外部 Jar 导入回执逐形状存储数与丢弃数不守恒: " + item.Key);
            stored += storedForShape;
            discarded += discardedForShape;
        }
        if (submitted <= 0 || submitted > int.MaxValue || stored + discarded != submitted ||
            stored > int.MaxValue || discarded > int.MaxValue)
            throw new DomainValidationException("外部 Jar 导入回执总数量不守恒或超出可表示范围。");
    }

    public ResourceLedgerProducedJarImportReceipt DeepCopy() => new(
        SchemaVersion, OperationId, LedgerOperationId, PayloadFingerprint,
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            RequestedJarCounts.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)),
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            StoredJarCounts.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)),
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            DiscardedJarCounts.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)));

    public bool HasSamePayload(ResourceLedgerProducedJarImportReceipt other) =>
        other != null &&
        SchemaVersion == other.SchemaVersion &&
        string.Equals(OperationId, other.OperationId, StringComparison.Ordinal) &&
        string.Equals(LedgerOperationId, other.LedgerOperationId, StringComparison.Ordinal) &&
        string.Equals(PayloadFingerprint, other.PayloadFingerprint, StringComparison.Ordinal) &&
        RequestedJarCounts.OrderBy(item => item.Key, StringComparer.Ordinal)
            .SequenceEqual(other.RequestedJarCounts.OrderBy(item => item.Key, StringComparer.Ordinal)) &&
        StoredJarCounts.OrderBy(item => item.Key, StringComparer.Ordinal)
            .SequenceEqual(other.StoredJarCounts.OrderBy(item => item.Key, StringComparer.Ordinal)) &&
        DiscardedJarCounts.OrderBy(item => item.Key, StringComparer.Ordinal)
            .SequenceEqual(other.DiscardedJarCounts.OrderBy(item => item.Key, StringComparer.Ordinal));
}

/// <summary>资源账本纯值快照，包含增量读取所需的连续性元数据。</summary>
public sealed class ResourceLedgerSnapshot
{
    public ResourceLedgerSnapshot(
        int capacity, long lastSeq, int dropped, IEnumerable<ResourceEvent> entries,
        IEnumerable<string>? operationIds = null,
        IEnumerable<ResourceTransferReceipt>? transferReceipts = null,
        IReadOnlyDictionary<string, string>? operationFingerprints = null,
        IEnumerable<ResourceLedgerTransferBatchReceipt>? transferBatchReceipts = null,
        IEnumerable<ResourceLedgerProducedJarImportReceipt>? producedJarImportReceipts = null)
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
            OperationId = item.OperationId,
            BatchOperationId = item.BatchOperationId
        }).ToArray());
        OperationIds = Array.AsReadOnly((operationIds ?? Array.Empty<string>())
            .OrderBy(item => item, StringComparer.Ordinal).ToArray());
        TransferReceipts = Array.AsReadOnly((transferReceipts ?? Array.Empty<ResourceTransferReceipt>())
            .OrderBy(item => item.OperationId, StringComparer.Ordinal)
            .Select(item => item with { }).ToArray());
        OperationFingerprints = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            (operationFingerprints ?? new Dictionary<string, string>())
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
        TransferBatchReceipts = Array.AsReadOnly((transferBatchReceipts ?? Array.Empty<ResourceLedgerTransferBatchReceipt>())
            .OrderBy(item => item.OperationId, StringComparer.Ordinal)
            .Select(item => item.DeepCopy()).ToArray());
        ProducedJarImportReceipts = Array.AsReadOnly((producedJarImportReceipts ?? Array.Empty<ResourceLedgerProducedJarImportReceipt>())
            .OrderBy(item => item.LedgerOperationId, StringComparer.Ordinal)
            .Select(item => item.DeepCopy()).ToArray());
    }

    public int Capacity { get; }
    public long LastSeq { get; }
    public int Dropped { get; }
    public IReadOnlyList<ResourceEvent> Entries { get; }
    public IReadOnlyList<string> OperationIds { get; }
    public IReadOnlyList<ResourceTransferReceipt> TransferReceipts { get; }
    public IReadOnlyList<ResourceLedgerTransferBatchReceipt> TransferBatchReceipts { get; }
    public IReadOnlyList<ResourceLedgerProducedJarImportReceipt> ProducedJarImportReceipts { get; }
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

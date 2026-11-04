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
/// <item><c>flow</c> 中央仓库 → 主台的单向流入</item>
/// <item><c>transfer</c> 主台 → 从台的转移（B 类订单的"挑罐过去"）</item>
/// <item><c>return</c> 从台 → 主台的归还（交付后剩下的工具罐/游离液体）</item>
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

    private readonly List<ResourceEvent> _entries = new();
    private long _seq;

    public ResourceLedger(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new DomainValidationException("资源账本容量必须是正整数: " + capacity);
        Capacity = capacity;
    }

    /// <summary>容量（条）。</summary>
    public int Capacity { get; }

    /// <summary>因容量不足已丢弃的条数（单调递增）。</summary>
    public int Dropped { get; private set; }

    /// <summary>当前保留的条数（≤ <see cref="Capacity"/>）。</summary>
    public int Retained => _entries.Count;

    /// <summary>最后一条的序号；空账本为 0。读者用它判断"我是不是漏看了"。</summary>
    public long LastSeq => _seq;

    /// <summary>保留的事件（按发生顺序，最旧在前）。只读视图。</summary>
    public IReadOnlyList<ResourceEvent> Entries => _entries;

    /// <summary>
    /// 记一条事实。<paramref name="amount"/> ≤ 0 时**不记**（"没变化"不是事实），返回 null。
    /// 不校验 cause 是否在 <see cref="ResourceCauses.All"/> 里（调用方是内核自己），
    /// 但测试会钉住"内核只产出已登记的原因"。
    /// </summary>
    public ResourceEvent? Record(int tick, string cause, string kind, string name, int amount,
        string? from = null, string? to = null, string? reason = null)
    {
        if (amount <= 0) return null;

        var e = new ResourceEvent
        {
            Seq = ++_seq,
            Tick = tick,
            Cause = cause,
            Kind = kind,
            Name = name,
            Amount = amount,
            From = from,
            To = to,
            Reason = reason
        };
        _entries.Add(e);
        Trim();
        return e;
    }

    /// <summary>清空保留的事件（序号不回绕，于是"清过"也能从序号跳变看出）。</summary>
    public void Clear() => _entries.Clear();

    /// <summary>按原因筛选（如"这回合的产出都有哪些"）。顺序保持发生顺序。</summary>
    public IReadOnlyList<ResourceEvent> For(string cause) =>
        _entries.Where(e => e.Cause == cause).ToList();

    /// <summary>从 <paramref name="afterSeq"/> 之后的事件（供增量读、断线重连补差）。</summary>
    public IReadOnlyList<ResourceEvent> Since(long afterSeq) =>
        _entries.Where(e => e.Seq > afterSeq).ToList();

    private void Trim()
    {
        if (_entries.Count <= Capacity) return;
        var drop = Math.Min(Math.Max(1, Capacity / 2), _entries.Count);
        _entries.RemoveRange(0, drop);
        Dropped += drop;
    }
}

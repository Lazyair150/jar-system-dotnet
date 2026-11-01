namespace JarSystem.Core.Presentation;

/// <summary>
/// **表现层帧的类别** —— 一个**封闭**的词表。
///
/// 为什么是封闭的：表现层最容易腐坏的方式，不是画错，而是"每加一个功能就多一种消息"。
/// 消息种类一旦开成字符串，宿主与核心之间就没有契约，谁也说不清"该来的都来了没有"。
/// 这里是枚举：新增一类表现，必须在核心加一个值，并同步 <c>PresentationFeedTests</c>
/// 与 <c>docs/表现层框架.md</c> 的对照表 —— 一次有意识的、可审的改动。
///
/// 粒度刻意与 <c>WorldSignalKind</c> 一致（它是本层的上游），**不细分**到
/// "某只罐子被灌注"这种程度：那样核心就得知道表现层的兴趣点，规则一改就得跟着改。
/// 需要细节的宿主在收到帧之后回查世界（<c>getState</c> / <c>WorldQuery</c>）。
/// </summary>
public enum PresentationKind
{
    /// <summary>一次意图级的世界变更已落账（工作台、订单、产线、库存……）。</summary>
    WorldChanged,

    /// <summary>某个过程的状态变了（开始 / 推进 / 阻塞 / 恢复 / 结束）。</summary>
    ProcessChanged,

    /// <summary>逻辑时间被冻住。</summary>
    TimePaused,

    /// <summary>逻辑时间恢复流动。</summary>
    TimeResumed,
}

/// <summary>
/// **一帧** —— 表现层能拿到的最小、也是唯一的时间单位。
///
/// 它**只有引用，没有载荷**：类别、操作名、主体、逻辑 tick、世界版本号。
/// 这与 <c>WorldSignal</c> 同一条理由 —— 帧里一旦带上"世界现在是什么样"，
/// 帧就成了一份状态副本，而副本一定会与世界漂移（谁在什么时候刷新它？）。
/// 所以帧只回答"**什么时候、哪一类、关于谁**变了"，其余由宿主回查。
///
/// <see cref="Seq"/> 是这一层自己的单调序号（与 <c>ProcessJournal</c> 的 seq 分离：
/// 两者粒度不同，混用会让"缺了第几条"变得无法回答）。
/// </summary>
public readonly struct PresentationFrame
{
    public PresentationFrame(int seq, PresentationKind kind, string op, string? subject,
        int? subjectId, int tick, int revision)
    {
        Seq = seq;
        Kind = kind;
        Op = op;
        Subject = subject;
        SubjectId = subjectId;
        Tick = tick;
        Revision = revision;
    }

    /// <summary>单调递增的帧号（从 1 开始；被裁剪后仍不回退，缺口见 <see cref="PresentationFeed.IsTruncated"/>）。</summary>
    public int Seq { get; }

    /// <summary>帧的类别。</summary>
    public PresentationKind Kind { get; }

    /// <summary>操作名 / 原因名（例如 <c>addOrder</c>、<c>progressed</c>）。</summary>
    public string Op { get; }

    /// <summary>主体名（工作台 id、订单号……），无主体时为 null。</summary>
    public string? Subject { get; }

    /// <summary>主体编号，无编号时为 null。</summary>
    public int? SubjectId { get; }

    /// <summary>发出该帧的逻辑 tick **（唯一的时间轴；墙钟不得出现在这里）**。</summary>
    public int Tick { get; }

    /// <summary>发出该帧时的世界版本号（<c>JarSystem.WorldRevision</c>）。</summary>
    public int Revision { get; }

    public override string ToString() =>
        $"#{Seq} t{Tick} r{Revision} {Kind}:{Op}" + (Subject is null ? "" : $"@{Subject}")
        + (SubjectId is null ? "" : $"#{SubjectId}");
}

/// <summary>
/// 表现层的接收方（宿主实现：<c>JarSystem.Web</c>、Godot 宿主、离线录制器……）。
///
/// 纪律与 <c>IWorldObserver</c> 相同，理由也相同：
/// <list type="bullet">
/// <item>**不得抛异常** —— 表现层出错不能影响世界推进；</item>
/// <item>**不得反向改世界** —— 想改世界走命令入口，那条路有日志、可回放；</item>
/// <item>**不得阻塞** —— 帧是同步派发的，在这里睡觉等于让世界等显示器。</item>
/// </list>
/// 需要"慢放"的宿主请自己缓存帧、按自己的节奏消费（见 <see cref="PresentationFeed.Since"/>）。
/// </summary>
public interface IPresentationSink
{
    void OnFrame(in PresentationFrame frame);
}

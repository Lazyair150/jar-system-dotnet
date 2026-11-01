namespace JarSystem.Core.World;

/// <summary>
/// 世界信号的类别。
///
/// 刻意保持**粗粒度**：信号描述的是"哪一类变化发生了"，不是"世界现在是什么样"。
/// 订阅方拿到信号后自己回查世界（经 <c>WorldQuery</c>），而不是相信信号里带的载荷 ——
/// 这样信号就不可能成为第二份状态，也就不会与世界漂移。
/// </summary>
public enum WorldSignalKind
{
    /// <summary>一次意图级的世界变更（工作台、订单、产线、库存……）已落到命令日志。</summary>
    WorldChanged,

    /// <summary>一个过程的状态变了（started / progressed / blocked / resumed / finished）。</summary>
    ProcessChanged,

    /// <summary>时间被冻住（<see cref="WorldTimeState.Paused"/>）：此后推进请求不生效，tick 不再增长。</summary>
    TimePaused,

    /// <summary>时间恢复流动：下一个推进请求重新生效。</summary>
    TimeResumed,
}

/// <summary>
/// 一条世界信号。**值类型且不可变**：它会被同步分发给每个订阅方，任何一方都不该能改写它。
///
/// <see cref="Tick"/> 与 <see cref="Revision"/> 是硬要求，不是装饰：
/// 没有它们，订阅方无法判断"这条信号是不是我已经处理过的那条"，
/// 也就无法在回放里复现自己的决策。
/// </summary>
public readonly struct WorldSignal
{
    public WorldSignal(WorldSignalKind kind, string op, string? subject, int? subjectId, int tick, int revision)
    {
        Kind = kind;
        Op = op;
        Subject = subject;
        SubjectId = subjectId;
        Tick = tick;
        Revision = revision;
    }

    public WorldSignalKind Kind { get; }

    /// <summary>意图名 / 原因名（例如 <c>deliverOrder</c>、<c>started</c>）。</summary>
    public string Op { get; }

    /// <summary>主体名（例如工作台 id、订单号），无主体时为 null。</summary>
    public string? Subject { get; }

    public int? SubjectId { get; }

    /// <summary>发出该信号的逻辑 tick。</summary>
    public int Tick { get; }

    /// <summary>发出该信号时的世界版本（即 <c>JarSystem.WorldRevision</c>）。</summary>
    public int Revision { get; }

    public override string ToString() =>
        Kind + ":" + Op + (Subject == null ? "" : "@" + Subject)
        + (SubjectId.HasValue ? "#" + SubjectId.Value : "")
        + " (tick " + Tick + ", rev " + Revision + ")";
}

using JarSystem.Core.Presentation;

/// <summary>
/// **表现层帧的推送器（Web 壳）** —— S1：把核心的 <see cref="PresentationFeed"/> 投影成一条
/// <c>frame</c> 消息，取代原先由 <c>ProcessObserver</c> 手工拼的 5 条 <c>process*</c> 消息。
///
/// 为什么换：那 5 条消息是**宿主自己翻译**出来的词汇表（started/progressed/blocked/…），
/// 于是同一件事在 Web 与 demo 各有一套说法。核心已有正式的帧流（`Presentation/PresentationFrame`
/// 的 seq/kind/op/subject/tick/revision + `PresentationFeed` 的 `Since` 拉模型），
/// 宿主该做的只是**转发**，不是**再定义**一遍。
///
/// 帧里**只有引用**（谁、哪一类、什么操作、第几刻、哪一版），没有载荷：明细由客户端回查
/// `getState`（`processes` / `workshops` / `orders`）。这与 `PresentationFrame` 的文档注释同一条理由 ——
/// 帧一旦带上世界快照就成了一份副本，而副本一定会与世界漂移。
///
/// 发送刻意"发射即忘"（`_ = BroadcastAsync(...)`）：表现层发送失败不得影响世界推进
/// （`IPresentationSink` 的约定本来就要求实现方不得抛异常、不得改世界、不得阻塞）。
/// </summary>
public sealed class PresentationObserver : IPresentationSink
{
    private readonly IWebSocketSink _sink;

    public PresentationObserver(IWebSocketSink sink) => _sink = sink;

    /// <inheritdoc />
    public void OnFrame(in PresentationFrame frame)
    {
        _ = _sink.BroadcastAsync(JsonArgs.J(new
        {
            type = "frame",
            data = new
            {
                seq = frame.Seq,
                kind = KindName(frame.Kind),
                op = frame.Op,
                subject = frame.Subject,
                subjectId = frame.SubjectId,
                tick = frame.Tick,
                revision = frame.Revision
            }
        }));
    }

    /// <summary>
    /// 类别名用**穷举 switch 表达式**，兜底分支**抛异常**（不静默返回枚举名、也不给兜底值）：
    /// 核心新增一类帧时，这里第一次收到就**当场炸**，而不是让客户端安静地少看到一类表现。
    /// 与 <c>BusPresentationBridge.KindOf</c> 同一纪律 —— 那一层由编译器强制（CS8524 逼你写兜底），
    /// 这一层的兜底是抛异常，效果一样：**新增一类表现必须是一次有意识的改动**。
    /// </summary>
    private static string KindName(PresentationKind kind) => kind switch
    {
        PresentationKind.WorldChanged => "worldChanged",
        PresentationKind.ProcessChanged => "processChanged",
        PresentationKind.TimePaused => "timePaused",
        PresentationKind.TimeResumed => "timeResumed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "核心新增了一类帧，但 Web 壳还没有对应的类别名（同步 KindName 与协议基线）。")
    };
}

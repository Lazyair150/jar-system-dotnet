using JarSystem.Core.Presentation;

namespace JarSystem.Core.World;

/// <summary>
/// **总线 → 帧流** 的唯一桥。世界信号在这里被投影成表现层帧。
///
/// 为什么单独一个类，而不是让 <see cref="PresentationFeed"/> 自己去订阅总线：
/// 那会让 <c>Presentation</c> 反过来依赖 <c>World</c>，两个圈层就成环了
/// （层序由 <c>ArchitectureTests</c> 守住：<c>Presentation</c> 必须低于 <c>World</c>）。
/// 桥住在 <c>World</c> 这一侧，恰好表达真实方向：**是世界的信号被投影成帧，不是帧去拉世界**。
///
/// 它只做一件事：把信号原样翻译成帧（类别映射 + 四个引用字段），
/// **不加字段、不查询世界、不排队**。任何"顺手补点信息"的念头都必须拒绝 ——
/// 一旦这里开始回查世界，帧就带上了"当时的快照"，而快照会过期，
/// 消费者就再也不知道自己看到的是哪一刻（这正是 <c>WorldSignal</c> 刻意不带载荷的原因）。
/// </summary>
public sealed class BusPresentationBridge : IWorldObserver
{
    private readonly PresentationFeed _feed;

    public BusPresentationBridge(PresentationFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        _feed = feed;
    }

    public void OnSignal(in WorldSignal signal)
    {
        _feed.Append(KindOf(signal.Kind), signal.Op, signal.Subject, signal.SubjectId,
            signal.Tick, signal.Revision);
    }

    /// <summary>
    /// 信号类别 → 帧类别。**穷举 switch**（不给 default 兜底）：
    /// 以后有人给 <c>WorldSignalKind</c> 加一个值而忘了这里，编译期就报"未覆盖所有取值"，
    /// 而不是让那一类表现悄悄消失。
    /// </summary>
    private static PresentationKind KindOf(WorldSignalKind kind) => kind switch
    {
        WorldSignalKind.WorldChanged => PresentationKind.WorldChanged,
        WorldSignalKind.ProcessChanged => PresentationKind.ProcessChanged,
        WorldSignalKind.TimePaused => PresentationKind.TimePaused,
        WorldSignalKind.TimeResumed => PresentationKind.TimeResumed,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "总线新增了一个信号类别，但表现层还没有对应的帧类别（见 PresentationKind）。"),
    };
}

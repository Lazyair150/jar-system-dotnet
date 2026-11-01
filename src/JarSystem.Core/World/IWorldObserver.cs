namespace JarSystem.Core.World;

/// <summary>
/// 世界观察者：**只读**地得知世界变了。
///
/// 约定（与 <c>IProcessObserver</c> 同一套纪律，理由也一样）：
/// <list type="bullet">
/// <item>**不得抛异常** —— 观察者出错不能影响世界推进；</item>
/// <item>**不得反向调用 <c>JarSystem</c>** 去改世界 —— 想改世界请实现 <see cref="IWorldReactor"/>，
///       那条路的副作用会被总线排进下一个 tick，可复现；</item>
/// <item>信号是粗粒度的，需要具体数据请自己回查（见 <see cref="WorldSignalKind"/> 的说明）。</item>
/// <item>**信号只在分发期间有效，不得转存**（D2 定案）。它是 <c>readonly struct</c>，存下来不会报错，
///       但存下来的 <c>Tick</c>/<c>Revision</c> 会**立刻变旧** —— 等它被用上时世界已经往前走了。
///       需要延后使用请只复制那两个整数（<c>int</c>），并在用之前和当前的 <c>JarSystem.WorldRevision</c>
///       比一次；不要持有整个信号，也不要指望它"记得"当时的世界。</item>
/// </list>
/// </summary>
public interface IWorldObserver
{
    void OnSignal(in WorldSignal signal);
}

/// <summary>
/// 世界反应器：**允许因为一条信号而请求"后续世界变化"**。
///
/// 这是"一个操作引发一系列世界变化"的落点，也是本设计唯一需要小心的地方：
/// 反应器请求的效果**不会立刻执行**，而是排进队列，由过程管线在**下一个 tick 的第 ① 步**统一执行
/// （见 <c>ProcessSystem.DrainEvents</c>）。这条延迟换来了三件事：
/// <list type="number">
/// <item>**可复现**：级联不是同一调用栈里的递归，而是"这一 tick 排的队，下一 tick 收的果"，
///       顺序由队列决定，与墙钟、线程、哈希序无关；</item>
/// <item>**有界**：每个 tick 只排一次队，不存在"同步级联到不收敛"这种死法；</item>
/// <item>**可审计**：每一条被排进队的效果都带名字与说明，过程日志与信号流能对上。</item>
/// </list>
/// 反应器允许在 <see cref="React"/> 里继续发信号（经 <c>WorldBus.Publish</c>），
/// 但它请求的**效果**一律进下一 tick 的批，不会当场执行。
/// </summary>
public interface IWorldReactor
{
    /// <param name="signal">刚发生的世界信号。</param>
    /// <param name="sink">把"我想要的后续世界变化"交给它；不想要就别调。</param>
    void React(in WorldSignal signal, IWorldEffectSink sink);
}

/// <summary>反应器请求后续世界变化的出口。实现方负责排队与边界，反应器只管表达意图。</summary>
public interface IWorldEffectSink
{
    /// <param name="name">效果名（会出现在诊断与日志里，例如 <c>recheckOrder</c>）。</param>
    /// <param name="description">人类可读说明。</param>
    /// <param name="effect">真正改世界的动作。**必须只依赖世界状态，不得依赖墙钟或随机数。**</param>
    /// <returns>是否被接受（队列满、或该 tick 的配额用尽时返回 false）。</returns>
    bool Enqueue(string name, string description, System.Action effect);
}

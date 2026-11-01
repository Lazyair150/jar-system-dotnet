namespace JarSystem.Core.Process;

/// <summary>
/// **tick 的阶段**。一个 tick 内按此顺序推进，顺序**写死**、不可配置。
///
/// <para>
/// 为什么阶段数固定而不是"随便谁注册随便跑"：确定性要求"同一状态 + 同一输入 ⇒ 同一结果"。
/// 只要允许两个模块在同一 tick 内的先后不定，回放就会出现两种结果 —— 这是整套系统唯一的硬资产。
/// 所以引擎那套"遍历所有模块调 process"在这里只能学一半：**能注册，但阶段和顺序必须被钉死**。
/// </para>
///
/// <para>
/// <see cref="Flow"/> 是**例外**：它不在"每 tick"里，而在"每次宿主 Tick 调用"的末尾跑一次。
/// 这个区别是既有语义（<c>ProductionService</c> 的类注释原文：
/// 「每个 tick 一次 TickStep、每次宿主 Tick 调用一次 FlushFlow」），
/// 所以它有自己的入口 <see cref="WorldScheduler.RunCallBoundary"/>，不与前四个阶段混在一条循环里。
/// </para>
/// </summary>
public enum TickPhase
{
    /// <summary>① 事件注入：世界事件**只改世界**（改资源 / 往修饰器栈加临时规则），不改过程状态。</summary>
    Events = 0,

    /// <summary>② 修饰器更新：区间到期的修饰器在这里自动摘掉（"临时禁令到期自动解除"）。</summary>
    Modifiers = 1,

    /// <summary>③ 过程推进：每个过程**至多一个单位**（步间隔未到的过程这一 tick 不推进）。</summary>
    Processes = 2,

    /// <summary>④ 世界产出：生产者产罐、液体产出（每个 tick 一次）。</summary>
    Production = 3,

    /// <summary>⑤ 向主台流入（**每次宿主 Tick 调用一次**，不是每 tick）。只由 <see cref="WorldScheduler.RunCallBoundary"/> 执行。</summary>
    Flow = 4
}

/// <summary>
/// 一次 tick 的上下文。**刻意只有一个字段**。
///
/// <para>
/// 没有 <c>dt</c>：我们的 <c>process()</c> 参数恒为"一步"，不是"一段时间"。
/// 引入 <c>dt</c> 会把定点累加（<c>ProducerModule</c> 的 <c>_accumulatedMilli</c>，
/// 起因是实测到 <c>0.20000000000000018</c> 导致 <c>Math.Floor</c> 少产一个罐）的成果全部作废。
/// </para>
/// <para>
/// 没有持有 <c>JarSystem</c>：处理器**不得反向调用世界**。要改世界必须经 <c>WorldBus</c> 排进下一个 tick 的
/// <see cref="TickPhase.Events"/> 阶段（通道已经存在，见 <c>ProcessSystem.EventSource</c>）。
/// </para>
/// </summary>
public readonly struct TickContext
{
    public TickContext(int tick)
    {
        Tick = tick;
    }

    /// <summary>本 tick 的编号（= 逻辑时钟读数，从 1 开始）。</summary>
    public int Tick { get; }
}

/// <summary>tick 处理器：需要"时间驱动"的模块实现它并注册进 <see cref="WorldScheduler"/>。</summary>
public interface ITickHandler
{
    /// <summary>在哪个阶段被调用（决定它与其他模块的相对次序）。</summary>
    TickPhase Phase { get; }

    /// <summary>同阶段内的次序，**必须唯一**；冲突在注册时直接报错，不静默按注册顺序。</summary>
    int Order { get; }

    /// <summary>处理器名（报错与诊断用）。</summary>
    string Name { get; }

    /// <summary>被调用一次（= 世界走了一步）。不得读墙钟、不得起线程、不得改世界。</summary>
    void OnTick(in TickContext context);
}

/// <summary>把裸委托包成 <see cref="ITickHandler"/>：既有模块不必为了接入而改成新类。</summary>
public delegate void TickHandlerCallback(in TickContext context);

/// <summary>基于委托的 tick 处理器。</summary>
public sealed class DelegateTickHandler : ITickHandler
{
    private readonly TickHandlerCallback _callback;

    public DelegateTickHandler(TickPhase phase, int order, string name, TickHandlerCallback callback)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("处理器必须有名字（报错与诊断要用）", nameof(name));
        Phase = phase;
        Order = order;
        Name = name;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public TickPhase Phase { get; }

    public int Order { get; }

    public string Name { get; }

    public void OnTick(in TickContext context) => _callback(context);
}

using JarSystem.Core.Domain;
using JarSystem.Core.Rules;
namespace JarSystem.Core.Process;

/// <summary>
/// **tick 管线**：一个 tick 内**固定顺序**的处理步骤，是"可中断 + 确定性"同时成立的来源。
///
/// <code>
/// 一个 tick（由宿主的世界推进驱动；全程在同一串行闸门内）：
///   0 逻辑时钟 +1                    ← 本 tick 编号（过程步记的就是它）
///   ① 事件注入                       ← 世界事件（事故 / 任命 / 禁令生效）
///   ② 修饰器更新                     ← 区间生效/失效 + 冲突解析
///   ③ 过程推进（每过程**至多一步**）
///   ④ 世界产出                       ← 既有 ProducerModule
/// （每次宿主 Tick 调用末尾再跑一次）
///   ⑤ 向主台流入
/// </code>
///
/// 为什么把顺序写死：确定性要求"同一状态 + 同一输入 ⇒ 同一结果"，
/// 而只要允许"事件与过程推进在同一 tick 内次序不定"，回放就会出现两种结果。
/// 顺序一旦写死，回放（既有的 <c>--check-replay</c>）就能把过程日志一并比对。
///
/// <para>
/// **本版起，这五个步骤是注册进 <see cref="Scheduler"/> 的处理器，不再是一串硬编码调用。**
/// 对外行为逐字未变（顺序、参数、时机都不变），变的是"新增一个时间驱动模块"的成本：
/// 从"改本类和 <c>JarSystem.Tick</c>"降到"注册一个 <see cref="ITickHandler"/>"。
/// </para>
/// </summary>
public sealed class TickPipeline
{
    private readonly LogicalClock _clock;

    internal TickPipeline(LogicalClock clock, ModifierStack modifiers, Action drainEvents,
        Action advanceActiveProcesses)
    {
        _clock = clock;

        // 三步都以 Order 0 注册在同一阶段里 —— 与改造前逐个直接调用等价，只是换了个调用外壳。
        // 保留这个外壳的价值：新模块不必再插进本类，注册即可；而阶段名让"这段代码属于心跳的哪一拍"显式化。
        Scheduler = new WorldScheduler();
        Scheduler.Register(new DelegateTickHandler(TickPhase.Events, 0, "events",
            (in TickContext _) => drainEvents()));
        Scheduler.Register(new DelegateTickHandler(TickPhase.Modifiers, 0, "modifiers",
            (in TickContext ctx) => modifiers.Expire(ctx.Tick)));
        Scheduler.Register(new DelegateTickHandler(TickPhase.Processes, 0, "processes",
            (in TickContext _) => advanceActiveProcesses()));
    }

    /// <summary>tick 阶段调度器（新增时间驱动模块从这里 <see cref="WorldScheduler.Register"/> 接入）。</summary>
    public WorldScheduler Scheduler { get; }

    /// <summary>当前 tick（= 逻辑时钟读数）。</summary>
    public int Tick => _clock.Tick;

    /// <summary>推进一个 tick：先时钟 +1，再按阶段跑 ① 事件 ② 修饰器 ③ 过程 ④ 产出。</summary>
    public void AdvanceOneTick()
    {
        _clock.Advance();
        Scheduler.RunTick(_clock.Tick);
    }

    /// <summary>跑一次"调用边界"阶段（⑤ 流入）：**每次宿主 Tick 调用一次**，不是每 tick。</summary>
    public void RunCallBoundary() => Scheduler.RunCallBoundary(_clock.Tick);
}

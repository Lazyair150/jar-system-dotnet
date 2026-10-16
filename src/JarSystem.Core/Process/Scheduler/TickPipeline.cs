using JarSystem.Core.Rules;
namespace JarSystem.Core.Process;

/// <summary>
/// **tick 管线**：一个 tick 内**固定顺序**的处理步骤，是"可中断 + 确定性"同时成立的来源。
///
/// <code>
/// 一个 tick（由宿主的世界推进驱动；全程在同一串行闸门内）：
///   0 逻辑时钟 +1                    ← 本 tick 编号（过程步记的就是它）
///   ① 事件注入                       ← P3：世界事件（事故 / 任命 / 禁令生效）
///   ② 修饰器更新                     ← P3：区间生效/失效 + 冲突解析
///   ③ 过程推进（每过程**至多一步**）   ← P2b 起真正使用；P2 的同步路径没有在册过程
///   ④ 世界产出                       ← 既有 ProducerModule（在 ProductionService 侧，紧随本调用）
/// </code>
///
/// 为什么把顺序写死：确定性要求"同一状态 + 同一输入 ⇒ 同一结果"，
/// 而只要允许"事件与过程推进在同一 tick 内次序不定"，回放就会出现两种结果。
/// 顺序一旦写死，回放（既有的 <c>--check-replay</c>）就能把过程日志一并比对。
/// </summary>
public sealed class TickPipeline
{
    private readonly LogicalClock _clock;
    private readonly ModifierStack _modifiers;
    private readonly Action _drainEvents;
    private readonly Action _advanceActiveProcesses;

    internal TickPipeline(LogicalClock clock, ModifierStack modifiers, Action drainEvents,
        Action advanceActiveProcesses)
    {
        _clock = clock;
        _modifiers = modifiers;
        _drainEvents = drainEvents;
        _advanceActiveProcesses = advanceActiveProcesses;
    }

    /// <summary>当前 tick（= 逻辑时钟读数）。</summary>
    public int Tick => _clock.Tick;

    /// <summary>推进一个 tick：① 事件 ② 修饰器 ③ 过程（每过程至多一步）。④ 世界产出由调用方紧随其后完成。</summary>
    public void AdvanceOneTick()
    {
        _clock.Advance();

        // ① 事件注入：事件**只改世界**（改资源 / 往修饰器栈加临时规则），不改过程状态。
        _drainEvents();
        // ② 修饰器更新：区间到期的修饰器在这里自动摘掉（"临时禁令到期自动解除"）。
        _modifiers.Expire(_clock.Tick);

        // ③ 过程推进：每个过程**至多一个单位**（步间隔未到的过程这一 tick 不推进）。
        //    实现由 ProcessSystem 提供（它持有在册过程与它们的工作体）；
        //    同步轨（drain）不走这里 —— 它在一次调用里把单位跑完（既有 executeOrder 的语义）。
        _advanceActiveProcesses();
    }
}

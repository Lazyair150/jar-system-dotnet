namespace JarSystem.Core.Process;

/// <summary>
/// **逻辑时钟**：世界推进的唯一权威（判定层只认它，绝不读墙钟）。
///
/// 语义：
/// <list type="bullet">
/// <item>整数、只增；每一次"世界步进"（Host 的 <c>Tick</c> / <c>manualTick</c> / 后台推进循环）加一；</item>
/// <item>编号从 **1** 开始：0 表示"世界尚未开始推进"（构造后、任何 Tick 之前）；</item>
/// <item>过程日志里的 <c>tick</c> 就是它 —— 于是"这一步发生在世界的哪一刻"可查、可回放。</item>
/// </list>
///
/// 为什么单独成类而不是用一个 int 字段：时钟是过程系统的**时间来源**，
/// 必须能被注入替身（例如将来做"加速/暂停"的宿主层实验），也必须只有一个推进口。
/// </summary>
public sealed class LogicalClock
{
    /// <summary>当前 tick（0 = 世界尚未开始推进）。</summary>
    public int Tick { get; private set; }

    /// <summary>推进一个 tick（唯一入口；只由 <see cref="TickPipeline"/> 调用）。</summary>
    internal void Advance() => Tick++;

    /// <summary>世界重建时归零（只由 <see cref="ProcessSystem.Clear"/> 调用）。</summary>
    internal void Reset() => Tick = 0;
}

namespace JarSystem.Core.Process;

/// <summary>
/// 过程系统的默认值（集中一处，便于将来按裁决统一调整）。
/// </summary>
public static class ProcessDefaults
{
    /// <summary>
    /// 阻断后的**宽限窗口**（刻）。语义：被阻断后，若在这么多刻内前置条件恢复 ⇒ 继续；否则失败。
    ///
    /// 为什么默认不是 0：0 表示"一被阻断立刻失败"，那就没有"等世界恢复"这回事了 ——
    /// 而玩家把主台选回来、把器物补回来都需要一点时间。默认取 4 刻（4 个完整回合的余量），
    /// 它是**可调**的：单次过程可以在 <see cref="ProcessDefinition"/> 里覆盖。
    /// </summary>
    public const int GraceTicks = 4;

    /// <summary>最近终结过程的保留条数（有界，避免长跑无界增长）。</summary>
    public const int RecentCapacity = 64;
}

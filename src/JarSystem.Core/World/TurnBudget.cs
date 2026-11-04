namespace JarSystem.Core.World;

/// <summary>
/// **回合预算**：这一局还剩几回合可以推进。
///
/// 规则属于谁：这条规则约束的是"世界还能不能往前走"，所以它是 <c>World</c> 的概念，
/// 不是页面的、也不是过程系统的。值来自 <see cref="Config.WorldOptions.TurnLimit"/>（0 = 不限），
/// 已用来自世界时钟 —— 这里只做**判据**，不持有状态、不推时间。
///
/// 判据是**整次推进**的：`Allows(3)` 在只剩 2 回合时返回 false，而不是"走 2 回合"。
/// 走一半的世界最糟：玩家以为推进了，实际上世界停在一个规则上不该到达的位置。
/// </summary>
public readonly struct TurnBudget
{
    public TurnBudget(int limit, int used)
    {
        Limit = limit;
        Used = used;
    }

    /// <summary>上限（0 = 不限）。</summary>
    public int Limit { get; }

    /// <summary>已用（世界时钟的 tick 数；一回合 = 一个 tick）。</summary>
    public int Used { get; }

    /// <summary>这一局有没有上限。</summary>
    public bool IsLimited => Limit > 0;

    /// <summary>回合已经用完 —— 本局到头了。</summary>
    public bool IsExhausted => IsLimited && Used >= Limit;

    /// <summary>还剩几回合（不限时给 <see cref="int.MaxValue"/>，调用方别拿它做算术）。</summary>
    public int Remaining => IsLimited ? Math.Max(0, Limit - Used) : int.MaxValue;

    /// <summary>这次推进 <paramref name="count"/> 回合能不能整体生效。</summary>
    public bool Allows(int count) => !IsLimited || (long)Used + Math.Max(0, count) <= Limit;

    /// <summary>不能推进时的理由（能推进时是空串）。理由文案只有这一处，界面直接显示。</summary>
    public string ReasonIfBlocked(int count) => Allows(count)
        ? string.Empty
        : $"回合已用完（上限 {Limit} 回合）：本关已经结束，无法继续推进";
}

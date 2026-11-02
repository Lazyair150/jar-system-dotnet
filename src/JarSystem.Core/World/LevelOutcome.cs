namespace JarSystem.Core.World;

/// <summary>这一局的结局。</summary>
public enum LevelOutcomeKind
{
    /// <summary>还在打。</summary>
    InProgress = 0,

    /// <summary>做成了目标单数。</summary>
    Won = 1,

    /// <summary>回合用尽还没做成 —— 本关失败。</summary>
    Lost = 2
}

/// <summary>
/// **一局的结局**：赢、输、还是没结束。
///
/// 规则属于谁：它约束的是"这一局结束没有"，所以是 World 的概念，
/// 与 <see cref="TurnBudget"/> 同源（同一把尺子：回合、目标单数都来自世界配置）。
///
/// 判据**只有一处**：<see cref="LevelGoal.Judge"/>。页面只显示结论，不自己算 ——
/// 界面算出来的"你赢了"和内核算出来的必须永远是同一句话。
/// </summary>
public readonly struct LevelOutcome
{
    private LevelOutcome(LevelOutcomeKind kind, string reason)
    {
        Kind = kind;
        Reason = reason;
    }

    public LevelOutcomeKind Kind { get; }

    /// <summary>世界语言的说明（页头直接显示；进行中时是空串）。</summary>
    public string Reason { get; }

    public bool IsOver => Kind != LevelOutcomeKind.InProgress;
    public bool IsWon => Kind == LevelOutcomeKind.Won;

    /// <summary>世界语言：`won` / `lost` / `inProgress`（契约里用的就是这三个词）。</summary>
    public string Name => Kind switch
    {
        LevelOutcomeKind.Won => "won",
        LevelOutcomeKind.Lost => "lost",
        _ => "inProgress"
    };

    internal static readonly LevelOutcome InProgress = new(LevelOutcomeKind.InProgress, "");
    internal static readonly LevelOutcome Won = new(LevelOutcomeKind.Won, "目标已完成：本关通过");
    internal static LevelOutcome Lost(string reason) => new(LevelOutcomeKind.Lost, reason);
}

/// <summary>
/// **判胜/判负的纯函数**：目标单数 + 已完成单数 + 回合预算 ⇒ 结局。
///
/// 三条顺序是有讲究的：
/// <list type="number">
/// <item>先看目标：已经做够了就是赢 —— 哪怕这一刻正好也是最后一回合（先赢后算回合用尽）；</item>
/// <item>再看回合：用尽且没做够 ⇒ 输，理由写明还差几单；</item>
/// <item>否则还在打。</item>
/// </list>
/// 反过来先判回合，就会出现"最后一回合刚好做完却判输"这种最伤人的判定。
/// </summary>
public static class LevelGoal
{
    /// <param name="goalOrders">目标单数（0 = 无目标，永不判胜）。</param>
    /// <param name="completedOrders">已经成功交付的单数。</param>
    /// <param name="turns">回合预算（只用它回答"还能不能打"）。</param>
    public static LevelOutcome Judge(int goalOrders, int completedOrders, TurnBudget turns)
    {
        if (goalOrders > 0 && completedOrders >= goalOrders)
            return LevelOutcome.Won;

        if (turns.IsExhausted)
            return LevelOutcome.Lost(
                goalOrders <= 0
                    ? $"回合已用完（上限 {turns.Limit} 回合）"
                    : $"回合已用完（上限 {turns.Limit} 回合），还差 {Math.Max(0, goalOrders - completedOrders)} 单");

        return LevelOutcome.InProgress;
    }
}

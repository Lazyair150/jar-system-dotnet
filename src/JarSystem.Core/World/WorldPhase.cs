namespace JarSystem.Core.World;

/// <summary>
/// **世界此刻处在哪个阶段**。存在的意义是：把"玩家下一步能做什么"从口语（"还没开局 / 手上有一单在跑 / 这关结束了"）
/// 收敛成一个内核判定的词，页面与宿主据此切换可用动作与措辞，而不是各自猜。
///
/// 三个阶段刻意只用"世界自己的事实"判定（见 <see cref="JarSystem.Core.JarSystem.Phase"/>）：
/// 判胜/判负的结果、有没有正在跑的订单过程。它**不**表达"玩家想做什么"——
/// 那属于界面状态，不属于世界。
/// </summary>
public enum WorldPhase
{
    /// <summary>还没有订单在执行：可以在产线卡上布置、分析订单、决定先做哪一单。</summary>
    Setup = 0,

    /// <summary>有订单正在执行：世界会按过程推进，但分析/新开单要等它收尾。</summary>
    Running = 1,

    /// <summary>这一局已有结果（达成目标单数，或回合用尽）：世界不再推进。</summary>
    Settled = 2,
}

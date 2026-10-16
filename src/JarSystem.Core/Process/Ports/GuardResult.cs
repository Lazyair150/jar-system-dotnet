namespace JarSystem.Core.Process;

/// <summary>守卫判定结果：可以推进 / 暂时不成立（阻断，可恢复）/ 永久不成立（终结失败）。</summary>
public enum GuardStatus
{
    /// <summary>前置条件成立，可以推进一个单位。</summary>
    Ok,

    /// <summary>前置条件**暂时**不成立（例：主台被临时取消选择、器物被暂时取走）⇒ 进 <c>Blocked</c>，等宽限窗口内恢复。</summary>
    Blocked,

    /// <summary>前置条件**永久**不成立（例：订单已被删除、工作台已被销毁）⇒ 直接失败，不等待。</summary>
    Fatal
}

/// <summary>
/// 守卫结果（含世界语言的原因）。
///
/// 为什么要区分 Blocked 与 Fatal：**"世界暂时变了"和"世界再也回不去了"是两种不同的事**。
/// 前者应当等待（玩家可能把主台选回来），后者应当立刻失败（等下去没有意义）。
/// 这条区分是设计文档 §4.2 里 <c>Blocked → Resumed / Failed</c> 那台状态机的输入。
/// </summary>
public readonly record struct GuardResult(GuardStatus Status, string? Reason)
{
    public static GuardResult Ok() => new(GuardStatus.Ok, null);
    public static GuardResult Blocked(string reason) => new(GuardStatus.Blocked, reason);
    public static GuardResult Fatal(string reason) => new(GuardStatus.Fatal, reason);
}

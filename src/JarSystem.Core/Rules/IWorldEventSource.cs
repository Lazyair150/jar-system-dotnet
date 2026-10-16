namespace JarSystem.Core.Rules;

/// <summary>
/// **世界事件源**：每个 tick 由管线在"过程推进之前"取一次事件并执行。
///
/// 边界（设计文档 §4.4 的边界在这里落地）：
/// <list type="bullet">
/// <item>事件**只改世界**（经既有意图方法改资源、或往修饰器栈里加临时规则）；</item>
/// <item>事件**不得直接改过程状态** —— 过程只读世界并据此自行迁移（该阻断就阻断）；</item>
/// <item>事件必须是**确定性**的（同一 tick + 同一世界 ⇒ 同一事件集），否则回放会漂；
/// 并且它是世界的**输入之一**：回放必须使用同一个事件源（与"同种子"同理）。</item>
/// </list>
/// </summary>
public interface IWorldEventSource
{
    /// <summary>取该 tick 要发生的事件（按顺序执行）。返回空表示这一 tick 无事件。</summary>
    IReadOnlyList<WorldEvent> DrainForTick(int tick);
}

/// <summary>一个世界事件：名字 + 说明 + 效果（效果由宿主以闭包给出，管线只负责在正确的时机调用它）。</summary>
public sealed class WorldEvent
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public Action? Effect { get; init; }
}

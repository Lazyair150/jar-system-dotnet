namespace JarSystem.Core.Process;

/// <summary>
/// **过程观察者**（宿主接它来驱动表现层：进度条、动画、日志、提示）。
///
/// 设计取舍（对 P0 规格 §4 的偏离，理由如实记录）：规格里写了五个具名回调
/// （OnStarted/OnProgressed/OnBlocked/OnResumed/OnFinished），实际落地收成**一个**
/// `OnChanged(instance, reason)`：
/// <list type="bullet">
/// <item>`totalSteps` 在过程建立时**还不知道**（计划随后才声明或按步推进），因此
/// "Started 一次性给全字段"这条路本来就走不通，宿主终究要在每次变化时读实例；</item>
/// <item>一个回调让**顺序**天然正确（观察者按发生顺序收到通知），而五个回调容易出现
/// "谁先谁后"的隐式约定；宿主按 reason 决定发哪条消息即可。</item>
/// </list>
/// 约定：实现方**不得抛异常**（观察者出错不能影响世界推进），也不得反向调用 <c>JarSystem</c>。
/// </summary>
public interface IProcessObserver
{
    /// <param name="instance">过程实例（**活视图**：读它的字段即为"此刻"的值）。</param>
    /// <param name="reason">取值见 <see cref="ProcessObserverReasons"/>。</param>
    /// <param name="tick">本次变化所在的逻辑 tick（宿主发消息要用；实例本身不记"现在"）。</param>
    void OnChanged(ProcessInstance instance, string reason, int tick);
}

/// <summary>观察者回调的 reason 取值（不是对外契约：对外消息名在 Web 层定义）。</summary>
public static class ProcessObserverReasons
{
    public const string Started = "started";
    public const string Progressed = "progressed";
    public const string Blocked = "blocked";
    public const string Resumed = "resumed";
    public const string Finished = "finished";
}

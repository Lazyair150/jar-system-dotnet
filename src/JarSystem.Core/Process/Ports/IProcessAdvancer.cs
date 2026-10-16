namespace JarSystem.Core.Process;

/// <summary>
/// **可被 tick 驱动的工作体**：一个"单位"= 一次可中断的推进（一个计划步骤，或一个兜底批次）。
///
/// 为什么要有这层：过程系统本身是通用的（它只管时间、状态机、日志），
/// 而"这次推进具体要干什么"属于业务 —— 由实现者提供。于是：
/// <list type="bullet">
/// <item>**同步轨（drain）**：宿主一次调用里把单位跑到完（既有 <c>executeOrder</c> 的语义）；</item>
/// <item>**异步轨**：每个到达间隔的 tick 推进一个单位（新增 <c>startOrder</c> 的语义）。</item>
/// </list>
/// 两条轨共用**同一个**实现，因此"计划怎么走、失败怎么算"只有一份定义（不会漂移）。
/// </summary>
public interface IProcessAdvancer
{
    /// <summary>
    /// 推进一个单位。返回 <c>true</c> 表示还有后续单位；返回 <c>false</c> 表示本次推进把工作做完（无论成败）。
    /// </summary>
    bool AdvanceOneUnit(Abstractions.IProcessRecorder recorder);

    /// <summary>工作是否成功（仅在 <see cref="AdvanceOneUnit"/> 返回过 <c>false</c> 之后有意义）。</summary>
    bool Succeeded { get; }

    /// <summary>失败原因（世界语言；成功时为 null）。</summary>
    string? FailureReason { get; }

    /// <summary>
    /// **守卫**：这一单位的前置条件现在成立吗？（异步轨在每个单位执行前、以及被阻断后的每个 tick 调用）
    ///
    /// 契约：只读检查，**不得改变世界**；原因必须是"世界语言"（可直接显示给玩家）。
    /// 同步轨（drain）不调用它 —— 那个轨道在一个调用里跑完，世界不会中途变化。
    /// </summary>
    GuardResult CheckGuard();

    /// <summary>
    /// 守卫判定为"永久不成立"或"宽限窗口耗尽"时，由过程系统调用：让工作体把自身标记为失败
    /// （这样随后的 <see cref="OnFinished"/> 才能按失败路径落账）。必须幂等。
    /// </summary>
    void MarkFailed(string reason);

    /// <summary>
    /// 工作结束后的**一次性收尾**（例：订单交付、事件历史落账）。
    /// 由过程系统在最后一个单位之后调用一次；实现必须**幂等**（重复调用不得重复收尾）。
    /// </summary>
    void OnFinished();
}

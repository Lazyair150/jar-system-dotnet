namespace JarSystem.Core.Abstractions;

/// <summary>
/// **过程记账出口**（阶段 P1 的依赖倒置缝）：内核把"我执行了哪一步"报告给过程系统，
/// 但内核（<c>Orders</c> / <c>Executor</c> / <c>Services</c>）**不认识 Process 包** —— 它只依赖本接口。
/// 实现方：<c>Process.ProcessRun</c>（把记账写进过程实例与过程日志 <c>ProcessJournal</c>）。
///
/// **为什么要有这层**（与 <see cref="ISystemLog"/> 同一动机）：过程系统要观察内核的执行过程，
/// 而"内核反向依赖过程系统"会让依赖图成环、也会让过程系统变成内核的运行前提。
/// 收成接口后，过程系统是**可插拔的观察者**：不接（null）时内核行为与今天逐字相同。
///
/// **实现方约定**：
/// <list type="bullet">
/// <item>不得因**外部原因**抛异常（IO/日志失败等）—— 记账失败不能影响订单执行的结果；</item>
/// <item>但**状态机非法**（例：过程已终结仍在推进）属于编程错误，允许抛，且应当立刻暴露；</item>
/// <item>不得反向调用 <c>JarSystem</c>（避免记账触发状态变更或递归）；</item>
/// <item>传 null 表示"本次执行不记账"（既有调用点与测试可直接不传）。</item>
/// </list>
/// </summary>
public interface IProcessRecorder
{
    /// <summary>
    /// 声明本次执行的**计划来源**与**声明步数**（0 表示未声明）。可多次调用，后者覆盖前者。
    /// 瞬时模式（P1）下声明步数只是提示；时间化（P2）后它会成为排程依据。
    /// </summary>
    void SetPlan(string source, int declaredSteps);

    /// <summary>
    /// 报告"已成功应用一步"。<paramref name="operation"/> 沿用**既有执行日志的词汇**
    /// （<c>ExecutionLogEntry.Action</c>：pourIn / reshape / reshape_full / pourOut / use_existing /
    /// direct_match 等），避免为同一件事发明第二套名字。
    /// </summary>
    void StepApplied(string operation, int? jarId = null, string? summary = null);

    /// <summary>
    /// 报告过程内的**阶段里程碑**（取值见 <see cref="ProcessMilestones"/>）。
    /// 生命周期类条目（started / succeeded / failed …）由过程系统自己写，内核不需要关心。
    /// </summary>
    void Milestone(string name, string? summary = null);
}

/// <summary>
/// 计划来源（内核侧声明；过程日志用它区分"这份计划从哪来"）。
/// **只进过程日志，不投影进 state** —— 玩家看到的是世界语言，不是实现细节。
/// </summary>
public static class PlanSources
{
    /// <summary>来自求解器路径（可行性分析认定的可行路径）。</summary>
    public const string Solver = "solver";

    /// <summary>来自贪心兜底（没有可用路径时）。</summary>
    public const string Greedy = "greedy";

    /// <summary>路径与贪心混用（声明时无法预知，运行期降级时以里程碑记录）。</summary>
    public const string Mixed = "mixed";

    /// <summary>来自手工/脚本编排（P5 的世界编辑器用）。</summary>
    public const string Manual = "manual";
}

/// <summary>
/// 内核可报告的**阶段里程碑名**（写进过程日志的 <c>operation</c> 字段，条目 <c>kind</c> 恒为 <c>phase</c>）。
/// 单独成类是为了让"内核侧词汇"与"过程日志的条目种类"分开：前者可以增删，后者是存档契约。
/// </summary>
public static class ProcessMilestones
{
    /// <summary>器物转移（一个批次）。</summary>
    public const string Transfer = "transfer";

    /// <summary>液体转移。</summary>
    public const string TransferLiquid = "transfer_liquid";

    /// <summary>路径执行失败后降级到贪心。</summary>
    public const string Fallback = "fallback";

    /// <summary>交付。</summary>
    public const string Delivery = "delivery";
}

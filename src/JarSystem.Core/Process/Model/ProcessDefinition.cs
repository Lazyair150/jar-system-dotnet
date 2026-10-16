namespace JarSystem.Core.Process;

/// <summary>
/// 过程定义（**不可变模板**）：描述"要跑一个什么样的过程"，不含任何运行期状态。
///
/// P1 只承载最小集合（种类/主体/优先级/时限/宽限）；P2 起会加入计划步骤序列
/// （时间化排程需要事先知道步数），P3 起加入守护与前置条件声明。
/// 这样分步而不是一次做全，是为了让每一步都保持"行为可验证、可回滚"。
/// </summary>
public sealed class ProcessDefinition
{
    public ProcessDefinition(string kind, string subject, int? subjectId = null,
        int priority = 0, int? deadlineTicks = null, int graceTicks = 0, int? ticksPerStep = null)
    {
        Kind = kind;
        Subject = subject;
        SubjectId = subjectId;
        Priority = priority;
        DeadlineTicks = deadlineTicks;
        GraceTicks = graceTicks;
        TicksPerStep = ticksPerStep;
    }

    /// <summary>过程种类（取值见 <see cref="ProcessKinds"/>）。</summary>
    public string Kind { get; }

    /// <summary>主体描述（**世界语言**，直接给玩家看：例 `"委托 #12"`）。</summary>
    public string Subject { get; }

    /// <summary>结构化主体标识（订单 id / 工作台下标）；null 表示无。</summary>
    public int? SubjectId { get; }

    /// <summary>同一 tick 内的推进顺序（越小越先；P2 起生效）。</summary>
    public int Priority { get; }

    /// <summary>时限（相对 tick 数，启动时换算成绝对 tick）；null = 无时限。</summary>
    public int? DeadlineTicks { get; }

    /// <summary>被阻断后的宽限窗口（P3 起生效）。</summary>
    public int GraceTicks { get; }

    /// <summary>
    /// 步间隔（刻/步）；null = 由 <see cref="StepAllocator"/> 按过程种类决定。
    /// 单次过程要特殊节奏时（例：加急委托）在这里覆盖它。
    /// </summary>
    public int? TicksPerStep { get; }
}

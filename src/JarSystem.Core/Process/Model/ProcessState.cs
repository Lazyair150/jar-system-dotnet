namespace JarSystem.Core.Process;

/// <summary>
/// 过程状态机（设计规格 §2.2）。**内部枚举**：对外取值由 <see cref="ProcessContract"/> 映射为冻结字符串。
///
/// 迁移规则（唯一入口是 <see cref="ProcessInstance"/> 的意图方法）：
/// <code>
/// Created -MarkReady-> Ready -Start-> Running -CommitStep-> Running -Conclude-> Succeeded
///                                          |-Block-> Blocked -Resume-> Running
///                                          |-Conclude-> Failed
/// 非终态 -Conclude(Aborted)-> Aborted
/// </code>
/// 终态（Succeeded / Failed / Aborted）**不可再推进**（抛异常），但**重复终结是幂等的**（返回 false）。
/// </summary>
public enum ProcessState
{
    /// <summary>已创建，尚未接受计划。</summary>
    Created,

    /// <summary>已接受计划，尚未开始。</summary>
    Ready,

    /// <summary>推进中。</summary>
    Running,

    /// <summary>被阻断（前置条件不成立），等待宽限窗口内恢复。P3 起使用。</summary>
    Blocked,

    /// <summary>成功结尾。</summary>
    Succeeded,

    /// <summary>失败结尾。</summary>
    Failed,

    /// <summary>被取消（外部意图），已完成的部分不回滚。</summary>
    Aborted
}

/// <summary>过程结尾（同一过程只会有一个；未终结时为 null）。</summary>
public enum ProcessOutcome
{
    Succeeded,
    Failed,
    Aborted
}

/// <summary>
/// 过程契约字符串（**取值即契约**，改名 = 改契约；与设计规格 §2.1 一致）。
/// 单独成类而不是直接写字符串字面量：投影（P4）与过程日志共用同一份映射，杜绝两处各写一份。
/// </summary>
public static class ProcessContract
{
    public const string Created = "created";
    public const string Ready = "ready";
    public const string Running = "running";
    public const string Blocked = "blocked";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Aborted = "aborted";

    /// <summary>状态到契约字符串。</summary>
    public static string Of(ProcessState state) => state switch
    {
        ProcessState.Created => Created,
        ProcessState.Ready => Ready,
        ProcessState.Running => Running,
        ProcessState.Blocked => Blocked,
        ProcessState.Succeeded => Succeeded,
        ProcessState.Failed => Failed,
        ProcessState.Aborted => Aborted,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知过程状态")
    };

    /// <summary>结尾到契约字符串。</summary>
    public static string Of(ProcessOutcome outcome) => outcome switch
    {
        ProcessOutcome.Succeeded => Succeeded,
        ProcessOutcome.Failed => Failed,
        ProcessOutcome.Aborted => Aborted,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "未知过程结尾")
    };
}

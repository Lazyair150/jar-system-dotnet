namespace JarSystem.Core.Process;

/// <summary>
/// 过程日志条目种类（**取值即契约**，见设计规格 §3.5；改名 = 改存档格式）。
/// </summary>
public static class JournalKinds
{
    public const string PlanAccepted = "plan_accepted";
    public const string Started = "started";
    public const string StepBegin = "step_begin";
    public const string StepApplied = "step_applied";
    public const string StepSkipped = "step_skipped";
    public const string Blocked = "blocked";
    public const string Resumed = "resumed";
    public const string GraceExpired = "grace_expired";
    public const string Failed = "failed";
    public const string Succeeded = "succeeded";
    public const string Aborted = "aborted";
    public const string ModifierOn = "modifier_on";
    public const string ModifierOff = "modifier_off";

    /// <summary>执行阶段里程碑（转移 / 转移液体 / 降级 / 交付 等，属于"过程内的大动作"）。</summary>
    public const string Phase = "phase";
}

using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Abstractions;

/// <summary>
/// **过程需要的执行原语**（P4c：把 `Process ↔ Executor` 的成环解开）。
///
/// 会话（Process）要驱动"应用一个计划步骤 / 跑一个贪心阶段"，而执行编排住在 Executor ——
/// 直接互指会成环。抽到这里后：`Process → Abstractions ← Executor`，两侧单向。
/// **项目已有同类先例**：`ISolver` 正是这样把"可行性分析 ↔ 求解器"解开的。
///
/// 返回形状刻意**中性**（不出现 Executor 的内部类型），因此宿主也可以自己实现它（例如做录制/回放替身）。
/// </summary>
public interface IProcessStepPrimitives
{
    /// <summary>应用一个计划步骤（含过程日志记账；<paramref name="recorder"/> 为 null 时只执行不记账）。</summary>
    (bool Ok, ExecutionLogEntry? Entry, string? Error) ApplyPlannedStep(
        Workshop workshop, SolverStepDto step, string phase, IProcessRecorder? recorder);

    /// <summary>跑一个贪心阶段（一个批次单位；含过程日志记账）。</summary>
    (IReadOnlyList<ExecutionLogEntry> Ops, IReadOnlyList<string> Errs) RunGreedyStage(
        Workshop workshop, IReadOnlyList<Requirement> requirements, string phase, OrderEvent ev,
        IProcessRecorder? recorder);
}

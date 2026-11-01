using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>
/// **过程需要的执行原语**（P4c：把 `Process ↔ Executor` 的成环解开）。
///
/// 会话（Process）要驱动"应用一个计划步骤 / 跑一个贪心阶段"，而执行编排住在 Executor ——
/// 直接互指会成环。抽出本接口后：`Process → Orders ← Executor`，两侧单向。
///
/// **注意：P4c 原本把它放在 `Abstractions`**，那里 `Abstractions → Orders` 的成环边**只有这一个文件**
/// —— 它的三个词汇（`SolverStepDto` / `ExecutionLogEntry` / `OrderEvent`）全部是订单执行概念，
/// 放在抽象层等于让抽象层反向认识实现层。契约跟着词汇走，所以它现在住在 `Orders`。
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

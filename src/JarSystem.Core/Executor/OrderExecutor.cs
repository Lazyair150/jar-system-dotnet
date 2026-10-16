using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Commands;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Executor;

/// <summary>
/// 订单执行器：按可行性路径或贪心策略在真实工作台上执行订单。
///
/// 阶段 2 拆分后的职责仅剩**执行编排**：A/B 类订单的主台阶段 → 选择性转移 → 从台阶段 → 交付判定。
/// 协作者：<see cref="SolverStepDto"/>（路径步骤强类型模型）、<see cref="SolverStepApplier"/>（单步应用与状态预检）、
/// <see cref="GreedyPlanBuilder"/>（贪心回退）、<see cref="JarSelector"/>（罐子选择策略）。
/// </summary>
public class OrderExecutor : IProcessStepPrimitives
{
    private readonly JarSelector _selector;
    private readonly SolverStepApplier _applier = new();
    private readonly GreedyPlanBuilder _greedy;

    /// <param name="rng">随机源（透传给随机选择策略，使随机策略可复现）；null 时由 <see cref="JarSelector"/> 新建。</param>
    public OrderExecutor(Random? rng = null)
    {
        _selector = new JarSelector(rng);
        _greedy = new GreedyPlanBuilder(_selector);
    }

    /// <summary>罐子选择策略（转发给 <see cref="JarSelector"/>，对外 API 与重构前一致）。</summary>
    public JarSelectionStrategy SelectionStrategy
    {
        get => _selector.Strategy;
        set => _selector.Strategy = value;
    }

    /// <param name="recorder">
    /// 过程记账器：每应用一步就报告一次（写进过程日志）。null = 不记账（既有行为不变）。
    /// <para>
    /// P2b 起**编排已迁到 <see cref="Process.OrderExecutionSession"/>**：本类退化为
    /// "单步执行体 + 会话工厂"，同步 drain 与异步 tick 驱动**共用同一个会话**（语义只有一份）。
    /// </para>
    /// </param>
    public (bool Success, List<ExecutionLogEntry> Operations, List<string> Errors, string? Reason)
        Execute(Order order, Workshop master, Workshop slave, OrderEvent event_, List<SolverStepDto>? solverPath,
            IProcessRecorder? recorder = null)
    {
        var session = BeginSession(order, master, slave, event_, solverPath);
        // 同步轨（既有 executeOrder 的语义）：一个调用里把单位跑到完，不等待刻。
        while (session.AdvanceOneUnit(recorder)) { }
        return (session.Succeeded, session.Operations, session.Errors, session.Reason);
    }

    /// <summary>
    /// 建立一次执行的**分步会话**（同步轨与异步轨共用；本方法**不改世界**）：
    /// 把计划拆成"主台步骤 / 从台步骤 + 是否求解器方案"，世界的变更交给会话的每次推进。
    /// </summary>
    internal Process.OrderExecutionSession BeginSession(Order order, Workshop master, Workshop slave,
        OrderEvent event_, List<SolverStepDto>? solverPath, IProcessRecorder? recorder = null)
    {
        var masterSteps = order.Type == Constants.OrderType.A
            ? new List<SolverStepDto>(solverPath ?? new List<SolverStepDto>())
            : new List<SolverStepDto>(order.FeasibilityResult?.MasterPath ?? new List<SolverStepDto>());
        var slaveSteps = order.Type == Constants.OrderType.B && solverPath != null
            ? new List<SolverStepDto>(solverPath)
            : new List<SolverStepDto>();
        var checkMethod = order.FeasibilityResult?.CheckMethod;
        bool solverBasedMethod = checkMethod is Constants.CheckMethod.SLAVE_ONLY or Constants.CheckMethod.PARTITION;
        return new Process.OrderExecutionSession(order, master, slave, event_, this,
            masterSteps, slaveSteps, solverBasedMethod, recorder);
    }

    /// <summary>应用一个**计划步骤**（会话用；含过程日志记账）。</summary>
    public (bool Ok, ExecutionLogEntry? Entry, string? Error) ApplyPlannedStep(
        Workshop workshop, SolverStepDto step, string phase, IProcessRecorder? recorder)
    {
        var r = _applier.Apply(workshop, step, phase);
        if (r.Ok) Record(r.Entry, recorder);
        return r;
    }

    /// <summary>跑一个**贪心阶段**（会话里的一个批次单位；含过程日志记账）。</summary>
    (IReadOnlyList<ExecutionLogEntry> Ops, IReadOnlyList<string> Errs) IProcessStepPrimitives.RunGreedyStage(Workshop workshop, IReadOnlyList<Requirement> requirements,
        string phase, OrderEvent ev, IProcessRecorder? recorder)
    {
        var result = _greedy.ExecuteOn(workshop, requirements, phase, ev);
        RecordAll(result.Ops, recorder);
        return (result.Ops, result.Errs);
    }

    // ============================================================
    // P1：过程记账（写进过程日志的"步"与"阶段里程碑"）
    //
    // 三条约定的理由：
    //   · **recorder 为 null 时空操作** ⇒ 既有调用点与测试完全不受影响（等价性的第一道保障）；
    //   · 词汇沿用既有执行日志（ExecutionLogEntry.Action）⇒ 不为同一件事发明第二套名字；
    //   · 只记"已成功应用"的步（失败的步由过程结尾的 failed 条目承载）⇒ 日志不被失败噪声淹没。
    // ============================================================

    private static void Record(ExecutionLogEntry? entry, IProcessRecorder? recorder)
    {
        if (entry == null || recorder == null) return;
        recorder.StepApplied(entry.Action ?? entry.Command ?? "step", null, Summarize(entry));
    }

    private static void RecordAll(IEnumerable<ExecutionLogEntry> entries, IProcessRecorder? recorder)
    {
        if (recorder == null) return;
        foreach (var e in entries) Record(e, recorder);
    }

    /// <summary>把一条执行日志渲染成"世界语言摘要"（诊断用；不参与任何判定）。</summary>
    private static string Summarize(ExecutionLogEntry e)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(e.Action)) parts.Add(e.Action!);
        if (!string.IsNullOrEmpty(e.Jar)) parts.Add(e.Jar!);
        if (!string.IsNullOrEmpty(e.Liquid)) parts.Add(e.Liquid!);
        if (e.Amount.HasValue) parts.Add("×" + e.Amount.Value);
        if (!string.IsNullOrEmpty(e.Description)) parts.Add(e.Description!);
        return parts.Count == 0 ? "step" : string.Join(" ", parts);
    }
}

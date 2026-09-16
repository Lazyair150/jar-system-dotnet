using System.Collections.Generic;
using System.Linq;
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
public class OrderExecutor
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

    public (bool Success, List<ExecutionLogEntry> Operations, List<string> Errors, string? Reason)
        Execute(Order order, Workshop master, Workshop slave, OrderEvent event_, List<SolverStepDto>? solverPath)
    {
        if (solverPath != null && solverPath.Count > 0 && order.Type == Constants.OrderType.A)
        {
            var pathResult = ExecuteViaSolverPath(order, master, slave, event_, solverPath);
            if (pathResult.Success) return pathResult;
        }
        if (order.Type == Constants.OrderType.A) return ExecuteTypeA(order, master, event_);
        return ExecuteTypeB(order, master, slave, event_, solverPath);
    }

    private (bool Success, List<ExecutionLogEntry> Operations, List<string> Errors, string? Reason) ExecuteViaSolverPath(
        Order order, Workshop master, Workshop slave, OrderEvent event_, List<SolverStepDto> solverPath)
    {
        var ops = new List<ExecutionLogEntry>();
        var errs = new List<string>();
        var w = order.Type == Constants.OrderType.A ? master : slave;

        // B 类订单路径只能在从台阶段执行（转移成功后才允许）
        if (order.Type == Constants.OrderType.B && event_.CurrentPhase != Constants.OrderPhase.SLAVE)
            return (false, ops, errs, "当前不在从台阶段");

        for (int si = 0; si < solverPath.Count; si++)
        {
            var r = _applier.Apply(w, solverPath[si], "solver_path");
            if (!r.Ok) { errs.Add("步骤" + si + ": " + r.Error); continue; }
            ops.Add(r.Entry!);
        }
        if (errs.Count > 0) return (false, ops, errs, "路径执行部分失败");
        return order.IsSatisfiedBy(w.Jars) ? (true, ops, errs, null) : (false, ops, errs, "路径执行后未满足");
    }

    private (bool Success, List<ExecutionLogEntry> Operations, List<string> Errors, string? Reason) ExecuteTypeA(
        Order order, Workshop master, OrderEvent ev)
    {
        var ops = new List<ExecutionLogEntry>();
        var errs = new List<string>();
        var mr = _greedy.ExecuteOn(master, order.Requirements, "master", ev);
        ops.AddRange(mr.Ops); errs.AddRange(mr.Errs);
        if (order.IsSatisfiedBy(master.Jars)) return (true, ops, errs, null);
        return (false, ops, errs, "主台资源不足");
    }

    private (bool Success, List<ExecutionLogEntry> Operations, List<string> Errors, string? Reason) ExecuteTypeB(
        Order order, Workshop master, Workshop slave, OrderEvent ev, List<SolverStepDto>? solverPath)
    {
        var ops = new List<ExecutionLogEntry>();
        var errs = new List<string>();
        // Try solver master path on master workshop, fallback to greedy
        WorkshopStageResult mr;
        bool masterPathErrored = false;
        var checkMethod = order.FeasibilityResult?.CheckMethod;
        bool solverBasedMethod = checkMethod is Constants.CheckMethod.SLAVE_ONLY or Constants.CheckMethod.PARTITION;
        if (order.FeasibilityResult?.MasterPath != null && order.FeasibilityResult.MasterPath.Count > 0)
        {
            var masterOps = new List<ExecutionLogEntry>();
            var masterErrs = new List<string>();
            foreach (var step in order.FeasibilityResult.MasterPath)
            {
                var r = _applier.Apply(master, step, "master_path");
                if (r.Ok) masterOps.Add(r.Entry!);
                else masterErrs.Add(r.Error ?? "步骤失败");
            }
            mr = new WorkshopStageResult { Ops = masterOps, Errs = masterErrs };
            masterPathErrored = masterErrs.Count > 0;
        }
        else if (solverBasedMethod)
        {
            // 求解器方案已定义主台阶段做什么（slave_only / partition 可能为空），
            // 不再运行贪心主台操作，避免改变路径依赖的初始状态。
            mr = new WorkshopStageResult();
        }
        else
        {
            mr = _greedy.ExecuteOn(master, order.Requirements, "master", ev);
        }
        ops.AddRange(mr.Ops); errs.AddRange(mr.Errs);

        // 主台路径是可行性分析的执行承诺：任何一步失败都意味着主台状态已偏离模拟，
        // 转移清单与从台路径都不再可靠，必须明确失败而非带着错误继续/静默成功。
        if (masterPathErrored)
            return (false, ops, errs, "主台路径执行失败，状态与可行性分析不一致");

        // 选择性转移：只转移可行性路径记录的罐子清单（交付罐 + 从台计划使用的工具罐），
        // 其余罐子留在主台。无可行性结果（直接执行）时退回全量转移。
        List<Command> xfer;
        if (order.FeasibilityResult?.TransferJarIds != null)
        {
            xfer = order.FeasibilityResult.TransferJarIds
                .Select(id => master.Jars.FirstOrDefault(j => j.Id == id))
                .Where(j => j != null)
                .Select(j => (Command)new TransferCommand(j!, master, slave))
                .ToList();
        }
        else
        {
            xfer = master.Jars.Select(j => new TransferCommand(j, master, slave)).Cast<Command>().ToList();
        }
        var tr = ev.ExecuteBatch(xfer, master);
        if (!tr.Success)
        {
            // 转移失败即终止：可行性分析以「选择性转移清单」为前提，不允许部分转移后继续
            return (false, ops, errs, "从台容量不足，无法接收转移清单中的全部罐子");
        }
        ops.Add(new ExecutionLogEntry { Phase = "transfer", Action = "transfer", Description = $"转移 {xfer.Count} 个罐子与所需液体到从台" });
        var transferLiq = order.FeasibilityResult?.TransferLiquids;
        foreach (var l in Constants.LIQUIDS)
        {
            var available = master.LiquidStorage.GetValueOrDefault(l);
            var need = transferLiq != null ? transferLiq.GetValueOrDefault(l, 0) : available;
            if (need <= 0) continue;
            if (slave.IsBanned(l)) continue; // 从台禁液：留在主台，不转移
            var amt = Math.Min(need, available);
            if (amt <= 0) continue;
            // 容量感知的部分填充：与可行性分析的截断语义一致，溢出部分留在主台
            var accepted = slave.TryReceiveLiquid(l, amt);
            if (accepted <= 0)
            {
                ops.Add(new ExecutionLogEntry { Phase = "transfer", Action = "transfer_liquid_failed", Description = $"从台容量不足，{l}×{need} 留在主台" });
                continue;
            }
            master.RemoveLiquid(l, accepted);
            ev.RecordLiquidTransfer(l, accepted);
            if (accepted < need)
                ops.Add(new ExecutionLogEntry { Phase = "transfer", Action = "transfer_liquid_failed", Description = $"从台容量不足，{l}×{need - accepted} 留在主台" });
        }
        ev.AdvanceToSlavePhase();
        slave.ClearMarks();

        // 阶段3: 从台优先使用求解器路径，失败时降级到贪心执行
        if (solverPath != null && solverPath.Count > 0)
        {
            var pathResult = ExecuteViaSolverPath(order, master, slave, ev, solverPath);
            if (pathResult.Success) { ops.AddRange(pathResult.Operations); errs.AddRange(pathResult.Errors); }
            else
            {
                ev.ExecutionLog.Add(new ExecutionLogEntry { Phase = "fallback", Action = "path_failed_fallback", Description = "路径执行失败，使用默认策略" });
                var fb = _greedy.ExecuteOn(slave, order.Requirements, "slave", ev);
                ops.AddRange(fb.Ops); errs.AddRange(fb.Errs);
            }
        }
        else
        {
            var sr = _greedy.ExecuteOn(slave, order.Requirements, "slave", ev);
            ops.AddRange(sr.Ops); errs.AddRange(sr.Errs);
        }

        if (order.IsSatisfiedBy(slave.Jars)) return (true, ops, errs, null);
        return (false, ops, errs, "从台资源不足");
    }
}

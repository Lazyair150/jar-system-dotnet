using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Commands;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Abstractions;

namespace JarSystem.Core.Executor;

/// <summary>
/// 贪心回退计划（原 <c>OrderExecutor.ExecOnWs / TryFull / TryEmpty / HasFreeLiquid</c>）。
///
/// 仅在「无求解器路径」或「路径执行失败降级」时使用。语义（不可改动）：
/// - 需求按执行顺序（满罐优先）逐个尝试；
/// - 「就简原则」：只有在工作台完全没有可用游离液体时，才允许从满罐倒出以换取液体；
/// - 所有真实状态变更都经 <see cref="OrderEvent.ExecuteCommand"/>（阶段/工作台校验 + 记账）。
/// </summary>
internal sealed class GreedyPlanBuilder
{
    private readonly JarSelector _selector;

    public GreedyPlanBuilder(JarSelector selector)
    {
        _selector = selector;
    }

    public WorkshopStageResult ExecuteOn(Workshop w, IReadOnlyList<Requirement> reqs, string loc, OrderEvent ev)
    {
        var sorted = RequirementOrdering.FullFirst(reqs);
        var ops = new List<ExecutionLogEntry>();
        var errs = new List<string>();
        foreach (var req in sorted)
        {
            bool ok; ExecutionLogEntry? e; string? reason;
            if (req.Liquid != null) { (ok, e, reason) = TryFull(req, w, loc, ev); }
            else { (ok, e, reason) = TryEmpty(req, w, loc, ev); }
            if (ok) ops.Add(e!);
            else if (reason != null) errs.Add(reason);
        }
        return new WorkshopStageResult { Ops = ops, Errs = errs };
    }

    private (bool, ExecutionLogEntry?, string?) TryFull(Requirement req, Workshop w, string loc, OrderEvent ev)
    {
        var s = req.Shape; var lq = req.Liquid!;
        var ex = FindJar(w.Jars, j => j.Shape == s && j.Liquid == lq && !j.IsMarked);
        if (ex != null)
        {
            var matched = w.MarkJar(ex);
            if (!matched.Success) return (false, null, matched.Reason);
            return (true, new ExecutionLogEntry { Phase = loc, Action = "use_existing", Jar = ex.GetDescription() }, null);
        }

        var emp = FindJar(w.Jars, j => j.Shape == s && j.IsEmpty() && !j.IsMarked);
        if (emp != null)
        {
            var grant = w.Liquid.Check(lq);
            if (!grant.Granted) return (false, null, grant.Reason);
            if (w.LiquidStorage.GetValueOrDefault(lq) < 1) return (false, null, "液体" + lq + "不足");
            var cr = ev.ExecuteCommand(new PourInCommand(emp, lq), w);
            if (cr.Success) return (true, new ExecutionLogEntry { Phase = loc, Action = "pourIn", Jar = emp.GetDescription(), Liquid = lq }, null);
            return (false, null, cr.Reason);
        }

        if (w.LiquidStorage.GetValueOrDefault(lq) < 1)
        {
            var sf = FindJar(w.Jars, j => j.Liquid == lq && !j.IsMarked && !w.Liquid.IsBanned(lq));
            if (sf != null && w.Liquid.CanStore(lq, 1, w.LiquidStorage.GetValueOrDefault(lq)).Granted)
            {
                var r = ev.ExecuteCommand(new PourOutCommand(sf, w), w);
                if (r.Success) return TryFull(req, w, loc, ev);
            }
        }

        return (false, null, "无法创建满罐(" + s + "," + lq + ")");
    }

    private (bool, ExecutionLogEntry?, string?) TryEmpty(Requirement req, Workshop w, string loc, OrderEvent ev)
    {
        var s = req.Shape;
        var ex = FindJar(w.Jars, j => j.Shape == s && j.IsEmpty() && !j.IsMarked);
        if (ex != null)
        {
            var matched = w.MarkJar(ex);
            if (!matched.Success) return (false, null, matched.Reason);
            return (true, new ExecutionLogEntry { Phase = loc, Action = "use_existing", Jar = ex.GetDescription() }, null);
        }

        var sr = FindJar(w.Jars, j => j.IsEmpty() && !j.IsMarked && w.CanTransform(j.Shape, s));
        if (sr != null)
        {
            var cl = Constants.LIQUIDS.FirstOrDefault(l => w.Liquid.CanPay(l, 1, w.LiquidStorage.GetValueOrDefault(l)).Granted);
            if (cl != null)
            {
                var cr = ev.ExecuteCommand(new ReshapeEmptyCommand(sr, s, cl), w);
                if (cr.Success) return (true, new ExecutionLogEntry { Phase = loc, Action = "reshape", Jar = sr.GetDescription(), Liquid = s }, null);
                return (false, null, cr.Reason);
            }
            var sf = FindJar(w.Jars, j => j.Liquid != null && !j.IsMarked && !w.Liquid.IsBanned(j.Liquid) && w.Liquid.CanStore(j.Liquid!, 1, w.LiquidStorage.GetValueOrDefault(j.Liquid!)).Granted);
            if (sf != null)
            {
                var r = ev.ExecuteCommand(new PourOutCommand(sf, w), w);
                if (r.Success) return TryEmpty(req, w, loc, ev);
            }
        }

        var fs = FindJar(w.Jars, j => j.Liquid != null && !j.IsMarked && !w.Liquid.IsBanned(j.Liquid) && w.CanTransform(j.Shape, s));
        if (fs != null)
        {
            var rr = w.TryReshape(fs, s, ReshapeMode.Full);
            if (rr.Success) return (true, new ExecutionLogEntry { Phase = loc, Action = "reshape_full", Jar = fs.GetDescription(), Liquid = s }, null);
        }

        // 就简原则: only pour out from full jars when no free liquid is available
        if (!HasFreeLiquid(w))
        {
            var pa = FindJar(w.Jars, j => j.Liquid != null && !j.IsMarked && !w.Liquid.IsBanned(j.Liquid) && w.Liquid.CanStore(j.Liquid!, 1, w.LiquidStorage.GetValueOrDefault(j.Liquid!)).Granted);
            if (pa != null)
            {
                var r = ev.ExecuteCommand(new PourOutCommand(pa, w), w);
                if (r.Success)
                {
                    if (pa.Shape == s && pa.IsEmpty())
                        return (true, new ExecutionLogEntry { Phase = loc, Action = "pour_out_and_use", Jar = pa.GetDescription() }, null);
                    return TryEmpty(req, w, loc, ev);
                }
            }
        }

        return (false, null, "无法获得空罐(" + s + ")");
    }

    /// <summary>工作台是否有可用（未禁且有库存）的游离液体。</summary>
    private static bool HasFreeLiquid(Workshop w) => Constants.LIQUIDS.Any(w.HasUsableLiquid);

    private Jar? FindJar(IReadOnlyList<Jar> jars, Func<Jar, bool> pred) => _selector.Select(jars, pred);
}

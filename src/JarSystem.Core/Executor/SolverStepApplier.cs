using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Executor;

/// <summary>
/// 求解器路径的**单步应用器**（原 <c>OrderExecutor.ApplySolverStep</c>）。
///
/// 契约（不可改动）：
/// - 按 `jarId` 首选定位罐子，失败再退回 `jarIndex`（转移后罐子顺序会变，ID 才是稳定身份）；
/// - 应用前校验真实罐子与步骤预期状态（形状/液体）一致，不符即判失败，绝不"碰运气"继续；
/// - RESHAPE 按 `reshapeMode` 分派空罐改形 / 满罐改形（缺字段时按罐子实际状态推断）。
///
/// 阶段 3-D：步骤类型由弱类型字典改为 <see cref="SolverStepDto"/>，
/// 原先散落的 `SolverPathAdapter.ReadXxx(step)` 读取调用（含 `as` 转换与键名查表）随之消失。
/// </summary>
internal sealed class SolverStepApplier
{
    public (bool Ok, ExecutionLogEntry? Entry, string? Error) Apply(
        Workshop w, SolverStepDto step, string phase)
    {
        var type = step.Type;
        if (string.IsNullOrEmpty(type)) return (false, null, "无操作类型");

        var jarId = step.JarId;
        var jarIndex = step.JarIndex;

        Jar? jar = null;
        if (jarId.HasValue) jar = w.Jars.FirstOrDefault(j => j.Id == jarId);
        if (jar == null && jarIndex.HasValue && jarIndex.Value >= 0 && jarIndex.Value < w.Jars.Count)
            jar = w.Jars[jarIndex.Value];
        if (jar == null) return (false, null, "找不到罐子");

        // 执行前校验：真实罐子必须与路径步骤的预期状态一致
        var expShape = step.JarShape;
        var expLiquid = step.JarLiquid;
        if (expShape != null && jar.Shape != expShape)
            return (false, null, "罐子形状状态不符: 期望 " + expShape + " 实际 " + jar.Shape);
        if (jar.Liquid != expLiquid)
            return (false, null, "罐子液体状态不符");

        // 「一罐一操作」（Domain/Jar.cs:10）：同一只罐子在一份计划里只能被操作一次。
        // 这一道守卫必须住在**执行器**，不能只靠下游的 Workshop 判定 ——
        // `MATCH` 分支直接 `jar.Mark()`，而 Mark 是幂等的：没有这里，
        // 一条把同一只罐子 MATCH 两次的计划会"满足"两条需求，
        // 于是求解器的"可行"变成假的（玩家看到一张做不出来的订单被判可行）。
        if (jar.IsMarked)
            return (false, null, "罐子已被操作过（一罐一操作）: " + jar.GetDescription());

        switch (type)
        {
            case "MATCH":
                jar.Mark();
                return (true, new ExecutionLogEntry { Phase = phase, Action = "direct_match", Jar = jar.GetDescription() }, null);

            case "POUR_IN":
            {
                var tl = step.TargetLiquid;
                if (tl == null) return (false, null, "POUR_IN缺targetLiquid");
                var r = w.PourIn(jar, tl);
                if (!r.Success) return (false, null, r.Reason);
                return (true, new ExecutionLogEntry { Phase = phase, Action = "pourIn", Jar = jar.GetDescription(), Liquid = tl }, null);
            }

            case "POUR_OUT":
            {
                var r = w.PourOut(jar);
                if (!r.Success) return (false, null, r.Reason);
                return (true, new ExecutionLogEntry { Phase = phase, Action = "pourOut", Jar = jar.GetDescription() }, null);
            }

            case "RESHAPE":
            {
                var ts = step.TargetShape;
                if (ts == null) return (false, null, "RESHAPE缺targetShape");
                var reshapeMode = step.ReshapeMode;
                bool full = reshapeMode == "full" || (reshapeMode == null && !jar.IsEmpty());
                if (full)
                {
                    var rf = w.ReshapeFull(jar, ts);
                    if (!rf.Success) return (false, null, rf.Reason);
                    return (true, new ExecutionLogEntry { Phase = phase, Action = "reshape_full", Jar = jar.GetDescription(), Liquid = ts }, null);
                }
                var cst = step.CostLiquid;
                var re = w.ReshapeEmpty(jar, ts, cst!);
                if (!re.Success) return (false, null, re.Reason);
                return (true, new ExecutionLogEntry { Phase = phase, Action = "reshape", Jar = jar.GetDescription(), Liquid = ts }, null);
            }

            default:
                return (false, null, "未知类型 " + type);
        }
    }
}

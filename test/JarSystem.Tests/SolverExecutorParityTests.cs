using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Executor;
using JarSystem.Core.Orders;
using JarSystem.Core.Solver;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 求解器 ⇄ 执行器的对拍门。
///
/// 为什么需要它：<c>SOLVER-OK</c> 比的是**两个求解器**的结论一致（`StateSpaceSolver` vs 带前置检查的那条路），
/// 证明不了"求解器说的可行，真实执行器也做得到"。求解器建模用 <see cref="WorkshopInfo"/>（纯值），
/// 执行器操作真 <see cref="Workshop"/> —— 两套类型只靠人工保证一致，历史上已经出过
/// "液体要么在池子里要么在这只罐子里"这种两个求解器一起错的形态（105 例）。
///
/// 本组测试把**执行器当 oracle**：求解器给出的 <c>Path</c> 必须能被
/// <see cref="SolverStepApplier"/> 逐条施加到真工作台上、全部返回成功，且终态满足全部需求。
/// 这是"求解器可行 ⇒ 一定能做出来"这条玩家可见承诺的唯一直接证据。
///
/// 关于把 <see cref="SolverStep"/> 转成 <see cref="SolverStepDto"/>：这里**故意在测试里手工重建**，
/// 而不是复用生产路径（`OrderExecutionSession` / `PathSummaryFormatter`）。
/// 理由：生产路径如果自己有转换 bug，用重构后的生产转换来验求解器就成了循环论证。
/// 重建只依赖求解器自己给出的 <c>Before</c> 快照（那正是执行前校验要比对的东西）。
/// </summary>
public class SolverExecutorParityTests
{
    private static Vocabulary Vocab() => new(
        new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" },
        new[] { "酒", "蜜" });

    private static Workshop Bench(List<string> baseMarks, List<string>? add = null, List<string>? ban = null) =>
        new("主台", baseMarks, add ?? new List<string>(), ban ?? new List<string>(),
            maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());

    private static WorkshopInfo Info(Workshop w) => new()
    {
        Capability = w.Capability,
        MaxJars = w.MaxJars,
        MaxLiquids = w.MaxLiquids
    };

    private static List<JarState> States(Workshop w) =>
        w.Jars.Select(j => new JarState { Shape = j.Shape, Liquid = j.Liquid }).ToList();

    private static Dictionary<string, int> Liquids(Workshop w) =>
        w.LiquidStorage.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>把求解器步骤重建成执行器认识的 DTO（只用求解器自报的 Before 快照）。</summary>
    private static SolverStepDto ToDto(SolverStep step, Workshop w)
    {
        var op = step.Operation!;
        var idx = op.JarIndex ?? -1;
        var before = step.Before!;
        var st = idx >= 0 && idx < before.Count ? before[idx] : null;

        // 空罐改形要付成本液体：取池子里第一份"能用"的（不被禁、有余量）。
        string? cost = null;
        if (string.Equals(op.Type, "RESHAPE", StringComparison.Ordinal) && st?.Liquid == null)
        {
            cost = w.LiquidStorage
                .Where(kv => kv.Value > 0 && !w.IsBanned(kv.Key))
                .Select(kv => kv.Key)
                .OrderBy(k => k, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        return new SolverStepDto
        {
            Step = step.Step,
            Type = op.Type,
            JarIndex = op.JarIndex,
            JarShape = st?.Shape,
            JarLiquid = st?.Liquid,
            TargetShape = op.TargetShape,
            TargetLiquid = op.TargetLiquid,
            CostLiquid = cost,
            // 缺字段时执行器会按罐子实际状态推断。这里显式填，免得把"推断"当成"验证"。
            ReshapeMode = string.Equals(op.Type, "RESHAPE", StringComparison.Ordinal)
                ? (st?.Liquid == null ? "empty" : "full")
                : null
        };
    }

    private sealed record Case(string Name, Workshop Bench, Action<Workshop, Vocabulary> Setup, List<Requirement> Reqs);

    private static List<Case> Cases()
    {
        var vocab = Vocab();
        return new List<Case>
        {
            new("直接照用：一只圆罐满足空罐需求",
                Bench(new List<string> { "圆罐" }),
                (w, v) => w.AddJar(new Jar("圆罐", null, null, v)),
                new List<Requirement> { new() { Shape = "圆罐" } }),

            new("灌液：圆罐 + 池里的蜜 ⇒ 圆罐:蜜",
                Bench(new List<string> { "圆罐" }),
                (w, v) => { w.AddJar(new Jar("圆罐", null, null, v)); w.AddLiquid("蜜", 2); },
                new List<Requirement> { new() { Shape = "圆罐", Liquid = "蜜" } }),

            new("空罐改形：付一份蜜把圆罐改成三角罐（附加标记）",
                Bench(new List<string> { "圆罐", "方罐" }, new List<string> { "三角罐" }),
                (w, v) => { w.AddJar(new Jar("圆罐", null, null, v)); w.AddLiquid("蜜", 2); },
                new List<Requirement> { new() { Shape = "三角罐" } }),

            new("满罐自耗改形：酒罐改成方罐（成本由罐内液体自付）",
                Bench(new List<string> { "圆罐", "方罐" }),
                (w, v) => { w.AddJar(new Jar("圆罐", "酒", null, v)); w.AddJar(new Jar("圆罐", null, null, v)); },
                new List<Requirement> { new() { Shape = "方罐" } }),

            new("两条需求一起做：一只照用 + 一只灌液",
                Bench(new List<string> { "圆罐", "方罐" }),
                (w, v) =>
                {
                    w.AddJar(new Jar("圆罐", null, null, v));
                    w.AddJar(new Jar("方罐", null, null, v));
                    w.AddLiquid("蜜", 2);
                },
                new List<Requirement> { new() { Shape = "圆罐" }, new() { Shape = "方罐", Liquid = "蜜" } })
        };
    }

    [Fact]
    public void EveryFeasiblePlanActuallyExecutesOnARealWorkshop()
    {
        var solver = new StateSpaceSolver();
        var failures = new List<string>();
        var executed = 0;

        foreach (var c in Cases())
        {
            var w = c.Bench;
            c.Setup(w, Vocab());

            var result = solver.Solve(c.Reqs, Info(w), States(w), Liquids(w));
            if (!result.Feasible)
            {
                // 求解器说不可行 —— 那不是本测试要断言的（另一条测试守反向）。
                // 但这里必须报出来：用例本该是可行的，若求解器说不可行，说明用例或求解器坏了。
                failures.Add($"{c.Name}：求解器判不可行（{result.Blockage}）—— 用例设计有问题或求解器退化");
                continue;
            }
            if (result.Path == null || result.Path.Count == 0)
                continue;   // 零步路径：世界本来就满足，不必执行

            var applier = new SolverStepApplier();
            foreach (var step in result.Path)
            {
                var dto = ToDto(step, w);
                var (ok, entry, error) = applier.Apply(w, dto, "master");
                if (!ok)
                {
                    failures.Add($"{c.Name}：第 {step.Step} 步 {step.Operation?.Type} 执行失败 —— {error}（" +
                                 $"DTO: jar={dto.JarShape}/{dto.JarLiquid} → {dto.TargetShape ?? dto.TargetLiquid}）");
                    break;
                }
                executed++;
            }

            // 执行完之后，世界必须真的满足全部需求（不只是"每一步都没报错"）。
            foreach (var req in c.Reqs)
            {
                bool met = w.Jars.Any(j => j.Shape == req.Shape && j.Liquid == req.Liquid);
                if (!met)
                    failures.Add($"{c.Name}：执行完却没有罐子满足需求（{req.Shape}/{req.Liquid ?? "空"}）");
            }
        }

        Assert.True(executed > 0, "一步都没执行，这条断言会空转");
        Assert.True(failures.Count == 0,
            "求解器给出的可行路径在真实工作台上执行不通：\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public void EveryFeasiblePlanLeavesTheWorldConsistentWithTheSolversOwnFinalState()
    {
        // 第二重校验：不只是"执行成功"，还要和求解器自己报的终态对得上。
        // 若两者不一致，说明求解器的模型（WorkshopInfo/纯值）与真实工作台的同名规则已经漂移，
        // 而这种漂移在"每步都返回成功"的情况下是看不见的。
        var solver = new StateSpaceSolver();
        var problems = new List<string>();

        foreach (var c in Cases())
        {
            var w = c.Bench;
            c.Setup(w, Vocab());
            var result = solver.Solve(c.Reqs, Info(w), States(w), Liquids(w));
            if (!result.Feasible || result.Path == null || result.Path.Count == 0) continue;

            var applier = new SolverStepApplier();
            foreach (var step in result.Path)
            {
                var (ok, _, error) = applier.Apply(w, ToDto(step, w), "master");
                if (!ok) { problems.Add($"{c.Name}: 无法执行（{error}）"); break; }
            }

            // 求解器把最终世界写在哪？契约要求 FinalJars/FinalLiquids 或最后一步的 After 至少其一。
            var last = result.Path[result.Path.Count - 1];
            var expectedJars = result.FinalJars ?? last.After;
            if (expectedJars == null) continue;   // 求解器没报终态，跳过（不假装校验过）

            var actualShapes = w.Jars.Select(j => j.Shape + "/" + (j.Liquid ?? "空")).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var expectedShapes = expectedJars.Select(j => j.Shape + "/" + (j.Liquid ?? "空")).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!actualShapes.SequenceEqual(expectedShapes))
            {
                problems.Add($"{c.Name}: 终态不一致 —— 求解器 [{string.Join(",", expectedShapes)}] " +
                             $"vs 真实 [{string.Join(",", actualShapes)}]");
            }
        }

        Assert.True(problems.Count == 0,
            "求解器自报的终态与真实执行结果不一致（模型漂移）：\n  " + string.Join("\n  ", problems));
    }
}

/// <summary>
/// 执行器的守卫：同一只罐子不许被两次操作。
///
/// 领域规则里「一罐一操作」由 <c>Jar.IsMarked</c> 表达（`Domain/Jar.cs:10` 注释原文），
/// 而执行器的前置校验只比对形状与液体（`Executor/SolverStepApplier.cs:42-47`），
/// **不看标记**。这条缺口最危险的后果不是"多倒一次液"，而是
/// <c>MATCH</c> 分支直接 <c>jar.Mark()</c>（幂等）：
/// 一条把**同一只罐子** MATCH 两次的计划会静默"满足"两条需求，
/// 于是"求解器说可行"变成了假的 —— 玩家会看到一张做不出来的订单被判可行。
///
/// 这一组是**先失败测试**：按读码，第二条应当在修复前就红。
/// </summary>
public class SolverStepApplierGuardTests
{
    private static Vocabulary V() => new(
        new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" },
        new[] { "酒", "蜜" });

    private static Workshop Bench() =>
        new("主台", new List<string> { "圆罐" }, new List<string>(), new List<string>(),
            maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());

    [Fact]
    public void MatchOnAnAlreadyMarkedJarIsRefused()
    {
        var w = Bench();
        w.AddJar(new Jar("圆罐", null, null, V()));
        var applier = new SolverStepApplier();

        var first = new SolverStepDto
        {
            Step = 1, Type = "MATCH", JarIndex = 0, JarShape = "圆罐", JarLiquid = null
        };
        var (ok1, _, err1) = applier.Apply(w, first, "master");
        Assert.True(ok1, "第一步 MATCH 应当成功：" + err1);

        var second = (SolverStepDto)new SolverStepDto
        {
            Step = 2, Type = "MATCH", JarIndex = 0, JarShape = "圆罐", JarLiquid = null
        };
        var (ok2, _, err2) = applier.Apply(w, second, "master");

        Assert.False(ok2,
            "同一只罐子被 MATCH 了两次却都成功 —— 一条计划可以用一只罐子" +
            "「满足」两条需求，求解器的\"可行\"就成了假的");
        Assert.False(string.IsNullOrWhiteSpace(err2));
    }

    [Fact]
    public void PourInOnAnAlreadyMarkedJarIsRefused()
    {
        var w = Bench();
        w.AddJar(new Jar("圆罐", null, null, V()));
        w.AddLiquid("蜜", 2);
        var applier = new SolverStepApplier();

        var (ok1, _, err1) = applier.Apply(w, new SolverStepDto
        {
            Step = 1, Type = "POUR_IN", JarIndex = 0, JarShape = "圆罐", JarLiquid = null, TargetLiquid = "蜜"
        }, "master");
        Assert.True(ok1, "第一步灌液应当成功：" + err1);

        var (ok2, _, err2) = applier.Apply(w, new SolverStepDto
        {
            Step = 2, Type = "POUR_OUT", JarIndex = 0, JarShape = "圆罐", JarLiquid = "蜜"
        }, "master");

        Assert.False(ok2, "同一只罐子在一份计划里被操作了两次（第二次是倒出）却成功");
        Assert.False(string.IsNullOrWhiteSpace(err2));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Solver;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 步骤2b 的健全性门：**禁液不是资源**。
///
/// 2026-11 的语义变更让「池子里存着被禁的液体」成为合法状态（能存不能用），
/// 于是求解器多了一类必须挡住的前提：**池内的禁液不能被当成供应，也不能被当成改形成本**。
///
/// 为什么这条门必须存在（而不是"读代码觉得没问题"）：<c>SOLVER-OK</c> 比的是**两个求解器**
/// 的结论是否一致；若三个实现共享同一个错误前提（"池里出现的液体一律可用"），
/// 门禁不会红 —— 它只会一致地给出错误答案。而这类错误的形态是**放行**
/// （不可行判成可行），报错发生在执行时，比"错杀"更难被发现。
///
/// 实测结论（2026-11）：三个实现**都**已经按 <see cref="WorkshopCapability.IsBanned"/> 过滤，
/// 所以这些用例当前全绿；它们的作用是把"已经对了"变成"不许改坏"。
/// </summary>
public class SolverLiquidSoundnessTests
{
    private static Vocabulary Vocab() => new(
        new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" },
        new[] { "酒", "蜜" });

    /// <summary>禁酒的台：圆罐/方罐为基础标记，三角罐为附加标记。</summary>
    private static Workshop Bench() => new(
        "禁酒台",
        new List<string> { "圆罐", "方罐" },
        new List<string> { "三角罐" },
        new List<string> { "酒" },
        maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());

    private static WorkshopInfo Info(Workshop w) => new()
    {
        Capability = w.Capability,
        MaxJars = w.MaxJars,
        MaxLiquids = w.MaxLiquids
    };

    private static List<JarState> Jars(params string?[] liquids) =>
        liquids.Select(l => new JarState { Shape = "圆罐", Liquid = l }).ToList();

    private static List<ISolver> Solvers() => new List<ISolver>
    {
        new StateSpaceSolver(),
        new PrecheckedStateSpaceSolver(),
        new MatchingSolver()
    };

    private static void NeverFeasible(string what, List<Requirement> reqs, List<JarState> jars,
        Dictionary<string, int> liquids)
    {
        foreach (var solver in Solvers())
        {
            var r = solver.Solve(reqs, Info(Bench()), jars, liquids);
            Assert.False(r.Feasible,
                solver.GetType().Name + " 把「" + what + "」判成了可行 —— 禁液被当成了资源" +
                (r.Blockage == null ? "" : "（blockage: " + r.Blockage + "）"));
        }
    }

    [Fact]
    public void AStoredButBannedLiquidIsNeverASupply()
    {
        // 池里 2 份酒、台禁酒、罐子只有空圆罐：需求「圆罐:酒」必须判不可行/未判定，绝不能可行。
        // 这正是步骤2a 新造出来的局面：酒现在能流进这台（存得下），但取不出来。
        NeverFeasible("用池里的禁酒灌满圆罐",
            new List<Requirement> { new() { Shape = "圆罐", Liquid = "酒" } },
            Jars((string?)null),
            new Dictionary<string, int> { ["酒"] = 2 });
    }

    [Fact]
    public void AStoredButBannedLiquidNeverPaysTheReshapeCost()
    {
        // 空罐改形要付 1 份游离液体；池里只有禁酒 ⇒ 付不出 ⇒ 不可行。
        // 若求解器按"池里液体总数"当预算，这里就会放行。
        NeverFeasible("拿禁酒当改形成本",
            new List<Requirement> { new() { Shape = "三角罐" } },
            Jars((string?)null),
            new Dictionary<string, int> { ["酒"] = 2 });
    }

    [Fact]
    public void AJarCarryingABannedLiquidCannotBeReshapedBySelfConsumption()
    {
        // 读法 B（用户 m01174）：满罐自耗算"使用" ⇒ 罐内是禁酒时不能改形。
        NeverFeasible("把装着禁酒的满罐改形",
            new List<Requirement> { new() { Shape = "方罐" } },
            Jars("酒"),
            new Dictionary<string, int>());
    }

    [Fact]
    public void TheSameThreeScenariosBecomeFeasibleOnceTheBanIsLifted()
    {
        // 反向对照：证明上面三条不是"因为别的什么原因恒不可行"，而是**禁令**在起作用。
        var free = new Workshop("自由台", new List<string> { "圆罐", "方罐" },
            new List<string> { "三角罐" }, new List<string>(),
            maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());
        var info = Info(free);

        foreach (var solver in Solvers())
        {
            // ① 池里的蜜灌满圆罐
            var a = solver.Solve(new List<Requirement> { new() { Shape = "圆罐", Liquid = "蜜" } },
                info, Jars((string?)null), new Dictionary<string, int> { ["蜜"] = 2 });
            Assert.True(a.Feasible, solver.GetType().Name + " 连「池里有蜜就灌满」都判不可行（用例或求解器坏了）");

            // ② 蜜当改形成本
            var b = solver.Solve(new List<Requirement> { new() { Shape = "三角罐" } },
                info, Jars((string?)null), new Dictionary<string, int> { ["蜜"] = 2 });
            Assert.True(b.Feasible, solver.GetType().Name + " 连「有蜜就能改形」都判不可行");

            // ③ 满罐自耗改形（罐内是蜜，不是禁液）
            var c = solver.Solve(new List<Requirement> { new() { Shape = "方罐" } },
                info, Jars("蜜"), new Dictionary<string, int>());
            Assert.True(c.Feasible, solver.GetType().Name + " 连「满罐自耗改形」都判不可行");
        }
    }

    /// <summary>
    /// 用户 m01274 指出的**数量型**误判：液池里 5 份禁酒看起来像 5 份可用液体，
    /// 而需求正好要两份「能用」的液体。若求解器把库存当供应，它会判可行 —— 但实际上一份都取不出。
    /// 这条钉的是"活性液体"的定义，不是单条需求能不能灌。
    /// </summary>
    [Fact]
    public void StoredBannedLiquidDoesNotInflateTheUsableSupply()
    {
        NeverFeasible(
            "池里 5 份禁酒被当成可用液体，两条满罐都灌上了",
            new List<Requirement>
            {
                new() { Shape = "圆罐", Liquid = "蜜" },
                new() { Shape = "圆罐", Liquid = "蜜" }
            },
            Jars((string?)null, (string?)null),
            new Dictionary<string, int> { ["酒"] = 5, ["蜜"] = 0 });
    }

    /// <summary>
    /// 口径本身就是被测对象：把"存量"换算成"可用量"只有一个地方（<see cref="SolverInput"/>）。
    /// 这条防的是"某个实现自己记得过滤、另一个忘了"—— 那种不一致不会在任何世界状态里显形。
    /// </summary>
    [Fact]
    public void UsableLiquidCountsDropTheBannedOnes()
    {
        var cap = Bench().Capability;

        var usable = SolverInput.UsableLiquids(
            new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 1 }, cap);

        Assert.Equal(0, usable["酒"]);   // 存得住，但不算资源
        Assert.Equal(1, usable["蜜"]);
        Assert.Equal(2, usable.Count);   // 键还在：清零不等于抹掉"这格子里有东西"
        Assert.True(cap.IsBanned("酒"));
    }

    [Fact]
    public void BackwardLiquidDemandBoundRejectsUnfundedSecondRequirementAcrossMultipleLiquidKinds()
    {
        var free = new Workshop("反向需求台", new List<string> { "圆罐" },
            maxJars: 6, maxLiquids: 5);
        var info = Info(free);
        var requirements = new List<Requirement>
        {
            new() { Shape = "圆罐", Liquid = "酒" },
            new() { Shape = "圆罐", Liquid = "酒" },
            new() { Shape = "圆罐", Liquid = "酒" },
            new() { Shape = "圆罐", Liquid = "蜜" }
        };
        var jars = Jars("酒", null, null, null);
        var liquids = new Dictionary<string, int> { ["酒"] = 0, ["蜜"] = 1 };

        var reason = SolverPrecheck.ProveInfeasible(requirements, info, jars, liquids, int.MaxValue);

        Assert.NotNull(reason);
        Assert.Contains("酒", reason);
        Assert.Contains("至少需要 1 份", reason);

        var exact = new StateSpaceSolver().Solve(requirements, info, jars, liquids);
        Assert.False(exact.Feasible);

        var prechecked = new PrecheckedStateSpaceSolver();
        var accelerated = prechecked.Solve(requirements, info, jars, liquids);
        Assert.False(accelerated.Feasible);
        Assert.Equal(1, prechecked.PrecheckShortCircuits);
    }

    [Fact]
    public void BackwardLiquidDemandBoundAllowsDirectMatchPlusExactlyOneRequiredUnit()
    {
        var free = new Workshop("反向需求台", new List<string> { "圆罐" },
            maxJars: 6, maxLiquids: 5);
        var info = Info(free);
        var requirements = new List<Requirement>
        {
            new() { Shape = "圆罐", Liquid = "酒" },
            new() { Shape = "圆罐", Liquid = "酒" }
        };
        var jars = Jars("酒", null);
        var liquids = new Dictionary<string, int> { ["酒"] = 1 };

        var reason = SolverPrecheck.ProveInfeasible(requirements, info, jars, liquids, int.MaxValue);

        Assert.Null(reason);
        var result = new PrecheckedStateSpaceSolver().Solve(requirements, info, jars, liquids);
        Assert.True(result.Feasible, result.Blockage);
    }

    [Fact]
    public void BackwardLiquidDemandBoundTreatsBannedLiquidAsZeroExternalSupply()
    {
        var info = Info(Bench());
        var requirements = new List<Requirement>
        {
            new() { Shape = "圆罐", Liquid = "酒" },
            new() { Shape = "圆罐", Liquid = "酒" }
        };
        var jars = Jars("酒", null);

        var reason = SolverPrecheck.ProveInfeasible(
            requirements, info, jars, new Dictionary<string, int> { ["酒"] = 10 }, int.MaxValue);

        Assert.NotNull(reason);
        Assert.Contains("被禁", reason);
    }

    [Fact]
    public void AggregateStagingCapacityRejectsAPathNeedingTwoInitialLiquidUnits()
    {
        var info = Info(new Workshop("总容量一", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 10));
        info.MaxLiquidCapacity = 1;
        var result = new StateSpaceSolver().Solve(
            new List<Requirement>
            {
                new() { Shape = "圆罐", Liquid = "酒" },
                new() { Shape = "圆罐", Liquid = "蜜" }
            },
            info,
            Jars((string?)null, (string?)null),
            new Dictionary<string, int> { ["酒"] = 5, ["蜜"] = 5 });

        Assert.False(result.Feasible);
    }

    [Fact]
    public void AggregateStagingCapacityIgnoresUnusedSourceLiquidSurplus()
    {
        var info = Info(new Workshop("总容量一", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 10));
        info.MaxLiquidCapacity = 1;
        var result = new StateSpaceSolver().Solve(
            new List<Requirement> { new() { Shape = "圆罐", Liquid = "酒" } },
            info,
            Jars((string?)null),
            new Dictionary<string, int> { ["酒"] = 5, ["蜜"] = 5 });

        Assert.True(result.Feasible, result.Blockage);
    }
}

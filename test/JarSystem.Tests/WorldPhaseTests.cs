using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JarSystem.Core;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Services;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **世界阶段**（<see cref="WorldPhase"/>）与「执行中不再重新规划」。
///
/// 为什么这两件事要一起钉：玩家在现场问过两次同一个问题 ——
/// "这一单明明在跑，为什么分析又说缺需求／我点的第 6 单怎么跑的是第 5 单"。
/// 根因不在求解器，而在**问法**：一张正在执行的单，它的资源已经按计划在消耗，
/// 再拿原需求搜一遍必然报"缺少需求"；而"现在轮到做什么"应该由阶段回答，不该靠各页面自己拼。
///
/// 钉住四件事：
/// 1 还没有订单在跑 ⇒ <see cref="WorldPhase.Setup"/>；
/// 2 有订单过程在跑 ⇒ <see cref="WorldPhase.Running"/>，此时**单条分析**回"正在执行中"且不覆盖原计划；
/// 3 这一局已有结果 ⇒ <see cref="WorldPhase.Settled"/>（再推进也改变不了结局）；
/// 4 状态面 `state.phase` 与 <see cref="JarSystem.Core.JarSystem.Phase"/> 逐字一致。
/// </summary>
public class WorldPhaseTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JarSystem.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>第一关的世界（有主台、有生产者、两张订单模板）—— 用它才能真的起一个过程。</summary>
    private static WorldOptions FirstLevelWorld(int? turnLimit = null)
    {
        var file = Path.Combine(RepoRoot(), "config", "levels", "01_学会改形.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var world = WorldOptionsJson.FromJson(doc.RootElement.GetProperty("world").GetRawText());
        world.TurnLimit = turnLimit ?? doc.RootElement.GetProperty("turns").GetInt32();
        world.GoalOrders = doc.RootElement.GetProperty("goal").GetInt32();
        var report = world.Validate();
        Assert.True(report.IsValid, "用例前提：第一关世界必须合法 —— " + string.Join("；", report.Errors));
        return world;
    }

    private static global::JarSystem.Core.JarSystem NewSystem(WorldOptions world)
    {
        var sys = new global::JarSystem.Core.JarSystem(randomSeed: 41, world: world);
        // 第二张模板含“方罐 → 三角罐”的真实改形，能形成执行过程；
        // 第一张是全直接匹配，按新语义开始即交付。
        _ = sys.CreateOrderFromTemplate(sys.OrderTemplates.Names.Last());
        sys.SetMasterIndex(0);
        return sys;
    }

    [Fact]
    public void SetupBeforeAnythingRuns()
    {
        var sys = NewSystem(FirstLevelWorld());

        Assert.Equal(WorldPhase.Setup, sys.Phase);
        Assert.Equal("setup", StateSerializer.Snapshot(sys).Phase);
    }

    [Fact]
    public void RunningWhileAnOrderExecutes()
    {
        var sys = NewSystem(FirstLevelWorld());

        // 前提：这一单要先真的可行，才开始得了（否则 StartOrder 走贪心、一步跑完，就没有"执行中"可言）。
        global::JarSystem.Core.Orders.FeasibilityResultData? feas = null;
        for (var guard = 0; guard < 40 && feas is not { Feasible: true }; guard++)
        {
            feas = sys.CheckFeasibility(0);
            if (feas is not { Feasible: true }) sys.Tick(1);
        }
        Assert.True(feas is { Feasible: true }, "用例前提：这一单最终必须可行");

        var started = sys.StartOrder(0);
        Assert.True(started.Started, "用例前提：这一单必须真的开始 —— " + (started.Error ?? ""));


        Assert.Equal(WorldPhase.Running, sys.Phase);
        Assert.Equal("running", StateSerializer.Snapshot(sys).Phase);
        Assert.Equal(Constants.OrderStatus.EXECUTING, sys.Orders[0].Status);

        // 执行中再问一次分析：不许重新规划，也不许把原来那条计划覆盖成"不可行"。
        var after = sys.CheckFeasibility(0);
        Assert.NotNull(after);
        Assert.False(after!.Feasible);
        Assert.Contains("正在执行中", after.Blockage ?? after.Reason ?? "");
        Assert.True(sys.Orders[0].FeasibilityResult is { Feasible: true },
            "执行中不应该覆盖订单里那条已经生效的计划");
    }

    [Fact]
    public void SettledOnceTheOutcomeIsDecided()
    {
        var world = FirstLevelWorld(turnLimit: 1);
        var sys = NewSystem(world);

        Assert.Equal(WorldPhase.Setup, sys.Phase);

        // 只推进一回合（上限 1）却没做成单 ⇒ 判负 ⇒ 世界进入"已结束"。
        sys.Tick(1);
        Assert.True(sys.Outcome.IsOver, "用例前提：一回合用尽且没做够，应当已有结果");
        Assert.Equal(WorldPhase.Settled, sys.Phase);
        Assert.Equal("settled", StateSerializer.Snapshot(sys).Phase);
    }

    [Fact]
    public void WinningStopsFurtherWorldProgress()
    {
        var sys = ExecutedFirstLevelOrder();
        Assert.Equal(LevelOutcomeKind.Won, sys.Outcome.Kind);
        Assert.True(sys.CreateOrderFromTemplate(sys.OrderTemplates.Names.Last()).Success);

        var tickBefore = sys.Turns.Used;
        var revisionBefore = sys.WorldRevision;

        Assert.Throws<DomainValidationException>(() => sys.Tick());
        var started = sys.StartOrder(1);
        var executed = sys.ExecuteOrder(1);
        Assert.False(started.Started);
        Assert.Contains("已经结束", started.Error ?? "");
        Assert.False(executed.Success);
        Assert.Contains("已经结束", executed.Reason ?? "");
        Assert.Equal(tickBefore, sys.Turns.Used);
        Assert.Equal(revisionBefore, sys.WorldRevision);
    }

    [Fact]
    public void OnlyOneAsyncOrderMayRunAtATime()
    {
        var sys = NewSystem(FirstLevelWorld());
        Assert.True(sys.CreateOrderFromTemplate(sys.OrderTemplates.Names.Last()).Success);

        FeasibilityResultData? feasible = null;
        for (var guard = 0; guard < 40 && feasible is not { Feasible: true }; guard++)
        {
            feasible = sys.CheckFeasibility(0);
            if (feasible is not { Feasible: true }) sys.Tick(1);
        }
        Assert.True(feasible is { Feasible: true }, "用例前提：第一关订单最终应当可行");
        Assert.True(sys.StartOrder(0).Started);

        var second = sys.StartOrder(1);

        Assert.False(second.Started);
        Assert.Contains("已有订单正在执行", second.Error ?? "");
    }

    [Fact]
    public void DeletingACompletedOrderDoesNotEraseTheWin()
    {
        var sys = ExecutedFirstLevelOrder();
        var completedBefore = sys.CompletedOrders;
        Assert.Equal(1, completedBefore);

        _ = sys.DeleteOrder(0);

        Assert.Empty(sys.Orders);
        Assert.Equal(completedBefore, sys.CompletedOrders);
        Assert.Equal(LevelOutcomeKind.Won, sys.Outcome.Kind);
    }

    private static global::JarSystem.Core.JarSystem ExecutedFirstLevelOrder()
    {
        var sys = NewSystem(FirstLevelWorld());
        FeasibilityResultData? feasible = null;
        for (var guard = 0; guard < 40 && feasible is not { Feasible: true }; guard++)
        {
            feasible = sys.CheckFeasibility(0);
            if (feasible is not { Feasible: true }) sys.Tick(1);
        }
        Assert.True(feasible is { Feasible: true }, "用例前提：第一关订单最终应当可行");

        var executed = sys.ExecuteOrder(0);
        Assert.True(executed.Success, executed.Reason);
        return sys;
    }
}

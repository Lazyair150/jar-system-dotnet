using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JarSystem.Core;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Sessions;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 命令日志必须覆盖“宿主能改变世界”的每一个公开入口。
/// 回放门此前只走了少量命令，漏掉了模板派单、订单排序、能力替换和异步迁徙。
/// </summary>
public class CommandLogReplayTests
{
    private static JarSystem.Core.JarSystem NewSystem(WorldOptions? world = null, int seed = 20261103)
        => new(log: null, randomSeed: seed, world: world,
            jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());

    private static JarSystem.Core.JarSystem Replay(JarSystem.Core.JarSystem source, WorldOptions? world = null)
    {
        var target = NewSystem(world, source.RandomSeed ?? 20261103);
        var persisted = WorldCommandJson.FromJson(WorldCommandJson.ToJson(source.CommandLog));
        SessionReplay.Into(target, persisted);
        return target;
    }

    [Fact]
    public void TemplateOrderIsLoggedAndReplays()
    {
        var source = NewSystem();
        var (ok, error) = source.CreateOrderFromTemplate("主从高长");
        Assert.True(ok, error);

        var replayed = Replay(source);

        var original = Assert.Single(source.Orders);
        var restored = Assert.Single(replayed.Orders);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Type, restored.Type);
        Assert.Equal(original.Requirements.Count, restored.Requirements.Count);
        Assert.Equal(original.Requirements[0].Shape, restored.Requirements[0].Shape);
        Assert.Equal(original.Requirements[1].Liquid, restored.Requirements[1].Liquid);
    }

    [Fact]
    public void MoveAndCapabilityCommandsReplay()
    {
        var source = NewSystem();
        Assert.True(source.CreateOrderFromTemplate("空双罐").Success);
        Assert.True(source.CreateOrderFromTemplate("三角改形").Success);

        var movedId = source.Orders[0].Id;
        Assert.True(source.MoveOrder(movedId, 1));
        source.SetWorkshopCapability(0, ["圆罐"], ["三角罐"], ["蜜"]);

        var replayed = Replay(source);

        Assert.Equal(source.Orders.Select(o => o.Id), replayed.Orders.Select(o => o.Id));
        Assert.Equal(source.Workshops[0].BaseMarks, replayed.Workshops[0].BaseMarks);
        Assert.Equal(source.Workshops[0].AdditionalMarks, replayed.Workshops[0].AdditionalMarks);
        Assert.Equal(source.Workshops[0].Ban, replayed.Workshops[0].Ban);
    }

    [Fact]
    public void AsyncMigrationWithDeadlineReplays()
    {
        var source = NewSystem();
        source.SetMasterIndex(0);
        source.Tick(3);

        var started = source.BeginSwitch(1, deadlineTicks: 20);
        Assert.True(started.Started, started.Error);
        var logged = Assert.Single(source.CommandLog, c => c.Op == WorldOps.StartSwitch);
        Assert.Equal(20, logged.IntB);

        source.Tick(3);
        var replayed = Replay(source);

        Assert.Equal(source.CurrentMaster, replayed.CurrentMaster);
        Assert.Equal(source.Processes.Clock.Tick, replayed.Processes.Clock.Tick);
        Assert.Equal(source.Workshops.Select(w => w.Name), replayed.Workshops.Select(w => w.Name));
    }

    [Fact]
    public void AsyncOrderDeadlineReplays()
    {
        var source = NewFirstLevelSystem();
        FeasibilityResultData? feasible = null;
        for (var guard = 0; guard < 40 && feasible is not { Feasible: true }; guard++)
        {
            feasible = source.CheckFeasibility(0);
            if (feasible is not { Feasible: true }) source.Tick();
        }
        Assert.True(feasible is { Feasible: true }, "用例前提：第一关订单最终应当可行");

        var started = source.StartOrder(0, deadlineTicks: 12);
        Assert.True(started.Started, started.Error);
        var logged = source.CommandLog.Last(c => c.Op == WorldOps.StartOrder);
        Assert.Equal(12, logged.IntB);

        source.Tick(2);
        var replayed = Replay(source, FirstLevelWorld());

        Assert.Equal(source.Processes.Clock.Tick, replayed.Processes.Clock.Tick);
        Assert.Equal(source.Orders.Count, replayed.Orders.Count);
        Assert.Equal(source.Orders[0].Status, replayed.Orders[0].Status);
    }

    [Fact]
    public void CancelReasonReplaysIntoTheFailureHistory()
    {
        var source = NewFirstLevelSystem();
        FeasibilityResultData? feasible = null;
        for (var guard = 0; guard < 40 && feasible is not { Feasible: true }; guard++)
        {
            feasible = source.CheckFeasibility(0);
            if (feasible is not { Feasible: true }) source.Tick();
        }
        Assert.True(feasible is { Feasible: true }, "用例前提：第一关订单最终应当可行");
        var started = source.StartOrder(0);
        Assert.True(started.Started, started.Error);
        var cancelled = source.CancelProcess(started.ProcessId!.Value, "玩家主动撤销");
        Assert.True(cancelled.Cancelled, cancelled.Error);

        var originalReason = Assert.IsType<string>(source.EventHistory.Last()["reason"]);
        var replayed = Replay(source, FirstLevelWorld());
        var replayedReason = Assert.IsType<string>(replayed.EventHistory.Last()["reason"]);

        Assert.Equal("玩家主动撤销", originalReason);
        Assert.Equal(originalReason, replayedReason);
    }

    [Fact]
    public void ReplayLeavesTheTargetInLiveMode()
    {
        var source = NewSystem();
        Assert.True(source.CreateOrderFromTemplate("空双罐").Success);
        var target = NewSystem();

        SessionReplay.Into(target, source.CommandLog);

        Assert.False(target.Replaying);
    }

    [Fact]
    public void RejectedDeleteAndCancelDoNotEnterTheCommandLog()
    {
        var source = NewFirstLevelSystem();
        FeasibilityResultData? feasible = null;
        for (var guard = 0; guard < 40 && feasible is not { Feasible: true }; guard++)
        {
            feasible = source.CheckFeasibility(0);
            if (feasible is not { Feasible: true }) source.Tick();
        }
        Assert.True(feasible is { Feasible: true }, "用例前提：第一关订单最终应当可行");
        Assert.True(source.StartOrder(0).Started);

        var before = source.CommandLog.Count;
        var deleted = source.DeleteOrder(0);
        var cancel = source.CancelProcess(int.MaxValue, "不存在的过程");
        _ = source.ExecuteOrder(int.MaxValue);
        _ = source.StartOrder(int.MaxValue);
        _ = source.CheckFeasibility(int.MaxValue);
        _ = source.SetMaster(int.MaxValue);
        _ = source.SetSlave(int.MaxValue);
        _ = source.UpdateWorkshopCapacity(int.MaxValue, 1, 1);

        Assert.False(deleted.Success);
        Assert.Contains("正在执行", deleted.Error ?? "");
        Assert.Single(source.Orders);
        Assert.False(cancel.Cancelled);
        Assert.Equal(before, source.CommandLog.Count);
    }

    private static JarSystem.Core.JarSystem NewFirstLevelSystem()
    {
        var sys = NewSystem(FirstLevelWorld(), seed: 41);
        // 含真实改形步骤，因此计划不可直接交付，能形成可取消/可超时的异步过程。
        Assert.True(sys.CreateOrderFromTemplate(sys.OrderTemplates.Names.Last()).Success);
        sys.SetMasterIndex(0);
        return sys;
    }

    private static WorldOptions FirstLevelWorld()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JarSystem.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var file = Path.Combine(dir!.FullName, "config", "levels", "01_学会改形.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var world = WorldOptionsJson.FromJson(doc.RootElement.GetProperty("world").GetRawText());
        world.TurnLimit = doc.RootElement.GetProperty("turns").GetInt32();
        world.GoalOrders = doc.RootElement.GetProperty("goal").GetInt32();
        return world;
    }
}

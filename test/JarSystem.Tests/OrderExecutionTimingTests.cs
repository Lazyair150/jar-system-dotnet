using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Process;
using JarSystem.Core.Services;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 交付是原子事实；只有实现动作占用过程单位。
///
/// - 已经满足交付形状与位置时，`MATCH` 不是 tick 步骤；
/// - A 类已经可直接交付时，开始订单即完成；
/// - B 类多目标从主台迁移到从台，是**一个**过程单位，迁移完成后一次性交付。
/// </summary>
public class OrderExecutionTimingTests
{
    private static JarSystem.Core.JarSystem NewSystem()
    {
        var system = new JarSystem.Core.JarSystem(
            randomSeed: 53,
            jarIds: new SequentialIdGenerator(),
            orderIds: new SequentialIdGenerator());
        system.ClearWorkshops();
        system.ClearOrders();
        return system;
    }

    private static Workshop Bench(string name, params string[] baseMarks) =>
        new(name, baseMarks.ToList(), [], [], maxJars: 20, maxLiquids: 30,
            vocabulary: Vocabulary.Default);

    [Fact]
    public void AnAlreadyDeliverableAOrderCompletesWhenStarted()
    {
        var system = NewSystem();
        var master = Bench("主台", "圆罐", "方罐");
        master.AddJar(new Jar("圆罐", id: 1, vocabulary: Vocabulary.Default));
        system.AddWorkshop(master);
        system.SetMasterIndex(0);
        Assert.True(system.CreateOrder(
            [new Requirement { Shape = "圆罐" }], Constants.OrderType.A).Success);
        Assert.True(system.CheckFeasibility(0)?.Feasible);

        var started = system.StartOrder(0);

        Assert.True(started.Started, started.Error);
        Assert.Equal(Constants.OrderStatus.SUCCESS, system.Orders[0].Status);
        Assert.Empty(system.Processes.ActiveProcesses);
        Assert.Empty(master.Jars);
    }

    [Fact]
    public void DirectMatchesDoNotConsumeTicksWhenOneRealOperationIsRequired()
    {
        var system = NewSystem();
        var master = Bench("主台", "圆罐", "方罐");
        master.AddJar(new Jar("圆罐", id: 1, vocabulary: Vocabulary.Default));
        master.AddJar(new Jar("方罐", id: 2, vocabulary: Vocabulary.Default));
        master.AddLiquid("蜜", 1);
        system.AddWorkshop(master);
        system.SetMasterIndex(0);
        Assert.True(system.CreateOrder(
            [
                new Requirement { Shape = "圆罐" },
                new Requirement { Shape = "圆罐" }
            ], Constants.OrderType.A).Success);

        // 第二个空圆罐只能由方罐空罐改形得到；第一个圆罐已经可直接交付。
        var feasibility = system.CheckFeasibility(0);
        Assert.True(feasibility?.Feasible, feasibility?.Reason);
        Assert.Equal(1, feasibility!.DirectCount);
        Assert.Equal(1, feasibility.OperationCount);

        Assert.True(system.StartOrder(0).Started);
        system.Tick(1);

        Assert.Equal(Constants.OrderStatus.SUCCESS, system.Orders[0].Status);
        var process = Assert.Single(
            system.Processes.RecentProcesses,
            p => p.Kind == ProcessKinds.OrderExecution);
        Assert.Equal(1, process.AppliedSteps);
        Assert.DoesNotContain(
            system.Journal.Entries,
            entry => entry.Operation == "direct_match");
    }

    [Fact]
    public void BOrderTransfersAllTargetsAsOneUnitThenDeliversAtomically()
    {
        var system = NewSystem();
        var master = Bench("主台", "圆罐", "方罐");
        var slave = Bench("从台", "圆罐", "方罐");
        master.AddJar(new Jar("圆罐", id: 1, vocabulary: Vocabulary.Default));
        master.AddJar(new Jar("方罐", id: 2, vocabulary: Vocabulary.Default));
        system.AddWorkshop(master);
        system.AddWorkshop(slave);
        system.SetMasterIndex(0);
        system.SetSlaveIndex(1);
        Assert.True(system.CreateOrder(
            [
                new Requirement { Shape = "圆罐" },
                new Requirement { Shape = "方罐" }
            ], Constants.OrderType.B).Success);

        var feasibility = system.CheckFeasibility(0);
        Assert.True(feasibility?.Feasible, feasibility?.Reason);
        Assert.Equal(2, feasibility!.DirectCount);
        Assert.Equal(0, feasibility.OperationCount);

        Assert.True(system.StartOrder(0).Started);
        Assert.Equal(2, master.Jars.Count);
        Assert.Empty(slave.Jars);

        // 开始订单本身不迁移；迁移是一个 tick 单位，并在该单位结束时一次性交付。
        system.Tick(1);

        Assert.Equal(Constants.OrderStatus.SUCCESS, system.Orders[0].Status);
        Assert.Empty(master.Jars);
        Assert.Empty(slave.Jars);
        var process = Assert.Single(
            system.Processes.RecentProcesses,
            p => p.Kind == ProcessKinds.OrderExecution);
        Assert.Equal(1, process.AdvancedUnits);
        Assert.DoesNotContain(
            system.Journal.Entries,
            entry => entry.Operation == "direct_match");
    }

    [Fact]
    public void ProcessProjectionSeparatesNextUnitDelayFromDeadlineRemaining()
    {
        var system = NewSystem();
        var configuredMaster = new Workshop(
            "主台", ["方罐"], ["三角罐"], [], maxJars: 20, maxLiquids: 30,
            vocabulary: Vocabulary.Default);
        configuredMaster.AddJar(new Jar("方罐", id: 1, vocabulary: Vocabulary.Default));
        configuredMaster.AddJar(new Jar("方罐", id: 2, vocabulary: Vocabulary.Default));
        configuredMaster.AddLiquid("蜜", 2);
        system.AddWorkshop(configuredMaster);
        system.SetMasterIndex(0);
        Assert.True(system.CreateOrder(
            [
                new Requirement { Shape = "三角罐" },
                new Requirement { Shape = "三角罐" }
            ], Constants.OrderType.A).Success);
        Assert.True(system.CheckFeasibility(0)?.Feasible);

        Assert.True(system.StartOrder(0, deadlineTicks: 10).Started);
        system.Tick(1);

        var process = Assert.Single(system.GetState().Processes);
        Assert.Equal(1, process.TicksUntilNextUnit);
        Assert.Equal(9, process.RemainingTicks);
    }

    [Fact]
    public void OneAdvanceExecutesTheNextImplementationUnit()
    {
        var system = NewSystem();
        var master = new Workshop(
            "主台", ["方罐"], ["三角罐"], [], maxJars: 20, maxLiquids: 30,
            vocabulary: Vocabulary.Default);
        master.AddJar(new Jar("方罐", id: 1, vocabulary: Vocabulary.Default));
        master.AddJar(new Jar("方罐", id: 2, vocabulary: Vocabulary.Default));
        master.AddLiquid("蜜", 2);
        system.AddWorkshop(master);
        system.SetMasterIndex(0);
        Assert.True(system.CreateOrder(
            [
                new Requirement { Shape = "三角罐" },
                new Requirement { Shape = "三角罐" }
            ], Constants.OrderType.A).Success);
        Assert.True(system.CheckFeasibility(0)?.Feasible);
        Assert.True(system.StartOrder(0).Started);

        system.Tick(1);
        var active = Assert.Single(system.Processes.ActiveProcesses);
        Assert.Equal(1, active.AppliedSteps);

        system.Tick(1);
        Assert.Equal(Constants.OrderStatus.SUCCESS, system.Orders[0].Status);
        Assert.Empty(system.Processes.ActiveProcesses);
        Assert.Equal(2, system.Processes.RecentProcesses
            .Single(process => process.Kind == ProcessKinds.OrderExecution).AppliedSteps);
    }
}

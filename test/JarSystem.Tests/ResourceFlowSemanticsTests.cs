using System;
using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 工作台的接收方法只是容量原语；真正的策略由调用方决定。
/// 本组固定“从台回滚先于仓库流入”和“换台/回滚溢出必须进入丢弃账目”。
/// </summary>
public class ResourceFlowSemanticsTests
{
    private static Workshop Bench(string name, int maxJars = 6, int maxLiquids = 5) =>
        new(name, ["圆罐", "方罐"], [], [], maxJars: maxJars, maxLiquids: maxLiquids,
            vocabulary: Vocabulary.Default);

    [Fact]
    public void RolledBackResourcesOccupyCapacityBeforeWarehouseFlow()
    {
        var master = Bench("主台", maxJars: 1);
        var slave = Bench("从台");
        var returned = new Jar("圆罐", id: 7, vocabulary: Vocabulary.Default);
        slave.AddJar(returned);
        var warehouse = new Warehouse(Vocabulary.Default);
        warehouse.StoreJars([new Jar("方罐", id: 8, vocabulary: Vocabulary.Default)]);

        new SlaveRollback().Execute(master, slave, _ => { });
        warehouse.FlowToMaster(master, jarCount: 1, liquidAmount: 0);

        Assert.Single(master.Jars);
        Assert.Equal(7, master.Jars[0].Id);
        Assert.Single(warehouse.Jars);
        Assert.Equal(8, warehouse.Jars[0].Id);
    }

    [Fact]
    public void SwitchOverflowEntersTheDiscardLedger()
    {
        var source = Bench("旧主台");
        var target = Bench("窄目标台", maxJars: 1, maxLiquids: 5);
        source.AddJar(new Jar("圆罐", id: 101, vocabulary: Vocabulary.Default));
        source.AddJar(new Jar("方罐", id: 102, vocabulary: Vocabulary.Default));
        var discarded = 0;

        var result = new WorkshopSwitchEvent(source, target,
            countDiscardedJars: count => discarded += count).Execute();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, discarded);
    }

    [Fact]
    public void SwitchLiquidOverflowEntersTheDiscardLedger()
    {
        var source = Bench("旧主台");
        var target = Bench("窄目标台", maxJars: 6, maxLiquids: 1);
        source.AddLiquid("蜜", 2);
        var discarded = 0;

        var result = new WorkshopSwitchEvent(source, target,
            countDiscardedLiquids: (_, amount) => discarded += amount).Execute();

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, discarded);
    }

    [Fact]
    public void WarehouseIntakeIsPausedWhileTheMasterIsMigrating()
    {
        var world = WorldOptions.Default;
        world.Producers =
        [
            new ProducerOptions
            {
                Name = "停线",
                Rates = world.Shapes.ToDictionary(shape => shape, _ => 0.0)
            }
        ];
        world.InitialStock.Clear();
        world.Flow = new FlowOptions
        {
            JarCount = 3,
            LiquidAmount = 0,
            LiquidRate = world.Liquids.ToDictionary(liquid => liquid, _ => 0.0)
        };
        world.Workshops =
        [
            new WorkshopOptions { Name = "主台", BaseMarks = ["圆罐"] },
            new WorkshopOptions { Name = "目标台", BaseMarks = ["圆罐"] }
        ];
        var sys = new JarSystem.Core.JarSystem(randomSeed: 29, world: world);
        sys.SetMasterIndex(0);
        sys.Workshops[0].AddJar(new Jar("圆罐", id: 1, vocabulary: Vocabulary.Default));
        var incoming = new Jar("高罐", id: 77, vocabulary: Vocabulary.Default);
        sys.ProducerModule.Warehouse.StoreJars([incoming]);

        var started = sys.BeginSwitch(1);
        Assert.True(started.Started, started.Error);

        sys.Tick();

        Assert.Contains(sys.ProducerModule.Warehouse.Jars, jar => jar.Id == 77);
        Assert.DoesNotContain(sys.Workshops[0].Jars, jar => jar.Id == 77);
    }

    [Fact]
    public void BOrderReturnsLiquidProducedByAToolJar()
    {
        var master = new Workshop(
            "主台", ["圆罐"], [], [], maxJars: 6, maxLiquids: 5,
            vocabulary: Vocabulary.Default);
        var slave = new Workshop(
            "从台", ["圆罐"], [], ["蜜"], maxJars: 6, maxLiquids: 5,
            vocabulary: Vocabulary.Default);
        master.AddJar(new Jar("圆罐", "酒", id: 1, vocabulary: Vocabulary.Default));

        var system = new JarSystem.Core.JarSystem(randomSeed: 31);
        system.ClearWorkshops();
        system.ClearOrders();
        system.AddWorkshop(master);
        system.AddWorkshop(slave);
        system.SetMasterIndex(0);
        system.SetSlaveIndex(1);
        Assert.True(system.CreateOrder(
            [new Requirement { Shape = "圆罐" }], Constants.OrderType.B).Success);

        var feasibility = system.CheckFeasibility(0);
        var result = system.ExecuteOrder(0);

        Assert.True(feasibility?.Feasible, feasibility?.Reason);
        Assert.True(result.Success, result.Reason);
        Assert.Empty(slave.Jars);
        Assert.All(slave.LiquidStorage.Values, amount => Assert.Equal(0, amount));
        Assert.Equal(1, master.LiquidStorage["酒"]);
    }

    [Fact]
    public void AnOccupiedWorkshopCannotBeSelectedAsTheTemporarySlave()
    {
        var system = new JarSystem.Core.JarSystem(randomSeed: 37);
        system.SetMasterIndex(0);
        system.Workshops[1].AddLiquid("蜜", 1);

        var error = Assert.Throws<DomainValidationException>(() => system.SetSlaveIndex(1));
        var commandResult = system.SetSlave(1);

        Assert.Contains("空", error.Message);
        Assert.False(commandResult["success"] is true);
        Assert.Contains("空", commandResult["reason"] as string);
        Assert.Null(system.CurrentSlave);
    }

    [Fact]
    public void MasterAndSlavePositionsAreFrozenDuringAnActiveOrder()
    {
        var system = new JarSystem.Core.JarSystem(randomSeed: 41);
        system.SetMasterIndex(0);
        Assert.True(system.CreateOrder(
            [new Requirement { Shape = "圆罐" }], Constants.OrderType.A).Success);
        var started = system.StartOrder(0);
        Assert.True(started.Started, started.Error);

        var setMaster = system.SetMaster(1);
        var setSlave = system.SetSlave(1);

        Assert.False(setMaster["success"] is true);
        Assert.False(setSlave["success"] is true);
        Assert.Throws<DomainValidationException>(() => system.SetMasterIndex(1));
        Assert.Throws<DomainValidationException>(() => system.SetSlaveIndex(1));
        Assert.Equal((int?)0, system.CurrentMaster);
        Assert.Null(system.CurrentSlave);
    }
}

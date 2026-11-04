using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 生产卡背后的真实实体与状态投影：形状线、生产源、液体自产、仓库库存和流入参数
/// 是四个不同的概念。页面必须拿到完整投影，才能解释“速率为 0 为什么仍有东西进主台”。
/// </summary>
public class ProductionStatusProjectionTests
{
    private static JarSystem.Core.JarSystem NewFirstLevel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JarSystem.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var file = Path.Combine(dir!.FullName, "config", "levels", "01_学会改形.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var world = WorldOptionsJson.FromJson(doc.RootElement.GetProperty("world").GetRawText());
        world.TurnLimit = doc.RootElement.GetProperty("turns").GetInt32();
        world.GoalOrders = doc.RootElement.GetProperty("goal").GetInt32();
        var sys = new JarSystem.Core.JarSystem(
            randomSeed: 17, world: world,
            jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());
        sys.SetMasterIndex(0);
        return sys;
    }

    [Fact]
    public void SnapshotSeparatesShapeLinesSlotsAndFlow()
    {
        var status = NewFirstLevel().GetState().ProducerStatus;

        Assert.Equal(2, status.Slots.Used);
        Assert.Equal(3, status.Slots.Limit);
        Assert.Equal(3, status.Flow.Jars);
        Assert.Equal(2, status.Flow.LiquidPerLiquid);

        Assert.Single(status.Producers);
        Assert.Equal(8, status.Generators.Count);
        Assert.Equal(2, status.Generators.Count(g => g.Rate > 0));
        Assert.All(status.LiquidRates.Values, rate => Assert.Equal(0, rate));
    }

    [Fact]
    public void ZeroLiquidProductionCanStillFlowInitialWarehouseStock()
    {
        var sys = NewFirstLevel();
        var before = sys.GetState();
        var initialHoney = before.Warehouse.Liquids["蜜"];
        Assert.True(initialHoney > 0);

        sys.Tick();

        var after = sys.GetState();
        Assert.Equal(0, after.ProducerStatus.LiquidRates["蜜"]);
        Assert.True(after.Workshops[0].Liquids["蜜"] > 0);
        Assert.True(after.Warehouse.Liquids["蜜"] < initialHoney);
    }
}

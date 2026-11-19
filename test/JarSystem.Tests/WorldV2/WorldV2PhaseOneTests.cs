using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldClockTests
{
    [Fact]
    public void SpeedRatioConvertsWorldTimeToLogicalTime()
    {
        var clock = new WorldClock();
        clock.SetSpeedRatio(1.5);

        var logicalDelta = clock.Advance(2_000);

        Assert.Equal(3_000, logicalDelta);
        Assert.Equal(2_000, clock.WorldTimeMilli);
        Assert.Equal(3_000, clock.LogicalTimeMilli);
    }

    [Fact]
    public void SplitAdvancesKeepTheSameFixedPointResult()
    {
        var oneShot = new WorldClock();
        var split = new WorldClock();
        oneShot.SetSpeedRatio(1.333);
        split.SetSpeedRatio(1.333);

        oneShot.Advance(3_000);
        split.Advance(1_000);
        split.Advance(2_000);

        Assert.Equal(oneShot.WorldTimeMilli, split.WorldTimeMilli);
        Assert.Equal(oneShot.LogicalTimeMilli, split.LogicalTimeMilli);
    }

    [Fact]
    public void PauseStopsBothWorldAndLogicalTime()
    {
        var clock = new WorldClock();
        clock.Advance(1_000);
        clock.Pause();

        Assert.Equal(0, clock.Advance(5_000));
        Assert.Equal(1_000, clock.WorldTimeMilli);
        Assert.Equal(1_000, clock.LogicalTimeMilli);

        clock.Resume();
        Assert.Equal(1_000, clock.Advance(1_000));
    }

    [Fact]
    public void ZeroSpeedStillTracksWorldTimeButDoesNotAdvanceLogicalTime()
    {
        var clock = new WorldClock();
        clock.SetSpeedRatio(0);

        Assert.Equal(0, clock.Advance(2_000));
        Assert.Equal(2_000, clock.WorldTimeMilli);
        Assert.Equal(0, clock.LogicalTimeMilli);
    }
}

public sealed class ResourceWorldRuntimeTests
{
    [Fact]
    public void RuntimePassesWorldSpeedIntoResourceProduction()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var producers = new ProducerModule(vocabulary);
        producers.RegisterProducer("测试生产器", new Dictionary<string, double> { ["圆罐"] = 1 });
        var liquids = new LiquidProduction(
            vocabulary,
            defaults: new Dictionary<string, double> { ["酒"] = 1, ["蜜"] = 0 });
        var area = new ResourceArea(producers, liquids, jarFlowRate: 0, liquidFlowRate: 0);
        var runtime = new ResourceWorldRuntime(area);
        runtime.Clock.SetSpeedRatio(2);

        var step = runtime.AdvanceWorldTime(500, master: null);

        Assert.Equal(1_000, step.LogicalTimeDeltaMilli);
        Assert.Equal(1, step.Resources.ProducedJarCount);
        Assert.Equal(1, step.Resources.ProducedLiquids.Single(x => x.Liquid == "酒").Amount);
        Assert.Equal(500, runtime.Clock.WorldTimeMilli);
        Assert.Equal(1_000, runtime.Clock.LogicalTimeMilli);
    }
}

public sealed class ResourceAreaTests
{
    [Fact]
    public void ProductionAndFlowUseLogicalTimeAndKeepJarAndLiquidResourcesDistinct()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var producers = new ProducerModule(vocabulary);
        producers.RegisterProducer("测试生产器", new Dictionary<string, double> { ["圆罐"] = 0.5 });

        var liquidProduction = new LiquidProduction(
            vocabulary,
            defaults: new Dictionary<string, double> { ["酒"] = 0.5, ["蜜"] = 0 });
        var area = new ResourceArea(producers, liquidProduction, jarFlowRate: 1, liquidFlowRate: 1);
        var master = new Workshop(
            "主台",
            baseMarks: new List<string> { "圆罐" },
            vocabulary: vocabulary);

        var result = area.Advance(2_000, master);

        Assert.Equal(1, result.ProducedJarCount);
        Assert.Equal(1, result.FlowedJarCount);
        Assert.Equal(1, result.ProducedLiquids.Single(x => x.Liquid == "酒").Amount);
        Assert.Equal(1, result.FlowedLiquids["酒"]);
        Assert.Single(master.Jars);
        Assert.Equal(1, master.LiquidStorage["酒"]);
        Assert.Empty(area.Inventory.Jars);
        Assert.Equal(0, area.Inventory.LiquidCount("酒"));
    }

    [Fact]
    public void NoMasterLeavesProducedResourcesInTheResourceArea()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var producers = new ProducerModule(vocabulary);
        producers.RegisterProducer("测试生产器", new Dictionary<string, double> { ["圆罐"] = 1 });
        var liquids = new LiquidProduction(
            vocabulary,
            defaults: new Dictionary<string, double> { ["酒"] = 1, ["蜜"] = 0 });
        var area = new ResourceArea(producers, liquids, jarFlowRate: 10, liquidFlowRate: 10);

        area.Advance(1_000, master: null);

        Assert.Single(area.Inventory.Jars);
        Assert.Equal(1, area.Inventory.LiquidCount("酒"));
    }
}

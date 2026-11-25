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
    public void OverflowDoesNotPartiallyAdvanceTheClock()
    {
        var clock = new WorldClock();

        Assert.Throws<OverflowException>(() => clock.Advance(long.MaxValue));

        Assert.Equal(0, clock.WorldTimeMilli);
        Assert.Equal(0, clock.LogicalTimeMilli);
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

    [Fact]
    public void LiquidProductionAndDiscardFactsCarryCanonicalVocabularyIdentity()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var producers = new ProducerModule(vocabulary);
        var liquids = new LiquidProduction(
            vocabulary,
            defaults: new Dictionary<string, double> { ["酒"] = 2, ["蜜"] = 0 });
        var ship = new ShipLiquidInventory(capacity: 1);
        var area = new ResourceArea(
            producers, liquids, jarFlowRate: 0, liquidFlowRate: 0,
            shipLiquidInventory: ship);
        var ledger = new ResourceLedger();
        var runtime = new ResourceWorldRuntime(area, resourceLedger: ledger);

        runtime.AdvanceWorldTime(1_000, master: null);

        var production = Assert.Single(ledger.For(ResourceCauses.Production));
        var discarded = Assert.Single(ledger.For(ResourceCauses.Discard));
        Assert.Equal(ResourceKinds.Liquid, production.Kind);
        Assert.Equal(ResourceKinds.Liquid, discarded.Kind);
        Assert.Equal("酒", production.ResourceIdentity);
        Assert.Equal("酒", discarded.ResourceIdentity);
        Assert.Equal(1, production.Amount);
        Assert.Equal(1, discarded.Amount);
    }

}

    [Fact]
    public void JarProductionAndWarehouseFlowFactsCarryTheSameCanonicalJarId()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var producers = new ProducerModule(vocabulary);
        producers.RegisterProducer("测试生产器", new Dictionary<string, double> { ["圆罐"] = 1 });
        var liquids = new LiquidProduction(
            vocabulary,
            defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var area = new ResourceArea(producers, liquids, jarFlowRate: 1, liquidFlowRate: 0);
        var master = new Workshop(
            "主台", baseMarks: new List<string> { "圆罐" }, vocabulary: vocabulary,
            ownedInstanceId: "owned.resource-flow.master");
        var ledger = new ResourceLedger();
        var runtime = new ResourceWorldRuntime(area, resourceLedger: ledger);

        runtime.AdvanceWorldTime(1_000, master);

        var jar = Assert.Single(master.Jars);
        var produced = Assert.Single(ledger.For(ResourceCauses.Production),
            item => item.Kind == ResourceKinds.Jar);
        var flowed = Assert.Single(ledger.For(ResourceCauses.Flow),
            item => item.Kind == ResourceKinds.Jar);
        var jarId = jar.Id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(jarId, produced.ResourceIdentity);
        Assert.Equal(jarId, flowed.ResourceIdentity);
        Assert.Equal(1, produced.Amount);
        Assert.Equal(1, flowed.Amount);
        Assert.Equal("圆罐", produced.Name);
        Assert.Equal("圆罐", flowed.Name);
        Assert.Equal(new RuleResourceAccount("workshop.jar-storage", "owned.resource-flow.master").AccountId,
            flowed.ToAccountId);
    }

    [Fact]
    public void OverflowJarLedgerFactsRetainIdentityAfterTheEntityIsDiscarded()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var producers = new ProducerModule(vocabulary);
        producers.RegisterProducer("测试生产器", new Dictionary<string, double> { ["圆罐"] = 1 });
        var liquids = new LiquidProduction(
            vocabulary,
            defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var area = new ResourceArea(producers, liquids, jarFlowRate: 0, liquidFlowRate: 0);
        var existing = new List<Jar>();
        for (var i = 0; i < Constants.WAREHOUSE_MAX_JARS; i++)
            existing.Add(new Jar("圆罐", vocabulary: vocabulary));
        area.Inventory.StoreJars(existing);
        var ledger = new ResourceLedger();
        var runtime = new ResourceWorldRuntime(area, resourceLedger: ledger);

        runtime.AdvanceWorldTime(1_000, master: null);

        var produced = Assert.Single(ledger.For(ResourceCauses.Production),
            item => item.Kind == ResourceKinds.Jar);
        var discarded = Assert.Single(ledger.For(ResourceCauses.Discard),
            item => item.Kind == ResourceKinds.Jar);
        Assert.Equal(produced.ResourceIdentity, discarded.ResourceIdentity);
        Assert.True(int.TryParse(discarded.ResourceIdentity, out var jarId) && jarId > 0);
        Assert.Equal(1, produced.Amount);
        Assert.Equal(1, discarded.Amount);
        Assert.Equal(Constants.WAREHOUSE_MAX_JARS, area.Inventory.JarCount);
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
    public void ProductionIsInvariantToHowLogicalTimeIsPartitioned()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });

        var oneShotProducer = new ProducerModule(vocabulary);
        oneShotProducer.RegisterProducer("P", new Dictionary<string, double> { ["圆罐"] = 0.3 });
        var splitProducer = new ProducerModule(vocabulary);
        splitProducer.RegisterProducer("P", new Dictionary<string, double> { ["圆罐"] = 0.3 });

        var oneShotLiquids = new LiquidProduction(
            vocabulary, defaults: new Dictionary<string, double> { ["酒"] = 0.3, ["蜜"] = 0 });
        var splitLiquids = new LiquidProduction(
            vocabulary, defaults: new Dictionary<string, double> { ["酒"] = 0.3, ["蜜"] = 0 });

        oneShotProducer.AdvanceLogicalTime(10_000);
        oneShotLiquids.AdvanceLogicalTime(10_000);

        for (var i = 0; i < 10; i++)
        {
            splitProducer.AdvanceLogicalTime(1_000);
            splitLiquids.AdvanceLogicalTime(1_000);
        }

        Assert.Equal(oneShotProducer.Warehouse.JarCount, splitProducer.Warehouse.JarCount);
        Assert.Equal(oneShotLiquids.Accumulated["酒"], splitLiquids.Accumulated["酒"]);
        Assert.Equal(oneShotLiquids.Accumulated["蜜"], splitLiquids.Accumulated["蜜"]);
    }

    [Fact]
    public void FractionalFlowRateAccumulatesAcrossLogicalTimeSlices()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var producers = new ProducerModule(vocabulary);
        producers.RegisterProducer("测试生产器", new Dictionary<string, double> { ["圆罐"] = 1 });
        var liquids = new LiquidProduction(
            vocabulary,
            defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var area = new ResourceArea(producers, liquids, jarFlowRate: 0.5, liquidFlowRate: 0);
        var master = new Workshop("主台", baseMarks: new List<string> { "圆罐" }, vocabulary: vocabulary);

        var first = area.Advance(1_000, master);
        var second = area.Advance(1_000, master);

        Assert.Equal(0, first.FlowedJarCount);
        Assert.Equal(1, second.FlowedJarCount);
        Assert.Single(master.Jars);
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

using System;
using System.Linq;
using JarSystem.Core;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **液体也是资源**：它有速率、有累加器、能被玩家调，和罐子一样。
///
/// 与罐子的差别只在"谁产"：罐子的产能属于**生产者**（按形状、有产线槽上限），
/// 液体的产能属于**世界自己**（按品种、没有机器）。但"每 tick 产多少"必须是同一套：
/// 千分之一定点累加 ⇒ 0.5 份/tick 就是两 tick 产一份，且逐 tick 结果与平台无关。
/// </summary>
public sealed class LiquidProductionRateTests
{
    private static JarSystem.Core.JarSystem NewSystem() => new(randomSeed: 7, world: WorldOptions.Default);

    private static int WarehouseLiquid(JarSystem.Core.JarSystem sys, string liquid)
        => sys.ProducerModule.Warehouse.LiquidWarehouse.GetValueOrDefault(liquid);

    private static string FirstLiquid(JarSystem.Core.JarSystem sys) => sys.Vocabulary.Liquids[0];

    [Fact]
    public void AnIntegerRateProducesThatManyEachTick()
    {
        var sys = NewSystem();
        var liquid = FirstLiquid(sys);
        var before = WarehouseLiquid(sys, liquid);

        sys.SetLiquidProductionRate(liquid, 3);
        sys.Tick(2);

        Assert.Equal(before + 6, WarehouseLiquid(sys, liquid));
    }

    [Fact]
    public void AFractionalRateAccumulatesAndPaysOutWholeUnits()
    {
        var sys = NewSystem();
        var liquid = FirstLiquid(sys);
        var before = WarehouseLiquid(sys, liquid);

        sys.SetLiquidProductionRate(liquid, 0.5);
        sys.Tick(1);
        Assert.Equal(before, WarehouseLiquid(sys, liquid));
        Assert.Equal(0.5, sys.LiquidProduction.Accumulated[liquid], 3);

        sys.Tick(1);
        Assert.Equal(before + 1, WarehouseLiquid(sys, liquid));
        Assert.Equal(0.0, sys.LiquidProduction.Accumulated[liquid], 3);

        sys.Tick(2);
        Assert.Equal(before + 2, WarehouseLiquid(sys, liquid));
    }

    [Fact]
    public void AFifthOfATickPaysOutOneUnitEveryFiveTicks()
    {
        var sys = NewSystem();
        var liquid = FirstLiquid(sys);
        var before = WarehouseLiquid(sys, liquid);

        sys.SetLiquidProductionRate(liquid, 0.2);
        for (int i = 0; i < 4; i++) sys.Tick(1);
        Assert.Equal(before, WarehouseLiquid(sys, liquid));
        sys.Tick(1);
        Assert.Equal(before + 1, WarehouseLiquid(sys, liquid));
    }

    [Fact]
    public void RateZeroStopsProductionEntirely()
    {
        var sys = NewSystem();
        var liquid = sys.Vocabulary.Liquids[1];
        var before = WarehouseLiquid(sys, liquid);

        sys.SetLiquidProductionRate(liquid, 0);
        sys.Tick(3);

        Assert.Equal(before, WarehouseLiquid(sys, liquid));
        Assert.Equal(0.0, sys.LiquidProduction.Accumulated[liquid]);
    }

    [Fact]
    public void TheWriteDoorRejectsUnknownLiquidNegativeAndAbsurdRates()
    {
        var sys = NewSystem();
        var liquid = FirstLiquid(sys);

        Assert.Throws<DomainValidationException>(() => sys.SetLiquidProductionRate("水", 1));
        Assert.Throws<DomainValidationException>(() => sys.SetLiquidProductionRate(liquid, -0.1));
        Assert.Throws<DomainValidationException>(
            () => sys.SetLiquidProductionRate(liquid, Constants.MAX_PRODUCER_RATE + 1));
        Assert.Throws<DomainValidationException>(() => sys.SetLiquidProductionRate(liquid, double.NaN));

        Assert.Equal(1.0, sys.LiquidProductionRate[liquid], 3);
    }

    [Fact]
    public void TheStateCarriesTheRatesAndTheAccumulators()
    {
        var sys = NewSystem();
        var liquid = FirstLiquid(sys);
        sys.SetLiquidProductionRate(liquid, 0.5);
        sys.Tick(1);

        var status = sys.GetState().ProducerStatus;

        Assert.Equal(sys.Vocabulary.Liquids, status.LiquidRates.Keys.ToList());
        Assert.Equal(sys.Vocabulary.Liquids, status.LiquidAccumulated.Keys.ToList());
        Assert.Equal(0.5, status.LiquidRates[liquid], 3);
        Assert.Equal(0.5, status.LiquidAccumulated[liquid], 3);
    }

    [Fact]
    public void OneIntentWritesOneLogEntry()
    {
        var sys = NewSystem();
        var liquid = FirstLiquid(sys);

        var before = sys.CommandLog.Count(c => c.Op == "updateLiquidProductionRate");
        sys.UpdateLiquidProductionRate(liquid, 0.4);
        var after = sys.CommandLog.Count(c => c.Op == "updateLiquidProductionRate");

        Assert.Equal(before + 1, after);
        // 回放读 doubleA：退回 intA 会让"0.4 份/tick"在回放里变成 0。
        Assert.Equal(0.4, sys.CommandLog[sys.CommandLog.Count - 1].DoubleA ?? 0, 3);
    }

    [Fact]
    public void TheWarehouseExposesEveryJarNotJustTheCount()
    {
        var sys = NewSystem();
        sys.Tick(3);

        var warehouse = sys.GetState().Warehouse;

        Assert.Equal(warehouse.Jars, warehouse.JarList.Count);
        Assert.NotEmpty(warehouse.JarList);
        Assert.All(warehouse.JarList, j => Assert.False(string.IsNullOrEmpty(j.Shape)));
        Assert.Equal(
            sys.ProducerModule.Warehouse.Jars.Select(j => j.Id).ToList(),
            warehouse.JarList.Select(j => j.Id).ToList());
    }

    [Fact]
    public void TheWarehouseListShrinksByExactlyWhatFlowedIntoTheMaster()
    {
        var sys = new JarSystem.Core.JarSystem(randomSeed: 11, world: WorldOptions.Default);
        sys.Tick(3);   // 先攒一批罐子

        // 关掉所有产线：这一 tick 仓库里"少了多少只"就只可能来自流入。
        foreach (var producer in sys.ProducerModule.Producers.Values)
            foreach (var shape in producer.Rates.Keys.ToList())
                sys.UpdateProducerRate(producer.Name, shape, 0);

        var before = sys.GetState().Warehouse;
        Assert.True(before.JarList.Count > 0, "用例前提：仓库里得有罐子");
        Assert.Equal(before.Jars, before.JarList.Count);

        var masterJarsBefore = sys.Workshops[0].Jars.Count;
        sys.SetMasterIndex(0);
        sys.Tick(1);

        var after = sys.GetState().Warehouse;
        var moved = sys.Workshops[0].Jars.Count - masterJarsBefore;

        // 恒等式：仓库少了多少只 = 主台多了多少只（产线已关，没有新罐子补进来）。
        Assert.True(moved > 0, "设了主台之后应当有罐子流进去");
        Assert.Equal(before.JarList.Count - moved, after.Jars);
        Assert.Equal(after.Jars, after.JarList.Count);
    }
}

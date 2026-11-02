using System;
using System.Linq;
using JarSystem.Core;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **液体也是资源，因此它也有生产速率**（每 tick 几份）。
///
/// 这一组测试钉住四件事：
/// 1 速率真的决定"每 tick 产多少"（不是只改一个数字）；
/// 2 速率设为 0 就停产；
/// 3 写入口的护栏只有一份：未知液体 / 负数 / 超上限一律拒绝（回放与存档是外部数据，
///   放行就能造出手工不可达的世界）；
/// 4 状态面把速率交给客户端（页面要能像产线卡一样给液体做卡）。
///
/// 罐子的产能属于**生产者**（按形状、有累积器、有产线槽上限），液体的产能属于**世界**
/// （按品种、整数份）。两者概念不同，但"每 tick 产多少、玩家能调"这件事必须一样。
/// </summary>
public sealed class LiquidProductionRateTests
{
    private static JarSystem.Core.JarSystem NewSystem()
    {
        // 词表顺序固定为 酒 → 蜜（见 Constants.LIQUIDS），这里按名字取，不按下标。
        var world = WorldOptions.Default;
        return new JarSystem.Core.JarSystem(randomSeed: 7, world: world);
    }

    private static int WarehouseLiquid(JarSystem.Core.JarSystem sys, string liquid)
        => sys.ProducerModule.Warehouse.LiquidWarehouse.GetValueOrDefault(liquid);

    [Fact]
    public void TheRateDecidesHowMuchIsProducedEachTick()
    {
        var sys = NewSystem();
        var liquid = sys.Vocabulary.Liquids[0];
        var before = WarehouseLiquid(sys, liquid);

        sys.SetLiquidProductionRate(liquid, 3);
        sys.Tick(2);

        // 两 tick × 3 份 = 6 份（默认世界里主台为空时不流入仓库，见 ProductionService.TickStep）。
        Assert.Equal(before + 6, WarehouseLiquid(sys, liquid));
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
    }

    [Fact]
    public void TheWriteDoorRejectsUnknownLiquidNegativeAndAbsurdRates()
    {
        var sys = NewSystem();
        var liquid = sys.Vocabulary.Liquids[0];

        // 未知液体：绝不允许被插入成"幻影液体"（它会进产出循环 ⇒ 手工不可达的世界）。
        Assert.Throws<DomainValidationException>(() => sys.SetLiquidProductionRate("水", 1));
        Assert.Throws<DomainValidationException>(() => sys.SetLiquidProductionRate(liquid, -1));
        Assert.Throws<DomainValidationException>(
            () => sys.SetLiquidProductionRate(liquid, (int)Constants.MAX_PRODUCER_RATE + 1));

        // 被拒之后世界一点没被改动。
        Assert.Equal(1, sys.LiquidProductionRate[liquid]);
    }

    [Fact]
    public void TheStateCarriesTheLiquidRates()
    {
        var sys = NewSystem();
        var liquid = sys.Vocabulary.Liquids[0];
        sys.SetLiquidProductionRate(liquid, 2);

        var status = sys.GetState().ProducerStatus;

        // 键序 = 词表顺序：契约快照按行比较，顺序变了就是一次显式重基线。
        Assert.Equal(sys.Vocabulary.Liquids, status.LiquidRates.Keys.ToList());
        Assert.Equal(2, status.LiquidRates[liquid]);
    }

    [Fact]
    public void OneIntentWritesOneLogEntry()
    {
        var sys = NewSystem();
        var liquid = sys.Vocabulary.Liquids[0];

        var before = sys.CommandLog.Count(c => c.Op == "updateLiquidProductionRate");
        sys.UpdateLiquidProductionRate(liquid, 2);
        var after = sys.CommandLog.Count(c => c.Op == "updateLiquidProductionRate");

        // 转发的服务层不许再记一条：同一次意图记两条，回放会把日志越滚越长。
        Assert.Equal(before + 1, after);
    }
}

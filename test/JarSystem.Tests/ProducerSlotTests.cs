using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 产线卡槽上限（<see cref="WorldOptions.ProducerSlots"/>）。
///
/// 这条规则的归属问题值得说清楚：**卡槽上限是玩法规则，不是页面限制**。
/// 如果只写在 `world.js` 里，别的客户端（脚本、Godot、回放器）可以完全绕过它，
/// 而 `REPLAY-OK` 也不约束"玩家点了不该点的按钮"。所以校验住在
/// <see cref="ProducerModule.UpdateRate"/> —— 数据在这里，规则就在这里。
///
/// 一"条"产线 = 一个 (生产者, 形状) 且速率 &gt; 0。空槽合法（速率 0 的条目不算占用）。
/// **默认 0 = 不限**：出厂沙盒不该被某一关的规则约束；关卡文件显式写上限（关卡化 S1/S5）。
/// </summary>
public class ProducerSlotTests
{
    [Fact]
    public void EnablingAPreviouslyUnlistedShapeAlsoAttachesTheProductionSource()
    {
        var module = Module(2);
        module.RegisterProducer("甲", new Dictionary<string, double> { ["圆罐"] = 0.4 });

        module.UpdateRate("甲", "方罐", 1.0);

        Assert.Contains(module.Generators["方罐"].Producers,
            source => source.Name == "甲" && Math.Abs(source.Rate - 1.0) < 1e-9);
        module.Tick();
        Assert.Contains(module.Warehouse.Jars, jar => jar.Shape == "方罐");
        Assert.Equal(2, module.UsedSlots);
    }

    private static ProducerModule Module(int slots)
    {
        var module = new ProducerModule(new Vocabulary(new[] { "圆罐", "方罐", "高罐" }, new[] { "酒", "蜜" },
            new Dictionary<string, LiquidPolarity>
            {
                ["酒"] = LiquidPolarity.Cathode,
                ["蜜"] = LiquidPolarity.Anode
            }))
        {
            ProducerSlots = slots
        };
        return module;
    }

    [Fact]
    public void TheShippedWorldIsNotSlotLimited()
    {
        // 出厂世界是沙盒：4 个生产者、8 条活跃产线，若默认给 3 条上限，世界一开局就自相矛盾。
        var sys = new JarSystem.Core.JarSystem(randomSeed: 1);
        Assert.Equal(0, sys.World.ProducerSlots);
        Assert.Equal(0, sys.ProducerModule.ProducerSlots);
    }

    [Fact]
    public void UsedSlotsCountsOnlyActivePairs()
    {
        var m = Module(0);
        m.RegisterProducer("甲", new Dictionary<string, double> { ["圆罐"] = 0.4, ["方罐"] = 0.0 });
        Assert.Equal(1, m.UsedSlots);
        m.UpdateRate("甲", "方罐", 0.3);
        Assert.Equal(2, m.UsedSlots);
        m.UpdateRate("甲", "方罐", 0.0);
        Assert.Equal(1, m.UsedSlots);
    }

    [Fact]
    public void OpeningALineBeyondTheCapIsRejected()
    {
        var m = Module(2);
        m.RegisterProducer("甲", new Dictionary<string, double> { ["圆罐"] = 0.4 });
        m.UpdateRate("甲", "方罐", 0.3);            // 第 2 条：正好到顶

        var ex = Assert.Throws<DomainValidationException>(() => m.UpdateRate("甲", "高罐", 0.5));
        Assert.Contains("产线槽已满", ex.Message);
        Assert.Contains("2", ex.Message);
        Assert.Equal(2, m.UsedSlots);               // 被拒之后世界没变
        Assert.False(m.Producers["甲"].Rates.TryGetValue("高罐", out var opened) && opened > 0,
            "被拒绝的开线不该在世界里留下任何痕迹");
    }

    [Fact]
    public void ChangingAnEquippedLineDoesNotConsumeANewSlot()
    {
        var m = Module(2);
        m.RegisterProducer("甲", new Dictionary<string, double> { ["圆罐"] = 0.4, ["方罐"] = 0.3 });
        Assert.Equal(2, m.UsedSlots);

        // 已装备的那条调速率：不占新槽，也不该被上限拒绝。
        m.UpdateRate("甲", "圆罐", 0.9);
        Assert.Equal(2, m.UsedSlots);
        Assert.Equal(0.9, m.Producers["甲"].Rates["圆罐"]);
    }

    [Fact]
    public void LoweringALineToZeroFreesASlot()
    {
        var m = Module(2);
        m.RegisterProducer("甲", new Dictionary<string, double> { ["圆罐"] = 0.4, ["方罐"] = 0.3 });
        m.UpdateRate("甲", "圆罐", 0.0);            // 空出一条
        Assert.Equal(1, m.UsedSlots);

        m.UpdateRate("甲", "高罐", 0.5);            // 新开一条：应当允许
        Assert.Equal(2, m.UsedSlots);
    }

    [Fact]
    public void AssemblyIgnoresTheCapBecauseTheWorldFileIsTheAuthorsIntent()
    {
        // 装配期（RegisterProducer）不受上限约束：世界文件写了几条就是几条。
        // 否则"我自己声明的上限"会拒绝"我自己写的世界"，那是本末倒置。
        var m = Module(1);
        m.RegisterProducer("甲", new Dictionary<string, double> { ["圆罐"] = 0.4, ["方罐"] = 0.3, ["高罐"] = 0.2 });
        Assert.Equal(3, m.UsedSlots);
    }

    [Fact]
    public void InitialActiveLinesBeyondTheAuthoredCapAreRejectedByWorldValidation()
    {
        var world = new WorldOptions
        {
            Shapes = new List<string> { "圆罐", "方罐", "高罐" },
            Liquids = new List<string> { "酒", "蜜" },
            ProducerSlots = 2,
            Producers = new List<ProducerOptions>
            {
                new()
                {
                    Name = "甲",
                    Rates = new Dictionary<string, double>
                    {
                        ["圆罐"] = 0.4,
                        ["方罐"] = 0.3,
                        ["高罐"] = 0.2
                    }
                }
            }
        };

        var report = world.Validate();

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, error =>
            error.Contains("producerSlots", StringComparison.OrdinalIgnoreCase) &&
            error.Contains("3", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFacadeWiresTheCapacityFromWorldOptionsAndEnforcesIt()
    {
        var world = new WorldOptions
        {
            Shapes = new List<string> { "圆罐", "方罐" },
            Liquids = new List<string> { "酒", "蜜" },
            ProducerSlots = 1,
            Producers = new List<ProducerOptions>
            {
                new() { Name = "甲", Rates = new Dictionary<string, double> { ["圆罐"] = 0.4, ["方罐"] = 0.0 } }
            }
        };
        var sys = new JarSystem.Core.JarSystem(randomSeed: 1, world: world);

        Assert.Equal(1, sys.World.ProducerSlots);
        Assert.Equal(1, sys.ProducerModule.ProducerSlots);
        Assert.Equal(1, sys.ProducerModule.UsedSlots);

        // 改已装备那条：允许（不占新槽）
        sys.UpdateProducerRate("甲", "圆罐", 0.6);

        // 开第 2 条：被内核拒绝（且这是可回放的世界意图，被拒时不写命令日志）
        var before = sys.CommandLog.Count;
        var ex = Assert.Throws<DomainValidationException>(() => sys.UpdateProducerRate("甲", "方罐", 0.3));
        Assert.Contains("产线槽已满", ex.Message);
        Assert.Equal(before, sys.CommandLog.Count);
        Assert.Empty(JarSystem.Core.Services.WorldInvariants.Check(sys));
    }
}

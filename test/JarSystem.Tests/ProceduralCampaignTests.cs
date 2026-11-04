using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **程序化关卡**的契约：同种子同关、不同版本拒绝、生成的每一关都过闸。
///
/// 为什么必须钉：生成器的价值全在"可复现"和"不会交出赢不了的关"这两条上。
/// 少了第一条，玩家分享的种子没有意义；少了第二条，游戏会把一关死局当成内容发给玩家。
/// </summary>
public class ProceduralCampaignTests
{
    /// <summary>六原型 × 若干种子：每一关都必须真能生成（生成内部已过闸，过不了会抛）。</summary>
    [Fact]
    public void EveryArchetypeGeneratesALevelThatPassesTheGate()
    {
        foreach (var archetype in ProceduralCampaign.Archetypes)
        {
            for (var seed = 1; seed <= 5; seed++)
            {
                var level = ProceduralCampaign.Generate(archetype.Id, seed);

                Assert.Equal(archetype.Id, level.ArchetypeId);
                Assert.Equal(ProceduralCampaign.RulesVersion, level.RulesVersion);
                Assert.Equal(seed, level.Seed);
                Assert.True(level.Turns > 0, archetype.Id + " 的回合上限必须 > 0");
                Assert.True(level.Goal >= 1, archetype.Id + " 的目标必须 ≥ 1");
                Assert.NotEmpty(level.Orders);
                Assert.False(string.IsNullOrWhiteSpace(level.Name));
                Assert.False(string.IsNullOrWhiteSpace(level.Hint));

                // 生成器自己也会过闸；这里再核一次"世界本身合法"，防止闸门被人为放宽。
                var validity = level.World.Validate();
                Assert.True(validity.IsValid, archetype.Id + " 生成的世界不合法：" + string.Join("；", validity.Errors));

                // 每一关都要有产线，否则是空转的一关（闸门查不出来，因为是"能赢但没内容"）。
                Assert.NotEmpty(level.World.Producers);
            }
        }
    }

    /// <summary>同一 (原型, 种子, 版本) 必须逐字同一关：可分享种子、可复现问题。</summary>
    [Fact]
    public void TheSameSeedGivesTheVerySameLevel()
    {
        foreach (var archetype in ProceduralCampaign.Archetypes)
        {
            var a = ProceduralCampaign.Generate(archetype.Id, 12345);
            var b = ProceduralCampaign.Generate(archetype.Id, 12345);

            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Hint, b.Hint);
            Assert.Equal(a.Turns, b.Turns);
            Assert.Equal(a.Goal, b.Goal);
            Assert.Equal(a.Orders, b.Orders);
            Assert.Equal(WorldOptionsJson.ToJson(a.World), WorldOptionsJson.ToJson(b.World));
        }
    }

    /// <summary>种子真的起作用：不同种子会变出不同的关卡（否则"程序化"是假的）。</summary>
    [Fact]
    public void DifferentSeedsProduceDifferentLevels()
    {
        var fingerprints = Enumerable.Range(1, 12)
            .Select(seed =>
            {
                var level = ProceduralCampaign.Generate("reshape", seed);
                return level.Name + "|" + WorldOptionsJson.ToJson(level.World);
            })
            .Distinct()
            .Count();

        Assert.True(fingerprints > 1, "12 颗种子只生成了 1 种关卡 —— 种子没在控制内容");
    }

    /// <summary>规则版本不符的种子必须被拒绝，而不是"大致按新规则生成一关"。</summary>
    [Fact]
    public void ASeedFromAnotherRulesVersionIsRejected()
    {
        var error = Assert.Throws<ArgumentException>(() => ProceduralCampaign.Generate("reshape", 7, "0"));
        Assert.Contains("规则版本", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownArchetypeIsRejected()
    {
        Assert.Throws<ArgumentException>(() => ProceduralCampaign.Generate("没有这个原型", 1));
    }

    /// <summary>闸门必须抓得出"要的东西没有来源"：这是《No Mysterious Resources》的开局前形态。</summary>
    [Fact]
    public void TheGateRefusesAnOrderForAShapeNothingCanProduce()
    {
        var world = MinimalWorld();
        world.Producers.Add(new ProducerOptions { Name = "陶工", Rates = { ["圆罐"] = 0.5 } });
        world.Workshops.Add(new WorkshopOptions { Name = "台一", BaseMarks = { "圆罐", "方罐" } });
        world.OrderTemplates.Add(Template("要一只鼓罐", "鼓罐"));

        var report = LevelGates.Check(world, turns: 5, goal: 1, new[] { "要一只鼓罐" }, checkWinnability: false);

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, f => f.Contains("鼓罐", StringComparison.Ordinal) && f.Contains("没有来源", StringComparison.Ordinal));
    }

    /// <summary>能改形改出来的形状算有来源（附加标记就是"可改形目标"）。</summary>
    [Fact]
    public void AShapeReachableByReshapingCountsAsASource()
    {
        var world = MinimalWorld();
        world.InitialStock["蜜"] = 2;
        world.Producers.Add(new ProducerOptions { Name = "陶工", Rates = { ["圆罐"] = 0.5 } });
        world.Workshops.Add(new WorkshopOptions { Name = "台一", BaseMarks = { "圆罐", "方罐" }, AdditionalMarks = { "三角罐" } });
        world.OrderTemplates.Add(Template("要一只三角罐", "三角罐"));

        var report = LevelGates.Check(world, turns: 5, goal: 1, new[] { "要一只三角罐" }, checkWinnability: false);

        Assert.True(report.Passed, report.Describe());
    }

    [Fact]
    public void TheGateRefusesAGoalNoScheduleCanReach()
    {
        var world = MinimalWorld();
        world.InitialStock["蜜"] = 2;
        world.Producers.Add(new ProducerOptions { Name = "陶工", Rates = { ["圆罐"] = 0.5 } });
        world.Workshops.Add(new WorkshopOptions { Name = "台一", BaseMarks = { "圆罐" } });
        world.OrderTemplates.Add(Template("一只圆罐", "圆罐"));

        var report = LevelGates.Check(world, turns: 5, goal: 3, new[] { "一只圆罐" }, checkWinnability: false);

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, f => f.Contains("永远赢不了", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGateRefusesAnUnknownOrderInstance()
    {
        var world = MinimalWorld();
        world.InitialStock["蜜"] = 2;
        world.Producers.Add(new ProducerOptions { Name = "陶工", Rates = { ["圆罐"] = 0.5 } });
        world.Workshops.Add(new WorkshopOptions { Name = "台一", BaseMarks = { "圆罐" } });
        world.OrderTemplates.Add(Template("一只圆罐", "圆罐"));

        var report = LevelGates.Check(world, turns: 5, goal: 1, new[] { "没有这个模板" }, checkWinnability: false);

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, f => f.Contains("不存在的模板", StringComparison.Ordinal));
    }

    // ---- 辅助 -----------------------------------------------------------------------------

    private static WorldOptions MinimalWorld() => new()
    {
        Shapes = new List<string> { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" },
        Liquids = new List<string> { "酒", "蜜" },
        DefaultMaxJars = 6,
        DefaultMaxLiquids = 5,
        ProducerSlots = 3,
        Flow = new FlowOptions { JarCount = 3, LiquidAmount = 1 },
    };

    private static OrderTemplateOptions Template(string name, string shape) => new()
    {
        Name = name,
        Type = "A",
        Requirements = { new RequirementOptions { Shape = shape } },
    };
}

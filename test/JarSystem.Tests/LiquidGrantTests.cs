using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 「动用液体」的唯一说法（原则4：规则 = 概念体的能力）。
///
/// 改之前，"能不能用这份液体"是每个调用方自己拼的：`IsBanned` + `GetValueOrDefault` + `MaxLiquids`
/// 在 <see cref="Workshop"/> 内部四处各写一遍，`WorkshopRulebook` / `GreedyPlanBuilder` / 求解器再各拼一遍。
/// 于是同一条规则有四五种说法 —— B1 那条「禁液体必须先倒空再禁」的守卫，就是这种重复的症状。
///
/// 现在收敛成三个能力（向工作台请求，拿回应）：
/// <see cref="Workshop.CheckLiquid"/>（禁令门）、<see cref="Workshop.CanUseLiquid"/>（禁令 + 库存）、
/// <see cref="Workshop.CanStoreLiquid"/>（禁令 + 容量）。
/// 本组测试钉两件事：**回应里的说法**，以及**操作失败的理由没有变**（行为逐字节不变是这一步的前提）。
/// </summary>
public class LiquidGrantTests
{
    private static readonly Vocabulary Vocab = new(
        new[] { "圆罐", "方罐", "三角罐" }, new[] { "酒", "蜜" });

    private static Workshop Bench(List<string>? ban = null) => new(
        "主台",
        new List<string> { "圆罐", "方罐" },
        new List<string> { "三角罐" },
        ban ?? new List<string>(),
        maxJars: 6, maxLiquids: 2);

    [Fact]
    public void CheckLiquidOnlyAnswersTheBanQuestion()
    {
        var free = Bench();
        Assert.True(free.CheckLiquid("蜜").Granted);
        Assert.Null(free.CheckLiquid("蜜").Code);

        var banned = Bench(new List<string> { "酒" });
        var grant = banned.CheckLiquid("酒");
        Assert.False(grant.Granted);
        Assert.Equal("banned", grant.Code);
        Assert.Equal("工作台禁酒", grant.Reason);
    }

    [Fact]
    public void CanUseLiquidAddsStockToTheSameAnswer()
    {
        var w = Bench();
        w.AddLiquid("蜜", 1);

        Assert.True(w.CanUseLiquid("蜜", 1).Granted);
        var short1 = w.CanUseLiquid("蜜", 2);
        Assert.False(short1.Granted);
        Assert.Equal("insufficient", short1.Code);
        Assert.Equal("液体蜜不足", short1.Reason);
    }

    [Fact]
    public void CanStoreLiquidAddsCapacityToTheSameAnswer()
    {
        var w = Bench();                       // MaxLiquids = 2
        w.AddLiquid("蜜", 2);

        var full = w.CanStoreLiquid("蜜", 1);
        Assert.False(full.Granted);
        Assert.Equal("over-capacity", full.Code);
        Assert.Equal("液体蜜存储已满", full.Reason);
    }

    [Fact]
    public void BanOutranksCapacitySoTheReasonStaysTheBannedOne()
    {
        // 禁令是"整种液体不可用"，容量是"这一格放不下"。先答禁令，否则玩家会以为只是放不下。
        var w = Bench(new List<string> { "酒" });
        var grant = w.CanStoreLiquid("酒", 99);
        Assert.Equal("banned", grant.Code);
        Assert.Equal("工作台禁酒", grant.Reason);
    }

    [Fact]
    public void OperationFailureReasonsAreUnchanged()
    {
        // 这一步是纯重构：对外说的话必须逐字不变，否则回放与接口的可见行为就漂了。
        var w = Bench(new List<string> { "酒" });
        w.AddLiquid("蜜", 2);
        var bomb = new Jar("圆罐", "酒", null, Vocab);

        w.AddJar(new Jar("圆罐", null, null, Vocab));
        var empty = w.Jars[0];

        var pourInBanned = w.PourIn(empty, "酒");
        Assert.False(pourInBanned.Success);
        Assert.Equal("工作台禁酒", pourInBanned.Reason);

        var pourInShort = w.PourIn(empty, "酒");
        Assert.Equal("工作台禁酒", pourInShort.Reason);      // 禁令仍然先于库存

        w.AddJar(bomb);
        var fullBanned = w.ReshapeFull(bomb, "方罐");
        Assert.False(fullBanned.Success);
        Assert.Equal("工作台禁酒", fullBanned.Reason);
        Assert.Equal("酒", bomb.Liquid);                     // 被拒时液体放回罐里
    }

    [Fact]
    public void WarehouseInflowStillClampsInsteadOfRefusing()
    {
        var w = Bench();                        // MaxLiquids = 2
        Assert.Equal(2, w.TryReceiveLiquid("蜜", 5));   // 部分接收
        Assert.Equal(0, w.TryReceiveLiquid("蜜", 1));   // 满了就一点不收
        Assert.False(w.AddLiquid("蜜", 1));             // AddLiquid 是"全有或全无"
    }
}

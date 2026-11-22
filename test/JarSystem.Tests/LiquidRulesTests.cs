using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 液体规则的**纯值表述**（<see cref="LiquidRules"/>）。
///
/// 存在的理由：<see cref="Workshop"/> 有库存状态，而 <see cref="WorkshopRulebook"/>、贪心兜底、
/// 求解器是静态/纯值的，拿不到活的 Workshop。过去它们各自拼 "禁令 + 数量 + 上限"，
/// 同一条规则五六个说法。现在规则收敛成这个纯函数：**禁令在构造时给，账目由调用方传**。
///
/// 本组测试钉它的三件性质：①禁令同时约束**取用与接收**；②容量 &lt;= 0 表示不限；
/// ③它和活的工作台给出**同一批结论**（否则"只有一个说法"就只是口号）。
/// </summary>
public class LiquidRulesTests
{
    private static WorkshopCapability Cap(List<string>? ban = null) =>
        new(new List<string> { "圆罐", "方罐" }, new List<string> { "三角罐" }, ban ?? new List<string>());

    [Fact]
    public void UnlimitedSaysYesToEverything()
    {
        var rules = LiquidRules.Unlimited;
        Assert.False(rules.IsBanned("酒"));
        // "不限"限的是**禁令与容量**，不是库存：够不够付永远由调用方给的账目决定。
        Assert.True(rules.CanPay("酒", 99, 99).Granted);
        Assert.Equal("insufficient", rules.CanPay("酒", 99, 0).Code);
        Assert.True(rules.CanStore("酒", 99, 0).Granted);
    }

    [Fact]
    public void BanOutranksStockOnTheUseSide()
    {
        var rules = new LiquidRules(Cap(new List<string> { "酒" }), capacity: 2);
        Assert.Equal("banned", rules.CanPay("酒", 1, 0).Code);
        Assert.Equal("banned", rules.CanPay("酒", 1, 99).Code);   // 库存再多也取不出来
        Assert.Equal("工作台禁酒", rules.CanPay("酒", 1, 0).Reason);
    }

    [Fact]
    public void BanBlocksStorageBeforeCapacityIsConsidered()
    {
        var rules = new LiquidRules(Cap(new List<string> { "酒" }), capacity: 2);
        Assert.Equal("banned", rules.CanStore("酒", 1, 0).Code);
        Assert.Equal("banned", rules.CanStore("酒", 1, 2).Code);
        Assert.True(rules.CanStore("蜜", 1, 0).Granted);
        Assert.Equal("over-capacity", rules.CanStore("蜜", 1, 2).Code);
    }

    [Fact]
    public void WorkshopRejectsBannedFreeLiquidOnBothWholeAndPartialReceive()
    {
        var workshop = new Workshop(
            "禁液台",
            new List<string> { "圆罐" },
            ban: new List<string> { "酒" },
            maxLiquids: 2);

        Assert.False(workshop.AddLiquid("酒", 1));
        Assert.Equal(0, workshop.LiquidStorage["酒"]);
        Assert.Equal(0, workshop.TryReceiveLiquid("酒", 5));
        Assert.Equal(0, workshop.LiquidStorage["酒"]);

        Assert.Equal(2, workshop.TryReceiveLiquid("蜜", 5));
        Assert.Equal(2, workshop.LiquidStorage["蜜"]);
    }

    [Fact]
    public void ApplyingBanToAnExistingFreeLiquidBalanceIsRejectedAtomically()
    {
        var workshop = new Workshop(
            "动态禁液台",
            new List<string> { "圆罐" },
            maxLiquids: 3);
        Assert.True(workshop.AddLiquid("酒", 2));

        var error = Assert.Throws<DomainValidationException>(() =>
            workshop.ReplaceCapability(new WorkshopCapability(
                new List<string> { "圆罐" },
                new List<string>(),
                new List<string> { "酒" })));

        Assert.Contains("仍存有游离液体", error.Message);
        Assert.False(workshop.IsBanned("酒"));
        Assert.Equal(2, workshop.LiquidStorage["酒"]);
        Assert.True(workshop.CanUseLiquid("酒", 1).Granted);
    }

    [Fact]
    public void StockAndCapacityAreAnsweredSeparately()
    {
        var rules = new LiquidRules(Cap(), capacity: 2);
        Assert.Equal("insufficient", rules.CanPay("蜜", 2, 1).Code);
        Assert.Equal("over-capacity", rules.CanStore("蜜", 1, 2).Code);
        Assert.True(rules.CanPay("蜜", 1, 1).Granted);
        Assert.True(rules.CanStore("蜜", 1, 1).Granted);
    }

    [Fact]
    public void ZeroOrNegativeCapacityMeansNoLimit()
    {
        foreach (var cap in new[] { 0, -1 })
        {
            var rules = new LiquidRules(Cap(), cap);
            Assert.True(rules.CanStore("蜜", 1_000_000, 0).Granted);
        }
    }

    [Fact]
    public void TheLiveWorkshopAndThePureValueGiveTheSameAnswers()
    {
        // "只有一份规则"的判据：把工作台的库存喂给纯值，两者的结论必须逐格相同。
        // 如果哪天 Workshop 又自己长出一条旁路判断，这里立刻对不上。
        var vocab = new Vocabulary(new[] { "圆罐", "方罐", "三角罐" }, new[] { "酒", "蜜" });
        var w = new Workshop("主台", new List<string> { "圆罐", "方罐" }, new List<string> { "三角罐" },
            new List<string> { "酒" }, maxJars: 6, maxLiquids: 2, ids: new SequentialIdGenerator());
        w.AddLiquid("蜜", 2);

        var liquid = w.Liquid;
        foreach (var l in new[] { "酒", "蜜", "不存在的液体" })
        {
            Assert.Equal(w.IsBanned(l), liquid.IsBanned(l));
            var have = w.LiquidStorage.GetValueOrDefault(l);
            foreach (var amount in new[] { 1, 2, 5 })
            {
                Assert.Equal(w.CanUseLiquid(l, amount).Code, liquid.CanPay(l, amount, have).Code);
                Assert.Equal(w.CanStoreLiquid(l, amount).Code, liquid.CanStore(l, amount, have).Code);
            }
        }
        _ = vocab;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 改形规则的**纯值** <see cref="ReshapeRules"/>：活工作台与规则表问同一份实现。
///
/// 为什么要它：改形有三条规则（方向、空罐成本、满罐自耗），原先活工作台
/// （<see cref="Workshop.CanReshapeTo"/>）和规则表（<see cref="WorkshopRulebook.IsAllowed"/>）
/// 各写一份。任一方只改一处，规则表就会开始"承诺做不到的事"或"否认做得到的事" ——
/// 那正是差分对拍反复抓到过的形态（步骤2a 的 `PourOut` 少问禁令就是这么暴露的）。
///
/// 本组测试钉的是**同一份实现**：纯值、活门面、规则表三者对同一状态必须给出同一结论。
/// </summary>
public class ReshapeRulesTests
{
    private static JarFacts DefaultFacts(string shape, string? liquid, bool isMarked) =>
        new(shape, liquid, isMarked, JarPolarityResolver.Resolve(liquid, JarSystem.Core.Config.Vocabulary.Default));

    private static Vocabulary Vocab() => new(
        new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" },
        new[] { "酒", "蜜" });

    private static WorkshopCapability XuanwuCap() => new(
        new[] { "圆罐", "方罐" },
        new[] { "三角罐" },
        new[] { "酒" });   // 禁酒

    private static Workshop Xuanwu() => new(
        "玄武台",
        new List<string> { "圆罐", "方罐" },
        new List<string> { "三角罐" },
        new List<string> { "酒" },
        maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());

    private static ReshapeRules Rules(int maxLiquids = 5) =>
        new(XuanwuCap(), new LiquidRules(XuanwuCap(), maxLiquids));

    [Fact]
    public void DirectionComesFromTheShapeRule()
    {
        var rules = Rules();

        Assert.True(rules.CanTransform("圆罐", "方罐"));      // 基础 → 其它基础
        Assert.True(rules.CanTransform("圆罐", "三角罐"));    // 基础 → 附加
        Assert.True(rules.CanTransform("三角罐", "圆罐"));    // 附加 → 基础
        Assert.False(rules.CanTransform("圆罐", "高罐"));     // 高罐不在任何标记里
        Assert.False(rules.CanTransform("三角罐", "方罐") == false && false);
    }

    [Fact]
    public void AnExplicitCostMustBePayableThatExactLiquid()
    {
        var rules = Rules();
        var pool = new Dictionary<string, int> { ["酒"] = 3, ["蜜"] = 1 };

        Assert.True(rules.PayCost("蜜", pool).Granted);

        var banned = rules.PayCost("酒", pool);               // 池里有 3 份，但禁 ⇒ 付不了
        Assert.False(banned.Granted);
        Assert.Equal("no-cost-liquid", banned.Code);
        Assert.Contains("无法支付液体酒", banned.Reason);
    }

    [Fact]
    public void WithNoExplicitCostItPicksTheFirstPayableLiquidInPoolOrder()
    {
        var rules = Rules();
        var pool = new Dictionary<string, int> { ["酒"] = 3, ["蜜"] = 1 };   // 键序：酒 → 蜜

        var grant = rules.PayCost(null, pool);

        Assert.True(grant.Granted);      // 酒付不了，但按序继续找，蜜能付
        Assert.Null(grant.Code);

        var empty = rules.PayCost(null, new Dictionary<string, int> { ["酒"] = 3 });
        Assert.False(empty.Granted);
        Assert.Equal("no-cost-liquid", empty.Code);
        Assert.Contains("没有可支付的改形成本液体", empty.Reason);
    }

    [Fact]
    public void SelfConsumeUsesOnlyJarInternalAccountAndIgnoresWorkshopBan()
    {
        var rules = Rules();

        Assert.True(rules.SelfConsume("蜜").Granted);
        Assert.True(rules.SelfConsume("酒").Granted);
        Assert.Equal("not-full", rules.SelfConsume(null).Code);

        // 玄武台 Ban 只约束游离液体暂存区；Jar 内部酒可以被满罐自耗。
        var w = Xuanwu();
        var jar = new Jar("圆罐", "蜜", null, Vocab());
        w.AddJar(jar);
        var (execOk, execReason) = w.TryReshape(jar, "方罐", ReshapeMode.Full);

        Assert.True(execOk, execReason);
        Assert.Equal("方罐", jar.Shape);
        Assert.Null(jar.Liquid);
        Assert.True(jar.IsMarked);
        Assert.Equal(0, w.LiquidStorage["蜜"]);

        // 无 Ban 的从台也能自耗酒 Jar；资源仍只从 Jar 内部账户扣除。
        var permissive = new Workshop("从台", new List<string> { "圆罐", "方罐" });
        var received = new Jar("圆罐", "酒", null, Vocab());
        permissive.AddJar(received);
        var (slaveOk, slaveReason) = permissive.TryReshape(received, "方罐", ReshapeMode.Full);
        Assert.True(slaveOk, slaveReason);
        Assert.Equal("方罐", received.Shape);
        Assert.Null(received.Liquid);
        Assert.True(received.IsMarked);
        Assert.Equal(0, permissive.LiquidStorage["酒"]);
    }

    [Fact]
    public void AnEmptyJarIsNotSelfConsumableAndAMissingLiquidIsReportedAsNotFull()
    {
        var rules = Rules();
        var grant = rules.SelfConsume(null);
        Assert.False(grant.Granted);
        Assert.Equal("not-full", grant.Code);
    }

    /// <summary>
    /// 三方对拍：纯值、活门面（<see cref="Workshop.CanReshapeTo"/>）、规则表
    /// （<see cref="WorkshopRulebook.IsAllowed"/>）对同一状态必须同结论。
    /// </summary>
    [Fact]
    public void PureValueLiveFacadeAndRulebookNeverDisagree()
    {
        var shapes = new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐" };
        var disagreements = new List<string>();
        var cases = 0;

        foreach (var shape in shapes)
        foreach (var liquid in new string?[] { null, "酒", "蜜" })
        foreach (var to in shapes)
        {
            // ① 活门面
            var w = Xuanwu();
            var jar = new Jar(shape, liquid, null, Vocab());
            w.AddJar(jar);
            w.AddLiquid("酒", 1);
            w.AddLiquid("蜜", 1);
            var live = w.CanReshapeTo(jar, to).Granted;

            // ② 规则表（同一个世界状态喂给纯函数）
            var ctx = new RuleContext(XuanwuCap(), new ModifierStack());
            var facts = DefaultFacts(shape, liquid, false);
            var pool = new Dictionary<string, int> { ["酒"] = 1, ["蜜"] = 1 };
            var op = liquid == null
                ? WorkshopOperation.ReshapeEmpty(to, "蜜")
                : WorkshopOperation.ReshapeFull(to);
            var table = WorkshopRulebook.IsAllowed(ctx, 5, facts, op, pool);

            cases++;
            if (live != table)
                disagreements.Add($"{shape}:{liquid ?? "空"}→{to}：活门面说 {(live ? "能" : "不能")}，规则表说 {(table ? "能" : "不能")}");
        }

        Assert.True(cases >= 60, "用例太少（" + cases + "），会空转");
        Assert.True(disagreements.Count == 0,
            "活门面与规则表分家了：\n  " + string.Join("\n  ", disagreements.Take(10)));
    }
}

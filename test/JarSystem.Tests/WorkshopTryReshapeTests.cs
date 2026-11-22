using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 改形的**唯一入口** <see cref="Workshop.TryReshape"/>（用户「收口」：判定与执行合成一处）。
///
/// 收口前有两扇门（ReshapeEmpty / ReshapeFull），调用方必须自己先判断"这罐子空不空"，
/// 判错了就得到一条看起来像规则拒绝的错误。现在是一扇门 + 一个 <see cref="ReshapeMode"/>：
/// 默认 <c>Auto</c> 按罐子此刻状态自选，需要严格区分的路径显式传 <c>Empty</c> / <c>Full</c>。
///
/// 两条最容易出错的语义，这里逐条钉住：
/// 1 **成本自选是确定的**：不指定成本时按液池键序取第一份可取的 —— 可重放，不是"随便挑"。
/// 2 **显式给了却付不了 ⇒ 失败，不替换**：否则"我让你付蜜、它却吃了酒"会静默发生。
/// </summary>
public class WorkshopTryReshapeTests
{
    private static Vocabulary Vocab() => new(
        new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" },
        new[] { "酒", "蜜" });

    private static Workshop Xuanwu() => new(
        "玄武台",
        new List<string> { "圆罐", "方罐" },
        new List<string> { "三角罐" },
        new List<string> { "酒" },
        maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());

    [Fact]
    public void AutoModePicksAUsableCostLiquidAndPaysExactlyOne()
    {
        var w = Xuanwu();
        var jar = new Jar("圆罐", null, null, Vocab());
        w.AddJar(jar);
        w.AddLiquid("酒", 1);      // 被禁 ⇒ 不能当成本
        w.AddLiquid("蜜", 2);

        var (ok, reason) = w.TryReshape(jar, "方罐");

        Assert.True(ok, "Auto 应当自选到蜜并成功：" + reason);
        Assert.Equal("方罐", jar.Shape);
        Assert.True(jar.IsMarked);                                  // 改形会打标记
        Assert.Equal(1, w.LiquidStorage["蜜"]);                     // 只付了 1 份
        Assert.Equal(0, w.LiquidStorage["酒"]);                     // 禁酒一分未动
    }

    [Fact]
    public void AnExplicitCostThatCannotBePaidFailsInsteadOfSilentlySwitchingToAnother()
    {
        var w = Xuanwu();
        var jar = new Jar("圆罐", null, null, Vocab());
        w.AddJar(jar);
        w.AddLiquid("酒", 1);      // 池里有，但被禁 ⇒ 付不了
        w.AddLiquid("蜜", 2);      // 明明还有别的可付，但调用方点名要酒
        BanWine(w);

        var (ok, reason) = w.TryReshape(jar, "方罐", ReshapeMode.Empty, "酒");

        Assert.False(ok, "点名要付禁酒，必须失败");
        Assert.Contains("无法支付液体酒", reason);
        Assert.Equal("圆罐", jar.Shape);                            // 世界没变
        Assert.False(jar.IsMarked);
        Assert.Equal(2, w.LiquidStorage["蜜"]);                     // **没有**偷偷换用蜜
        Assert.Equal(1, w.LiquidStorage["酒"]);
    }

    [Fact]
    public void ExplicitModeIsHonouredEvenWhenTheJarStateSuggestsOtherwise()
    {
        var w = Xuanwu();
        var jar = new Jar("圆罐", "蜜", null, Vocab());   // 满罐
        w.AddJar(jar);
        w.AddLiquid("蜜", 2);

        // 声明"这是空罐改形" ⇒ 应当拿到"必须为空罐"，而不是被当成满罐自耗。
        var (ok, reason) = w.TryReshape(jar, "方罐", ReshapeMode.Empty, "蜜");
        Assert.False(ok);
        Assert.Contains("必须为空罐", reason);
        Assert.Equal("蜜", jar.Liquid);
    }

    [Fact]
    public void AMarkedJarIsRefusedAndTheWorldStaysUntouched()
    {
        var w = Xuanwu();
        var jar = new Jar("圆罐", null, null, Vocab());
        w.AddJar(jar);
        w.AddLiquid("蜜", 2);
        jar.Mark();

        var (ok, reason) = w.TryReshape(jar, "方罐");

        Assert.False(ok);
        Assert.Contains("罐子已被操作", reason);
        Assert.Equal("圆罐", jar.Shape);
        Assert.Equal(2, w.LiquidStorage["蜜"]);
    }

    /// <summary>
    /// 入口与只读询问必须永远一致：同一批（工作台 × 罐子 × 目标）上，
    /// <c>TryReshape</c> 的成败 == <c>CanReshapeTo</c> 的 Granted。
    ///
    /// 这条是对「收口」的定义：如果某天有人只改了其中一个，这里立刻分家。
    /// </summary>
    [Fact]
    public void TryReshapeAndCanReshapeToNeverDisagree()
    {
        var shapes = new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐" };
        var disagreements = new List<string>();
        var cases = 0;
        var successes = 0;

        foreach (var shape in shapes)
        foreach (var liquid in new string?[] { null, "酒", "蜜" })
        foreach (var to in shapes.Append("不存在的形状"))
        {
            // 询问
            Workshop ask = Xuanwu();
            var probe = new Jar(shape, liquid, null, Vocab());
            ask.AddJar(probe);
            ask.AddLiquid("酒", 1);
            ask.AddLiquid("蜜", 1);
            BanWine(ask);
            var granted = ask.CanReshapeTo(probe, to).Granted;

            // 动手
            Workshop act = Xuanwu();
            var jar = new Jar(shape, liquid, null, Vocab());
            act.AddJar(jar);
            act.AddLiquid("酒", 1);
            act.AddLiquid("蜜", 1);
            BanWine(act);
            var (ok, _) = act.TryReshape(jar, to);

            cases++;
            if (ok) successes++;
            if (granted != ok)
            {
                disagreements.Add($"{shape}:{liquid ?? "空"}→{to}：询问说 {(granted ? "能" : "不能")}，动手说 {(ok ? "能" : "不能")}");
            }
        }

        Assert.True(cases >= 100, "用例太少（" + cases + "），会空转");
        Assert.True(successes > 0, "一个都不成功，用例没法区分「能」与「不能」");
        Assert.True(disagreements.Count == 0,
            "唯一入口与只读询问分家了：\n  " + string.Join("\n  ", disagreements.Take(10)));
    }
}

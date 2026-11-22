using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 「这只罐子现在能改形成什么」的**单一入口**（<see cref="Workshop.CanReshapeTo"/> /
/// <see cref="Workshop.ReshapeTargets"/>）。
///
/// 为什么要它：这个问题的答案原先散在三处 —— 方向（<see cref="WorkshopCapability.CanTransform"/>）、
/// 空罐改形的成本（<c>TryReshape(..., ReshapeMode.Empty)</c> 的 TryPayReshapeCost）、满罐自耗（<c>ReshapeMode.Full</c>）分别属于游离液体和 Jar 内部资源；后者不经过 staging ban。规则表、求解器、页面各自拼一遍，中文文案也各写一份。
///
/// 本组测试的做法是**拿执行器当 oracle**（与 `SolverExecutorParityTests` 同一纪律）：
/// 对每一种（工作台 × 罐子状态 × 目标形状），`CanReshapeTo` 的回答必须与"真的去执行"逐例一致。
/// 只测入口自己"觉得对"是没用的 —— 判定与执行一旦分家，错的那个永远是判定。
/// </summary>
public class WorkshopReshapeTests
{
    private static Vocabulary Vocab() => new(
        new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" },
        new[] { "酒", "蜜" });

    /// <summary>玄武台（禁酒）；附加标记里既有三角罐，用来测"附加→附加"不通。</summary>
    private static Workshop Xuanwu() => new(
        "玄武台",
        new List<string> { "圆罐", "方罐" },
        new List<string> { "三角罐" },
        new List<string> { "酒" },
        maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());

    /// <summary>青龙台（禁蜜）；基础标记含三角罐与长罐。</summary>
    private static Workshop Qinglong() => new(
        "青龙台",
        new List<string> { "三角罐", "长罐" },
        new List<string> { "扁罐" },
        new List<string> { "蜜" },
        maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());

    private static readonly string[] Shapes = { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐" };

    // ---------------------------------------------------------------- 入口的必要性

    [Fact]
    public void ReshapeTargetsComesFromTheDirectionRuleNotFromTheVocabulary()
    {
        var w = Xuanwu();
        var jar = new Jar("圆罐", null, null, Vocab());
        w.AddJar(jar);
        w.AddLiquid("蜜", 2);

        var targets = w.ReshapeTargets(jar);

        // 基础标记的通向：其它基础（方罐）+ 全部附加（三角罐）；高罐/长罐不在标记里 ⇒ 不在列表里。
        Assert.Contains("方罐", targets);
        Assert.Contains("三角罐", targets);
        Assert.DoesNotContain("圆罐", targets);   // 自己不算目标
        Assert.DoesNotContain("高罐", targets);
        Assert.DoesNotContain("长罐", targets);
    }

    [Fact]
    public void AdditionalMarkCannotJumpToAnotherAdditionalMark()
    {
        var w = Xuanwu();
        var jar = new Jar("三角罐", null, null, Vocab());
        w.AddJar(jar);
        w.AddLiquid("蜜", 2);

        var grant = w.CanReshapeTo(jar, "高罐");

        Assert.False(grant.Granted);
        Assert.Equal("direction", grant.Code);
        Assert.Contains("无法从三角罐转化为高罐", grant.Reason);
    }

    [Fact]
    public void AnEmptyJarNeedsSomeUsableCostLiquid()
    {
        var w = Xuanwu();
        var jar = new Jar("圆罐", null, null, Vocab());
        w.AddJar(jar);

        // 池子全空 ⇒ 没有可付的成本液体。
        var dry = w.CanReshapeTo(jar, "方罐");
        Assert.False(dry.Granted);
        Assert.Equal("no-cost-liquid", dry.Code);

        // 只存着被禁的酒 ⇒ 依然是"没有可付的"（存得住、取不出）。
        w.AddLiquid("酒", 2);
        var bannedOnly = w.CanReshapeTo(jar, "方罐");
        Assert.False(bannedOnly.Granted);
        Assert.Equal("no-cost-liquid", bannedOnly.Code);

        // 有一份蜜 ⇒ 能付。
        w.AddLiquid("蜜", 1);
        Assert.True(w.CanReshapeTo(jar, "方罐").Granted);
    }

    [Fact]
    public void FullJarContainingBannedLiquidCannotBeOperatedOnThisWorkshop()
    {
        var w = Xuanwu();
        var jar = new Jar("圆罐", "酒", null, Vocab());
        w.AddJar(jar);

        var grant = w.CanReshapeTo(jar, "方罐");

        Assert.False(grant.Granted);
        Assert.Equal("banned", grant.Code);
        Assert.Contains("禁酒", grant.Reason);
        var (success, reason) = w.TryReshape(jar, "方罐", ReshapeMode.Full);
        Assert.False(success);
        Assert.Contains("禁酒", reason);
        Assert.Equal("圆罐", jar.Shape);
        Assert.Equal("酒", jar.Liquid);
        Assert.False(jar.IsMarked);
    }

    [Fact]
    public void AnAlreadyMarkedJarIsRefusedBeforeAnythingElse()
    {
        var w = Xuanwu();
        var jar = new Jar("圆罐", null, null, Vocab());
        w.AddJar(jar);
        w.AddLiquid("蜜", 1);
        jar.Mark();

        var grant = w.CanReshapeTo(jar, "方罐");

        Assert.False(grant.Granted);
        Assert.Equal("marked", grant.Code);
    }

    // ---------------------------------------------------------------- 与执行器逐例对拍

    /// <summary>
    /// 主对拍：入口的"行不行"必须等于"真的执行成不成"。
    /// 三种工作台 × 六种罐子形状 × 三种罐内液体 × 七种目标形状（含不存在的目标）= 378 例。
    /// </summary>
    [Fact]
    public void TheEntryAgreesWithTheExecutorOnEveryCombination()
    {
        var failures = new List<string>();
        var checkedCases = 0;
        var grantedCases = 0;

        foreach (var benchFactory in new Func<Workshop>[] { Xuanwu, Qinglong })
        {
            foreach (var shape in Shapes)
            foreach (var liquid in new string?[] { null, "酒", "蜜" })
            foreach (var to in Shapes.Append("不存在的形状"))
            {
                // 入口的回答（在干净世界上问）
                Workshop bench = benchFactory();
                var probe = new Jar(shape, liquid, null, Vocab());
                bench.AddJar(probe);
                bench.AddLiquid("酒", 1);
                bench.AddLiquid("蜜", 1);
                var grant = bench.CanReshapeTo(probe, to);
                checkedCases++;
                if (grant.Granted) grantedCases++;

                // 执行器的回答（同样的世界，另起一份，真的去改）
                Workshop exec = benchFactory();
                var jar = new Jar(shape, liquid, null, Vocab());
                exec.AddJar(jar);
                exec.AddLiquid("酒", 1);
                exec.AddLiquid("蜜", 1);
                var executed = TryExecuteReshape(exec, jar, to);

                if (grant.Granted != executed)
                {
                    failures.Add(
                        $"{bench.Name}／{shape}:{liquid ?? "空"}→{to}：入口说 {(grant.Granted ? "能" : "不能")}" +
                        $"（{grant.Code ?? "ok"}），执行说 {(executed ? "能" : "不能")}");
                }
            }
        }

        // 2 台 × 6 形状 × 3 种罐内液体 × 7 个目标 = 252 例；门槛卡在 240 上，防止循环被改窄。
        Assert.True(checkedCases >= 240, "用例太少（" + checkedCases + "），这条断言会空转");
        Assert.True(grantedCases > 0, "一个都不允许，说明用例没法区分「能」和「不能」，也会空转");
        Assert.True(failures.Count == 0,
            "入口与执行器的结论不一致：\n  " + string.Join("\n  ", failures.Take(12)));
    }

    /// <summary>
    /// 真的去改一次：空罐逐个试池里能付的成本液体（任一个成功即算"能"），满罐走自耗。
    /// 这是"执行器侧"的答案，测试用它当 oracle。
    /// </summary>
    private static bool TryExecuteReshape(Workshop bench, Jar jar, string to)
    {
        if (jar.IsEmpty())
        {
            foreach (var cost in bench.LiquidStorage.Keys.ToList())
            {
                Workshop fresh = bench.Name == "玄武台" ? Xuanwu() : Qinglong();
                var j = new Jar(jar.Shape, null, null, Vocab());
                fresh.AddJar(j);
                fresh.AddLiquid("酒", 1);
                fresh.AddLiquid("蜜", 1);
                if (fresh.TryReshape(j, to, ReshapeMode.Empty, cost).Success) return true;
            }
            return false;
        }

        return bench.TryReshape(jar, to, ReshapeMode.Full).Success;
    }
}

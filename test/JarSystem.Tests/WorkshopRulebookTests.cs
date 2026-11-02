using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **规则表与执行器的一致性对拍**（<see cref="WorkshopRulebook"/> ↔ <see cref="Workshop"/>）。
///
/// <para>要守的是什么：同一个 (罐子, 工作台, 候选操作) 上，
/// 「按规则推断能不能做」与「真的执行会不会成功」必须是同一个答案。</para>
///
/// <para>为什么值得单独守：历史上三次同类错误都不是"写出错算法"，而是**同一套规则被写了两遍**——
/// 原型 JS 的 <c>tryOp</c> 漏了"形状必须是基础标记"、前置检查把禁令挡在 MATCH 之前、
/// 以及最初那 105 例错杀。这类错误不会让任何单测变红，只会让某一个入口悄悄宽松或严格。
/// 对拍是唯一能自动抓住它的形状。</para>
///
/// <para>注意本测试**不要求**求解器调用规则表（求解器走热路径、自己知道规则即可）。
/// 它只要求"任何一方改动规则时另一方不会悄悄漂移"——这正是把口头约定换成可执行断言的地方。</para>
/// </summary>
public class WorkshopRulebookTests
{
    private sealed class Cfg
    {
        public string Name = "";
        public string[] Base = System.Array.Empty<string>();
        public string[] Add = System.Array.Empty<string>();
        public string[] Ban = System.Array.Empty<string>();
        public int MaxLiquids = 3;
    }

    private static readonly Cfg[] Configs =
    {
        new() { Name = "玄武台", Base = new[] { "圆罐", "方罐" }, Add = new[] { "三角罐" }, Ban = new[] { "酒" } },
        new() { Name = "朱雀台", Base = new[] { "高罐" }, Add = new[] { "长罐" }, Ban = new[] { "酒" } },
        new() { Name = "自由台", Base = new[] { "圆罐", "鼓罐" }, Add = new[] { "尖罐" }, Ban = System.Array.Empty<string>() }
    };

    private static readonly (string Liquid, int Count)[][] Storages =
    {
        System.Array.Empty<(string, int)>(),
        new[] { ("酒", 1) },
        new[] { ("蜜", 1) },
        new[] { ("酒", 3) },              // 满：这一种液体的存储已到上限
        new[] { ("酒", 2), ("蜜", 1) }
    };

    private static readonly string?[] JarLiquids = { null, "酒", "蜜" };

    private static Workshop Build(Cfg cfg, string shape, string? liquid, bool marked,
        (string Liquid, int Count)[] storage, ModifierStack? modifiers = null)
    {
        var w = new Workshop(cfg.Name, new List<string>(cfg.Base), new List<string>(cfg.Add),
            new List<string>(cfg.Ban), maxJars: 8, maxLiquids: cfg.MaxLiquids, modifiers: modifiers);
        var jar = new Jar(shape, liquid);
        if (marked) jar.Mark();
        Assert.True(w.AddJar(jar), "罐子放不进工作台（罐位不足？）");
        foreach (var (l, n) in storage)
        {
            // 2026-11 起**禁液也能存进来**（禁令只约束取用，不约束存储）。这里只需要"存得下就存进去"；
            // 存不下的情形由 CanStore 自己拦住，不是本组测试的关注点。
            Assert.True(w.AddLiquid(l, n), "液体存不进工作台（容量不足？）");
        }
        return w;
    }

    /// <summary>候选操作的完整宇宙（不预筛：预筛会掩盖"规则少了某道门"这类错误）。</summary>
    private static List<WorkshopOperation> Universe()
    {
        var list = new List<WorkshopOperation> { WorkshopOperation.PourOut() };
        foreach (var l in Constants.LIQUIDS) list.Add(WorkshopOperation.PourIn(l));
        foreach (var to in Constants.SHAPES)
        {
            foreach (var cost in Constants.LIQUIDS) list.Add(WorkshopOperation.ReshapeEmpty(to, cost));
            list.Add(WorkshopOperation.ReshapeFull(to));
        }
        return list;
    }

    private static bool Execute(Workshop w, WorkshopOperation op)
    {
        var jar = w.Jars[0];
        return op.Kind switch
        {
            WorkshopOperationKind.PourIn => w.PourIn(jar, op.TargetLiquid!).Success,
            WorkshopOperationKind.PourOut => w.PourOut(jar).Success,
            WorkshopOperationKind.ReshapeEmpty => w.TryReshape(jar, op.TargetShape!, ReshapeMode.Empty, op.CostLiquid).Success,
            _ => w.TryReshape(jar, op.TargetShape!, ReshapeMode.Full).Success
        };
    }

    /// <summary>
    /// 三套工作台 × 八种形状 × 空/酒/蜜 × 已操作/未操作 × 五种液体账目 × 27 个候选操作，
    /// 逐个断言「规则表判定」与「真执行」一致。
    /// </summary>
    [Fact]
    public void RulebookAgreesWithExecutionOnEveryCombination()
    {
        var universe = Universe();
        var checkedCount = 0;

        foreach (var cfg in Configs)
        {
            foreach (var shape in Constants.SHAPES)
            {
                foreach (var liquid in JarLiquids)
                {
                    foreach (var marked in new[] { false, true })
                    {
                        foreach (var storage in Storages)
                        {
                            var facts = new JarFacts(shape, liquid, marked);
                            // 规则表只读取状态、不改它，因此这一份可以复用；
                            // 真执行会打标记改形状，所以每个候选操作单独建一份。
                            var probe = Build(cfg, shape, liquid, marked, storage);

                            foreach (var op in universe)
                            {
                                var expected = WorkshopRulebook.IsAllowed(
                                    probe.Rules, cfg.MaxLiquids, facts, op, probe.LiquidStorage);

                                var w = Build(cfg, shape, liquid, marked, storage);
                                var actual = Execute(w, op);

                                Assert.True(expected == actual,
                                    $"不一致：{cfg.Name} 形状={shape} 罐内={liquid ?? "空"} 已操作={marked} " +
                                    $"账目=[{string.Join(",", storage.Select(x => x.Liquid + ":" + x.Count))}] " +
                                    $"操作={op}  规则表={expected} 执行={actual}");
                                checkedCount++;
                            }
                        }
                    }
                }
            }
        }

        Assert.True(checkedCount > 15000, "对拍组合数量异常偏少：" + checkedCount);
    }

    /// <summary>枚举入口与判定入口必须自洽：<see cref="WorkshopRulebook.Available"/> 恰好是判定为真的那批。</summary>
    [Fact]
    public void AvailableListsExactlyTheAllowedSet()
    {
        var universe = Universe();

        foreach (var cfg in Configs)
        {
            foreach (var shape in Constants.SHAPES)
            {
                foreach (var liquid in JarLiquids)
                {
                    foreach (var marked in new[] { false, true })
                    {
                        foreach (var storage in Storages)
                        {
                            var facts = new JarFacts(shape, liquid, marked);
                            var w = Build(cfg, shape, liquid, marked, storage);

                            var enumerated = WorkshopRulebook
                                .Available(w.Rules, cfg.MaxLiquids, facts, w.LiquidStorage)
                                .Select(x => x.ToString())
                                .OrderBy(x => x)
                                .ToList();

                            var allowed = universe
                                .Where(op => WorkshopRulebook.IsAllowed(
                                    w.Rules, cfg.MaxLiquids, facts, op, w.LiquidStorage))
                                .Select(x => x.ToString())
                                .OrderBy(x => x)
                                .ToList();

                            Assert.Equal(allowed, enumerated);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// **容量是"每种液体各自"的**：同一种液体到上限就倒不出去，另一种液体不受影响。
    /// （原实现与契约都按这个口径；写成总数会悄悄改掉液池语义。）
    /// </summary>
    [Fact]
    public void PourOutIsBlockedExactlyWhenThatLiquidStorageIsFull()
    {
        var cfg = new Cfg { Name = "自由台", Base = new[] { "圆罐" }, MaxLiquids = 3 };
        var facts = new JarFacts("圆罐", "酒", false);
        var op = WorkshopOperation.PourOut();

        var justBelow = Build(cfg, "圆罐", "酒", false, new[] { ("酒", 2) });
        Assert.True(WorkshopRulebook.IsAllowed(justBelow.Rules, cfg.MaxLiquids, facts, op, justBelow.LiquidStorage));
        Assert.True(Execute(justBelow, op));

        var atCap = Build(cfg, "圆罐", "酒", false, new[] { ("酒", 3) });
        Assert.False(WorkshopRulebook.IsAllowed(atCap.Rules, cfg.MaxLiquids, facts, op, atCap.LiquidStorage));
        Assert.False(Execute(atCap, op));

        // 另一种液体满了不影响酒：账目里没有蜜，酒只到 2
        var otherFull = Build(cfg, "圆罐", "酒", false, new[] { ("蜜", 3) });
        Assert.True(WorkshopRulebook.IsAllowed(otherFull.Rules, cfg.MaxLiquids, facts, op, otherFull.LiquidStorage));
        Assert.True(Execute(otherFull, op));
    }

    /// <summary>抑制类修饰器：规则表与执行必须**同时**翻转，不能只有一边生效。</summary>
    [Fact]
    public void SuppressedOperationIsRefusedByBothRulebookAndExecution()
    {
        // 这一台的基础标记是 圆罐，附加标记是 尖罐 ⇒ 圆罐可以改形成尖罐（基础→附加是合法的一跳）
        var cfg = new Cfg { Name = "自由台", Base = new[] { "圆罐" }, Add = new[] { "尖罐" }, MaxLiquids = 3 };
        var facts = new JarFacts("圆罐", null, false);
        var op = WorkshopOperation.PourIn("酒");

        var plain = Build(cfg, "圆罐", null, false, new[] { ("酒", 2) });
        Assert.True(WorkshopRulebook.IsAllowed(plain.Rules, cfg.MaxLiquids, facts, op, plain.LiquidStorage));
        Assert.True(Execute(plain, op));

        var stack = new ModifierStack();
        using var scope = stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Shape = "圆罐",
            Source = "测试"
        });

        var suppressed = Build(cfg, "圆罐", null, false, new[] { ("酒", 2) }, stack);
        Assert.False(WorkshopRulebook.IsAllowed(suppressed.Rules, cfg.MaxLiquids, facts, op, suppressed.LiquidStorage));
        Assert.False(Execute(suppressed, op));

        // 只抑制"灌入"：同一个罐子的改形仍然合法（规则表没有把抑制扩大到别的操作）
        var reshape = WorkshopOperation.ReshapeEmpty("尖罐", "蜜");
        var other = Build(cfg, "圆罐", null, false, new[] { ("蜜", 2) }, stack);
        Assert.True(WorkshopRulebook.IsAllowed(other.Rules, cfg.MaxLiquids, facts, reshape, other.LiquidStorage));
        Assert.True(Execute(other, reshape));
    }
}

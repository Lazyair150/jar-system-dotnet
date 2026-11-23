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
/// <para>StateSpaceSolver 现在也通过 WorkshopRulebook 评估四种操作；本测试继续固定规则表与真实 Workshop
/// 执行结果一致。它是规则权威入口重构的差分基线，后续执行门面也会收敛到同一契约。</para>
/// </summary>
public class WorkshopRulebookTests
{

    [Fact]
    public void RuleVersionsTrackTheJarOperationBanContract()
    {
        var full = WorkshopRulebook.Definitions.Single(x => x.Operator == "RESHAPE_FULL");
        var match = WorkshopRulebook.Definitions.Single(x => x.Operator == "MATCH");

        Assert.Equal(2, WorkshopRulebook.RuleContractVersion);
        Assert.Equal("jar.workshop.reshape-full", full.RuleId);
        Assert.Equal(1, full.RuleVersion);
        Assert.Equal("jar.workshop.match", match.RuleId);
        Assert.Equal(2, match.RuleVersion);
    }


    [Fact]
    public void MatchCannotOperateOnAJarContainingAWorkshopBannedLiquid()
    {
        var capability = new WorkshopCapability(
            new[] { "圆罐", "方罐" }, new string[0], new[] { "酒" });
        var rules = RuleContext.From(capability);
        var facts = new JarFacts("圆罐", "酒", false);

        var decision = WorkshopRulebook.EvaluateMatch(rules, facts);

        Assert.False(decision.IsAllowed);
        Assert.Equal("liquid.banned", decision.ReasonCode);
        Assert.Contains("禁酒", decision.Reason);
    }

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
            // 禁液不能进入游离液体池；Jar 内部液体仍是独立物理资源。
            if (w.IsBanned(l)) continue;
            Assert.True(w.AddLiquid(l, n), "测试初始液体库存无法建立");
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

    [Fact]
    public void RuleTransitionsDescribeJarAndResourceEffectsWithoutMutatingInputs()
    {
        var capability = new WorkshopCapability(
            new[] { "圆罐" }, new[] { "尖罐" }, System.Array.Empty<string>());
        var rules = RuleContext.From(capability);

        var empty = new JarFacts("圆罐", null, false);
        var fullWine = new JarFacts("圆罐", "酒", false);
        var liquids = new Dictionary<string, int> { ["酒"] = 1, ["蜜"] = 1 };

        var pourIn = WorkshopRulebook.CreateTransition(
            rules, 3, empty, WorkshopOperation.PourIn("酒"), liquids);
        Assert.NotNull(pourIn);
        Assert.Equal("jar.workshop.pour-in", pourIn!.Value.RuleId);
        Assert.Equal(1, pourIn.Value.RuleVersion);
        Assert.Equal(new JarFacts("圆罐", "酒", true), pourIn.Value.After);
        Assert.Equal("酒", pourIn.Value.ConsumedFreeLiquid);
        Assert.Null(pourIn.Value.ProducedFreeLiquid);
        Assert.Null(pourIn.Value.ConsumedJarLiquid);

        var pourOut = WorkshopRulebook.CreateTransition(
            rules, 3, fullWine, WorkshopOperation.PourOut(), liquids);
        Assert.NotNull(pourOut);
        Assert.Equal(new JarFacts("圆罐", null, true), pourOut!.Value.After);
        Assert.Equal("酒", pourOut.Value.ProducedFreeLiquid);

        var reshapeEmpty = WorkshopRulebook.CreateTransition(
            rules, 3, empty, WorkshopOperation.ReshapeEmpty("尖罐", "蜜"), liquids);
        Assert.NotNull(reshapeEmpty);
        Assert.Equal(new JarFacts("尖罐", null, true), reshapeEmpty!.Value.After);
        Assert.Equal("蜜", reshapeEmpty.Value.ConsumedFreeLiquid);

        var reshapeFull = WorkshopRulebook.CreateTransition(
            rules, 3, fullWine, WorkshopOperation.ReshapeFull("尖罐"), liquids);
        Assert.NotNull(reshapeFull);
        Assert.Equal(new JarFacts("尖罐", null, true), reshapeFull!.Value.After);
        Assert.Equal("酒", reshapeFull.Value.ConsumedJarLiquid);

        // 转换描述是纯值结果；调用前的输入库存不会被规则评估修改。
        Assert.Equal(1, liquids["酒"]);
        Assert.Equal(1, liquids["蜜"]);
    }

    [Fact]
    public void WorkshopExecutionRejectsFailedRequireWithoutCommittingResources()
    {
        var stack = new ModifierStack();
        using var scope = stack.Push(new RequireModifier
        {
            Name = "gate",
            Source = "test",
            Text = "gate must be open",
            Violation = () => "gate is closed"
        });
        var cfg = new Cfg { Name = "test", Base = new[] { "圆罐" } };
        var workshop = Build(cfg, "圆罐", null, false, new[] { ("酒", 1) }, stack);
        var jar = workshop.Jars[0];

        var result = workshop.PourIn(jar, "酒");

        Assert.False(result.Success);
        Assert.Equal("gate is closed", result.Reason);
        Assert.Null(jar.Liquid);
        Assert.Equal(1, workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void RuleCatalogHasUniqueStableIdsAndVersionsForEveryWorkshopOperator()
    {
        var definitions = WorkshopRulebook.Definitions;
        Assert.Equal(5, definitions.Count);
        Assert.Equal(definitions.Count, definitions.Select(x => x.RuleId).Distinct().Count());
        Assert.All(definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.RuleId));
            Assert.True(definition.RuleVersion > 0);
            Assert.False(string.IsNullOrWhiteSpace(definition.Operator));
        });
        Assert.Contains(definitions, x => x.Operator == "MATCH" &&
            x.Category == WorkshopRuleCategory.Lifecycle);
        Assert.Equal(4, definitions.Count(x => x.Category == WorkshopRuleCategory.Transform));
    }

    [Fact]
    public void RuleEvaluationUsesStableDecisionCodesAndKeepsUnknownSeparate()
    {
        var capability = new WorkshopCapability(
            new[] { "圆罐" }, new[] { "尖罐" }, System.Array.Empty<string>());
        var facts = new JarFacts("圆罐", null, false);
        var op = WorkshopOperation.PourIn("酒");
        var liquids = new Dictionary<string, int> { ["酒"] = 1 };

        var allowed = WorkshopRulebook.Evaluate(
            RuleContext.From(capability), 3, facts, op, liquids);
        Assert.Equal(WorkshopRuleDecisionKind.Allowed, allowed.Kind);
        Assert.Equal("rule.allowed", allowed.ReasonCode);

        var bannedCapability = new WorkshopCapability(
            new[] { "圆罐" }, new[] { "尖罐" }, new[] { "酒" });
        var rejected = WorkshopRulebook.Evaluate(
            RuleContext.From(bannedCapability), 3, facts, op, liquids);
        Assert.Equal(WorkshopRuleDecisionKind.Rejected, rejected.Kind);
        Assert.Equal("liquid.unavailable", rejected.ReasonCode);

        var missingPredicateStack = new ModifierStack();
        using (missingPredicateStack.Push(new RequireModifier
        {
            Name = "missing-predicate",
            Source = "test",
            Text = "external world condition"
        }))
        {
            var unknown = WorkshopRulebook.Evaluate(
                new RuleContext(capability, missingPredicateStack), 3, facts, op, liquids);
            Assert.Equal(WorkshopRuleDecisionKind.Undetermined, unknown.Kind);
            Assert.Equal("modifier.required-predicate-unavailable", unknown.ReasonCode);
        }

        var failedPredicateStack = new ModifierStack();
        using (failedPredicateStack.Push(new RequireModifier
        {
            Name = "closed-gate",
            Source = "test",
            Text = "gate must be open",
            Violation = () => "gate is closed"
        }))
        {
            var failed = WorkshopRulebook.Evaluate(
                new RuleContext(capability, failedPredicateStack), 3, facts, op, liquids);
            Assert.Equal(WorkshopRuleDecisionKind.Rejected, failed.Kind);
            Assert.Equal("modifier.required-condition-failed", failed.ReasonCode);
            Assert.Equal("gate is closed", failed.Reason);
        }
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

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 订单模板库（<see cref="OrderTemplateLibrary"/> / <see cref="OrderTemplate"/>）。
///
/// <para>它补的是一个**概念空缺**：改前系统只有"订单实例"，"这个世界可派什么单"没有落点 ——
/// 既不在配置里，也不在数据文件里，只能活在调用方代码里。这一层测试同时守两件事：
/// 模板是**不可变内容**（不能被某次派单改脏），以及**坏配置在装配时就报**而不是等派单才炸。</para>
/// </summary>
public class OrderTemplateTests
{
    private static JarSystem.Core.JarSystem NewWorld()
        => new(randomSeed: 3, jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());

    // ---------------------------------------------------------------- 内置世界

    [Fact]
    public void BuiltInWorldExposesTheConfiguredTemplatesInOrder()
    {
        var sys = NewWorld();
        var names = sys.OrderTemplates.Names;

        Assert.Equal(new[] { "空双罐", "灌酒圆罐", "三角改形", "主从高长", "三件套" }, names);
        Assert.Equal(5, sys.OrderTemplates.Count);

        // 类型必须与配置一致：前三个 A，后两个 B。
        Assert.Equal(Constants.OrderType.A, sys.OrderTemplates.Get(0).Type);
        Assert.Equal(Constants.OrderType.A, sys.OrderTemplates.Get(2).Type);
        Assert.Equal(Constants.OrderType.B, sys.OrderTemplates.Get(3).Type);
        Assert.Equal(Constants.OrderType.B, sys.OrderTemplates.Get(4).Type);

        // 每条模板自身仍然是合法的需求列表（与配置逐值一致）。
        Assert.Equal(2, sys.OrderTemplates.IndexOf("三角改形"));
        Assert.Equal(2, sys.OrderTemplates.Get(2).Requirements.Count);
        Assert.Null(sys.OrderTemplates.Get(2).Requirements[0].Liquid);
        Assert.Equal("三角罐", sys.OrderTemplates.Get(2).Requirements[1].Shape);
    }

    /// <summary>模板不可变、实例化不共享：改一张订单的需求不能影响模板或另一张订单。</summary>
    [Fact]
    public void InstantiationGivesIndependentRequirementLists()
    {
        var sys = NewWorld();
        var template = sys.OrderTemplates.Get(1);   // 灌酒圆罐：圆罐(空) + 圆罐:酒

        var a = template.Instantiate();
        var b = template.Instantiate();

        Assert.NotSame(a, b);
        Assert.NotSame(a[0], b[0]);

        // Requirement 的属性是 init-only（构造后不可改），所以"改脏"只能发生在**列表**这一层：
        // 换掉一个元素。这正是模板必须每次 Instantiate 出新列表的理由。
        a[0] = new Requirement { Shape = "被改脏了" };
        a.RemoveAt(1);

        Assert.Equal("圆罐", template.Requirements[0].Shape);   // 模板没被改
        Assert.Equal("酒", template.Requirements[1].Liquid);
        Assert.Equal("圆罐", b[0].Shape);                       // 另一份也没被改
        Assert.Equal("酒", b[1].Liquid);
        Assert.Equal(2, b.Count);
    }

    // ---------------------------------------------------------------- 派单

    [Fact]
    public void CreateOrderFromTemplateMakesARealOrder()
    {
        var sys = NewWorld();
        var before = sys.Orders.Count;

        var (ok, err) = sys.CreateOrderFromTemplate("主从高长");

        Assert.True(ok, err);
        Assert.Equal(before + 1, sys.Orders.Count);
        var order = sys.Orders[^1];
        Assert.Equal(Constants.OrderType.B, order.Type);
        Assert.Equal(2, order.Requirements.Count);
        Assert.Equal("高罐", order.Requirements[0].Shape);
        Assert.Equal("长罐", order.Requirements[1].Shape);
        Assert.Equal("蜜", order.Requirements[1].Liquid);
        Assert.Equal(Constants.OrderStatus.PENDING, order.Status);
    }

    [Fact]
    public void UnknownTemplateNameFailsWithAReadableReasonAndAddsNothing()
    {
        var sys = NewWorld();
        var before = sys.Orders.Count;

        var (ok, err) = sys.CreateOrderFromTemplate("不存在的模板");

        Assert.False(ok);
        Assert.Contains("不存在的模板", err);
        Assert.Equal(before, sys.Orders.Count);
    }

    // ---------------------------------------------------------------- 随机派单的两种模式

    /// <summary>模板库非空时，随机派单只能从库里抽 —— 订单是"世界内容里有的东西"。</summary>
    [Fact]
    public void RandomOrderComesFromTheLibraryWhenItIsNotEmpty()
    {
        var sys = NewWorld();
        var byName = sys.OrderTemplates.Templates.ToDictionary(t => t.Name, t => t);

        for (int i = 0; i < 40; i++)
        {
            var (ok, err) = sys.CreateRandomOrder();
            Assert.True(ok, err);

            var order = sys.Orders[^1];
            var match = byName.Values.Any(t =>
                t.Type == order.Type &&
                t.Requirements.Count == order.Requirements.Count &&
                t.Requirements.Zip(order.Requirements).All(p =>
                    p.First.Shape == p.Second.Shape && p.First.Liquid == p.Second.Liquid));

            Assert.True(match,
                $"第 {i + 1} 张随机订单不是任何模板的实例：" +
                string.Join(", ", order.Requirements.Select(r => r.Shape + "/" + (r.Liquid ?? "空"))));
        }
    }

    /// <summary>同一份种子 + 同一串命令 ⇒ 抽出同一批模板（这条命令不记参数，只能靠 Rng 复现）。</summary>
    [Fact]
    public void RandomOrdersAreReproducibleForAGivenSeed()
    {
        static string Fingerprint()
        {
            var sys = new JarSystem.Core.JarSystem(randomSeed: 424242,
                jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());
            for (int i = 0; i < 12; i++) sys.CreateRandomOrder();
            return string.Join("|", sys.Orders.Select(o =>
                o.Type + ":" + string.Join(",", o.Requirements.Select(r => r.Shape + "/" + (r.Liquid ?? "-")))));
        }

        Assert.Equal(Fingerprint(), Fingerprint());
    }

    /// <summary>库空时退回旧逻辑：仍然造得出订单，且形状/液体都来自词表。</summary>
    [Fact]
    public void EmptyLibraryFallsBackToSynthesisedRequirements()
    {
        // 手工给一份"只有词表、没有模板"的世界配置（不改动共享的 Default 单例）。
        var world = new WorldOptions
        {
            Shapes = new List<string>(Constants.SHAPES),
            Liquids = new List<string>(Constants.LIQUIDS)
        };
        var sys = new JarSystem.Core.JarSystem(world: world, randomSeed: 7,
            jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());

        Assert.True(sys.OrderTemplates.IsEmpty);

        var (ok, err) = sys.CreateRandomOrder();
        Assert.True(ok, err);
        var order = sys.Orders[^1];
        Assert.InRange(order.Requirements.Count, 1, 3);
        Assert.Contains(order.Type, new[] { Constants.OrderType.A, Constants.OrderType.B });
        Assert.All(order.Requirements, r => Assert.Contains(r.Shape, world.Shapes));
    }

    // ---------------------------------------------------------------- 坏配置要在装配时报

    private static OrderTemplateOptions Option(string name, string type, params (string Shape, string? Liquid)[] reqs)
    {
        var o = new OrderTemplateOptions { Name = name, Type = type };
        foreach (var (shape, liquid) in reqs)
            o.Requirements.Add(new RequirementOptions { Shape = shape, Liquid = liquid });
        return o;
    }

    private static readonly string[] Shapes = { "圆罐", "方罐", "三角罐" };
    private static readonly string[] Liquids = { "酒", "蜜" };

    [Fact]
    public void BadTemplateConfigIsRejectedAtBuildTime()
    {
        // 名字空
        Assert.Throws<DomainValidationException>(() => OrderTemplateLibrary.FromOptions(
            new[] { Option("  ", "A", ("圆罐", null)) }, Shapes, Liquids));

        // 名字重复
        Assert.Throws<DomainValidationException>(() => OrderTemplateLibrary.FromOptions(
            new[] { Option("甲", "A", ("圆罐", null)), Option("甲", "A", ("方罐", null)) }, Shapes, Liquids));

        // 类型不是 A/B
        var badType = Assert.Throws<DomainValidationException>(() => OrderTemplateLibrary.FromOptions(
            new[] { Option("甲", "C", ("圆罐", null)) }, Shapes, Liquids));
        Assert.Contains("A", badType.Message);
        Assert.Contains("B", badType.Message);

        // 一条需求都没有
        Assert.Throws<DomainValidationException>(() => OrderTemplateLibrary.FromOptions(
            new[] { new OrderTemplateOptions { Name = "空", Type = "A" } }, Shapes, Liquids));

        // 超过需求上限
        var tooMany = Enumerable.Range(0, Constants.MAX_ORDER_REQUIREMENTS + 1)
            .Select(_ => ("圆罐", (string?)null)).ToArray();
        Assert.Throws<DomainValidationException>(() => OrderTemplateLibrary.FromOptions(
            new[] { Option("超长", "A", tooMany) }, Shapes, Liquids));

        // 形状不在词表
        var badShape = Assert.Throws<DomainValidationException>(() => OrderTemplateLibrary.FromOptions(
            new[] { Option("甲", "A", ("不存在的形状", null)) }, Shapes, Liquids));
        Assert.Contains("形状", badShape.Message);

        // 液体不在词表
        var badLiquid = Assert.Throws<DomainValidationException>(() => OrderTemplateLibrary.FromOptions(
            new[] { Option("甲", "A", ("圆罐", "醋")) }, Shapes, Liquids));
        Assert.Contains("液体", badLiquid.Message);

        // 空罐需求排在满罐需求之后（Order 构造函数的顺序不变量）
        var badOrder = Assert.Throws<DomainValidationException>(() => OrderTemplateLibrary.FromOptions(
            new[] { Option("甲", "A", ("圆罐", "酒"), ("方罐", null)) }, Shapes, Liquids));
        Assert.Contains("空罐在前", badOrder.Message);

        // 反过来（空在前、满在后）必须放行
        var ok = OrderTemplateLibrary.FromOptions(
            new[] { Option("甲", "A", ("方罐", null), ("圆罐", "酒")) }, Shapes, Liquids);
        Assert.Equal(1, ok.Count);
    }

    [Fact]
    public void EmptyConfigGivesTheSharedEmptyLibrary()
    {
        Assert.Same(OrderTemplateLibrary.Empty, OrderTemplateLibrary.FromOptions(null));
        Assert.Same(OrderTemplateLibrary.Empty, OrderTemplateLibrary.FromOptions(Array.Empty<OrderTemplateOptions>()));
        Assert.True(OrderTemplateLibrary.Empty.IsEmpty);
        Assert.Equal(0, OrderTemplateLibrary.Empty.Count);
        Assert.False(OrderTemplateLibrary.Empty.TryGet("任意", out _));
        Assert.Equal(-1, OrderTemplateLibrary.Empty.IndexOf("任意"));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderTemplateLibrary.Empty.Get(0));
    }

    /// <summary>名字查找容忍首尾空白（名字来自外部输入），但库里存的是修整后的名字。</summary>
    [Fact]
    public void NameLookupTrimsWhitespace()
    {
        var lib = OrderTemplateLibrary.FromOptions(
            new[] { Option("  甲  ", "A", ("圆罐", null)) }, Shapes, Liquids);

        Assert.Equal(1, lib.Count);
        Assert.Equal("甲", lib.Get(0).Name);
        Assert.True(lib.TryGet("甲", out var t));
        Assert.Equal("甲", t.Name);
        Assert.True(lib.TryGet("  甲  ", out _));
    }
}

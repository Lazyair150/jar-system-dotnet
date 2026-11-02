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
/// 两条「规则写了、代码实现了、检查缺席」的规则，补上门禁。
///
/// 1 <c>规则.txt:29-30</c>「订单需求数量上限：10 个（用于约束可行性分析的搜索规模）」
///   —— 实现住在 <see cref="Order"/> 构造函数（<c>Orders/Order.cs:66</c>），
///   但 2026-11 之前 <c>test/</c> 里没有任何一条测试碰过它。上限一改就无人察觉。
/// 2 <c>规则.txt:33-37</c>「失败要付代价（设计定案，2026-09 确认）」：
///   订单执行失败时**主台上已经发生的操作不回滚** —— 已改形的罐子保持新形状、
///   已消耗的成本液体不返还、已倒出的液体留在工作台（操作标记会被清除）；
///   只有「跨工作台的转移」会被回滚。规则里还特意写了「**不要**当缺陷修掉」，
///   所以这里测的就是"它确实不回滚"，而不是"它应该回滚得更干净"。
/// </summary>
public class OrderLimitAndFailureCostTests
{
    private static Vocabulary Vocab() => new(new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" },
        new[] { "酒", "蜜" });

    private static List<Requirement> EmptyReqs(int n)
    {
        var shapes = new[] { "圆罐", "方罐" };
        return Enumerable.Range(0, n).Select(i => new Requirement { Shape = shapes[i % shapes.Length], Liquid = null }).ToList();
    }

    // ---------------- ① 需求上限 10 ----------------

    [Fact]
    public void TenRequirementsAreAccepted()
    {
        // 边界要"刚好能用"，否则上限就成了摆设：10 条必须能建出来。
        var reqs = EmptyReqs(Constants.MAX_ORDER_REQUIREMENTS);
        var order = new Order(reqs, Constants.OrderType.A);
        Assert.Equal(Constants.MAX_ORDER_REQUIREMENTS, order.Requirements.Count);
    }

    [Fact]
    public void ElevenRequirementsAreRejectedWithAReadableReason()
    {
        var reqs = EmptyReqs(Constants.MAX_ORDER_REQUIREMENTS + 1);
        var ex = Assert.Throws<DomainValidationException>(
            () => new Order(reqs, Constants.OrderType.A));
        Assert.Contains(Constants.MAX_ORDER_REQUIREMENTS.ToString(), ex.Message);
    }

    [Fact]
    public void TheLimitIsARealConstantNotACopiedNumber()
    {
        // 这条测试防的是"文档写 10、代码写 12"这类漂移：
        // 断言的对象必须是 `Constants.MAX_ORDER_REQUIREMENTS` 本身，而不是字面量 10。
        Assert.Equal(10, Constants.MAX_ORDER_REQUIREMENTS);
    }

    [Fact]
    public void FacadeRefusesOverLimitOrderWithoutLeavingAHalfOrder()
    {
        var sys = new JarSystem.Core.JarSystem(randomSeed: 7);
        var before = sys.Orders.Count;
        var r = sys.CreateOrder(EmptyReqs(Constants.MAX_ORDER_REQUIREMENTS + 1));
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
        Assert.Equal(before, sys.Orders.Count);   // 不许留下"半个订单"
    }

    // ---------------- ② 失败要付代价：主台不回滚 ----------------

    /// <summary>
    /// 这条测试用**领域层原语**直接摆出"主台上已经发生过一次操作"的世界，
    /// 然后走 `OrderEvent` 的失败路径，断言主台的状态**没有被还原**。
    ///
    /// 为什么不走门面执行一单失败的订单：那需要精确构造"前几条需求可做、后面做不到"的世界，
    /// 一旦求解器或贪心兜底的策略变化，用例就会因为"计划变了"而不是"语义变了"而红。
    /// 这里钉的是**语义**：失败路径不得回滚主台。
    /// </summary>
    [Fact]
    public void FailingAnOrderLeavesMasterBenchOperationsInPlace()
    {
        var vocab = Vocab();
        var master = new Workshop("主台",
            new List<string> { "圆罐", "方罐" },
            new List<string> { "三角罐" },
            new List<string>(),
            maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());

        var jar = new Jar("圆罐", null, null, vocab);
        var jar2 = new Jar("圆罐", null, null, vocab);
        master.AddJar(jar);
        master.AddJar(jar2);
        master.AddLiquid("蜜", 2);

        // 主台上"已经发生"的两件事：一次改形（付掉 1 份蜜）、一次灌液。
        // 注意「一罐一操作」：ReshapeEmpty 会给罐子打标记，所以灌液必须用另一只罐子 ——
        // 这不是测试的将就，而是领域规则本身（见 `_test_engine.js` 的同名约束）。
        var (reshaped, reshapeReason) = master.ReshapeEmpty(jar, "三角罐", "蜜");
        Assert.True(reshaped, "改形失败：" + reshapeReason);
        var (poured, pourReason) = master.PourIn(jar2, "蜜");
        Assert.True(poured, "灌液失败：" + pourReason);

        var shapeAfterOps = jar.Shape;
        var liquidAfterOps = master.LiquidStorage.TryGetValue("蜜", out var v) ? v : 0;
        Assert.Equal("三角罐", shapeAfterOps);
        // 2 份蜜：1 份当改形成本被吃掉，1 份被灌进 jar2 —— 所以液池剩 0。
        // 这不是笔误，是这两条规则叠在一起的必然结果（灌液是从池子里搬到罐子里）。
        Assert.Equal(0, liquidAfterOps);
        Assert.Equal("蜜", jar2.Liquid);          // 另一只罐子被灌满

        // 失败路径：OrderEvent 失败时只回滚"跨台转移"，主台一概不动。
        var slave = new Workshop("从台",
            new List<string> { "圆罐" }, new List<string>(), new List<string>(),
            maxJars: 4, maxLiquids: 5, ids: new SequentialIdGenerator());
        var order = new Order(EmptyReqs(2), Constants.OrderType.A);
        var ev = new OrderEvent(order, master, slave);
        Assert.True(ev.Start());
        ev.Fail("测试：故意失败");

        Assert.Equal(shapeAfterOps, jar.Shape);                                   // 形状不回滚
        Assert.Equal("蜜", jar2.Liquid);                                          // 灌进去的液体还在罐里
        var liquidAfterFail = master.LiquidStorage.TryGetValue("蜜", out var v2) ? v2 : 0;
        Assert.Equal(liquidAfterOps, liquidAfterFail);                            // 成本液体不返还
        Assert.False(jar.IsMarked);                                               // 标记被清除，场地能重新开工
    }

    [Fact]
    public void BeginningAnOrderEventClearsStaleMarks()
    {
        // 规则说失败后"操作标记会被清除"。标记残留会让下一单以为罐子已被占用而白白失败，
        // 所以这一条是「失败要付代价」的另一半：代价付掉，但场地要能重新开工。
        var vocab = Vocab();
        var jar = new Jar("圆罐", null, null, vocab);
        var slave = new Workshop("从台",
            new List<string> { "圆罐" }, new List<string>(), new List<string>(),
            maxJars: 4, maxLiquids: 5, ids: new SequentialIdGenerator());
        var master = new Workshop("主台",
            new List<string> { "圆罐" }, new List<string>(), new List<string>(),
            maxJars: 6, maxLiquids: 5, ids: new SequentialIdGenerator());
        master.AddJar(jar);
        var ev = new OrderEvent(new Order(EmptyReqs(1), Constants.OrderType.A), master, slave);
        jar.Mark();
        Assert.True(jar.IsMarked);
        Assert.True(ev.Start());
        Assert.False(jar.IsMarked);
    }
}

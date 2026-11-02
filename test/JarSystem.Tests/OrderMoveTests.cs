using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Services;
using JarSystem.Core.Sessions;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 订单执行顺序（`moveOrder`）的契约测试。
///
/// 为什么这件事必须住在内核、而不能只是页面上的一个数组：
/// **执行顺序决定谁先拿走稀缺的罐子与液体**（demo 第六关「抢资源」就是靠这个翻转结果）。
/// 若它只存在于某个页面的内存里，换个客户端就是另一个世界，回放也无法复现。
///
/// 这组测试钉四件事：
/// 1 移动真的改变列表顺序（不是改了一个没人读的字段）；
/// 2 越界是**无操作**（返回 false），而且不往命令日志里写一条空意图；
/// 3 未知 orderId 必须报错，不能被静默忽略；
/// 4 移动不改变订单身份（Id 与需求跟着订单走，不跟着位置走）。
/// </summary>
public class OrderMoveTests
{
    private static JarSystem.Core.JarSystem NewSystem()
    {
        var sys = new JarSystem.Core.JarSystem(
            randomSeed: 11,
            jarIds: new SequentialIdGenerator(),
            orderIds: new SequentialIdGenerator());
        return sys;
    }

    private static List<Requirement> Ok(string shape) =>
        new() { new Requirement { Shape = shape, Liquid = null } };

    private static JarSystem.Core.JarSystem WithThreeOrders(out int a, out int b, out int c)
    {
        var sys = NewSystem();
        Assert.True(sys.CreateOrder(Ok("圆罐")).Success);
        Assert.True(sys.CreateOrder(Ok("方罐")).Success);
        Assert.True(sys.CreateOrder(Ok("高罐")).Success);
        a = sys.Orders[0].Id;
        b = sys.Orders[1].Id;
        c = sys.Orders[2].Id;
        return sys;
    }

    [Fact]
    public void MovingAnOrderUpOrDownChangesTheListOrder()
    {
        var sys = WithThreeOrders(out var a, out var b, out var c);

        Assert.True(sys.MoveOrder(b, -1));                 // 第二单上移 ⇒ 变成第一
        Assert.Equal(new[] { b, a, c }, sys.Orders.Select(o => o.Id).ToArray());

        Assert.True(sys.MoveOrder(b, +1));                 // 再下移 ⇒ 回到原位
        Assert.Equal(new[] { a, b, c }, sys.Orders.Select(o => o.Id).ToArray());
    }

    [Fact]
    public void IdentityFollowsTheOrderNotThePosition()
    {
        var sys = WithThreeOrders(out var a, out var b, out var c);
        var firstRequirements = sys.Orders[0].Requirements.Select(r => r.Shape).ToList();

        sys.MoveOrder(c, -1);
        sys.MoveOrder(c, -1);                              // 把最后一单推到最前

        Assert.Equal(new[] { c, a, b }, sys.Orders.Select(o => o.Id).ToArray());
        var moved = sys.Orders[0];
        Assert.Equal(c, moved.Id);
        // 身份稳定：被移到最前的仍是最初那张"高罐"单，不是别单的壳
        Assert.Equal("高罐", moved.Requirements[0].Shape);
        // 原本的第一单内容不变
        Assert.Equal(firstRequirements, sys.Orders[1].Requirements.Select(r => r.Shape).ToList());
    }

    [Fact]
    public void MovingPastTheEdgeIsANoOpAndWritesNoLogEntry()
    {
        var sys = WithThreeOrders(out var a, out _, out _);
        var before = sys.Orders.Select(o => o.Id).ToArray();
        var logBefore = sys.CommandLog.Count;

        Assert.False(sys.MoveOrder(a, -1));                // 已经在最前
        Assert.False(sys.MoveOrder(sys.Orders[2].Id, +1)); // 已经在最后

        Assert.Equal(before, sys.Orders.Select(o => o.Id).ToArray());
        Assert.Equal(logBefore, sys.CommandLog.Count);     // 空意图不进日志（否则回放会重放它）
    }

    [Fact]
    public void AnUnknownOrderIdIsRejected()
    {
        var sys = WithThreeOrders(out _, out _, out _);
        var ex = Assert.Throws<DomainValidationException>(() => sys.MoveOrder(9999, -1));
        Assert.Contains("无效订单 id", ex.Message);
    }

    [Fact]
    public void AMoveIsRecordedAndKeepsTheWorldConsistent()
    {
        var sys = WithThreeOrders(out _, out var b, out _);
        var logBefore = sys.CommandLog.Count;

        Assert.True(sys.MoveOrder(b, -1));

        Assert.Equal(logBefore + 1, sys.CommandLog.Count);
        Assert.Equal(WorldOps.MoveOrder, sys.CommandLog[sys.CommandLog.Count - 1].Op);
        Assert.Empty(WorldInvariants.Check(sys));
    }
}

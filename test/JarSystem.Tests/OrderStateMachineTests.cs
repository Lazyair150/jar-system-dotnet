using System;
using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using Xunit;

namespace JarSystem.Tests;

public class OrderStateMachineTests
{
    private static Order NewOrder() => new(
        [new Requirement { Shape = "圆罐" }],
        Constants.OrderType.A,
        vocabulary: Vocabulary.Default);

    [Fact]
    public void ExecutionStartMarksTheOrderExecuting()
    {
        var order = NewOrder();
        var workshop = new Workshop("主台", ["圆罐"], [], [], 20, 30, vocabulary: Vocabulary.Default);
        workshop.AddJar(new Jar("圆罐", vocabulary: Vocabulary.Default));
        var execution = new OrderEvent(order, workshop, workshop);

        execution.Start();

        Assert.Equal(OrderLifecycleState.Executing, order.Lifecycle);
        Assert.Equal(Constants.OrderStatus.EXECUTING, order.Status);
        Assert.Equal(order.Status, execution.Status);
    }

    [Fact]
    public void FailureMovesExecutingOrderToFailed()
    {
        var order = NewOrder();
        var workshop = new Workshop("主台", ["圆罐"], [], [], 20, 30, vocabulary: Vocabulary.Default);
        var execution = new OrderEvent(order, workshop, workshop);
        execution.Start();

        _ = execution.Fail("测试失败");

        Assert.Equal(OrderLifecycleState.Failed, order.Lifecycle);
        Assert.Equal(Constants.OrderStatus.FAILED, order.Status);
    }

    [Fact]
    public void PendingOrderCannotSkipDirectlyToSuccessOrFailure()
    {
        var order = NewOrder();

        Assert.Throws<InvalidOperationException>(() => order.MarkSucceeded());
        Assert.Throws<InvalidOperationException>(() => order.MarkFailed());
        Assert.Equal(OrderLifecycleState.Pending, order.Lifecycle);
    }
}

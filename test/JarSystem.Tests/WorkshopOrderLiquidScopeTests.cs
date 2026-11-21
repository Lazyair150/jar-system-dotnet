using System.Collections.Generic;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class WorkshopOrderLiquidScopeTests
{
    private static Workshop CreateWorkshop(
        IEnumerable<string>? baseMarks = null,
        IEnumerable<string>? additionalMarks = null,
        IEnumerable<string>? ban = null)
    {
        return new Workshop(
            "订单测试台",
            baseMarks is null ? new List<string> { "圆罐" } : new List<string>(baseMarks),
            additionalMarks is null ? new List<string> { "尖罐" } : new List<string>(additionalMarks),
            ban is null ? new List<string>() : new List<string>(ban),
            maxJars: 8,
            maxLiquids: 3);
    }

    [Fact]
    public void PourInConsumesOnlyTheSelectedOrderAccount()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐");
        Assert.True(workshop.AddJar(jar));

        var staging = new WorkshopLiquidStaging(8);
        staging.Receive(101, "酒", 2);
        staging.Receive(202, "酒", 3);
        var scope = new OrderLiquidScope(101, staging);

        var result = workshop.PourIn(jar, "酒", scope);

        Assert.True(result.Success);
        Assert.Equal("酒", jar.Liquid);
        Assert.Equal(1, staging.GetAmount(101, "酒"));
        Assert.Equal(3, staging.GetAmount(202, "酒"));
    }

    [Fact]
    public void PourOutRequiresBanAndAggregateCapacityToAllowExactReceive()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", "酒");
        Assert.True(workshop.AddJar(jar));

        var staging = new WorkshopLiquidStaging(2);
        staging.Receive(202, "蜜", 2);
        var scope = new OrderLiquidScope(101, staging);

        var blocked = workshop.PourOut(jar, scope);
        Assert.False(blocked.Success);
        Assert.Equal("酒", jar.Liquid);
        Assert.Equal(2, staging.UsedCapacity);

        Assert.True(staging.TryConsume(202, "蜜", 1));
        var allowed = workshop.PourOut(jar, scope);
        Assert.True(allowed.Success);
        Assert.Null(jar.Liquid);
        Assert.Equal(1, staging.GetAmount(101, "酒"));
        Assert.Equal(1, staging.GetAmount(202, "蜜"));
    }

    [Fact]
    public void PourOutCannotBypassWorkshopBan()
    {
        var workshop = CreateWorkshop(ban: new[] { "酒" });
        var jar = new Jar("圆罐", "酒");
        Assert.True(workshop.AddJar(jar));
        var staging = new WorkshopLiquidStaging(4);
        var scope = new OrderLiquidScope(101, staging);

        var result = workshop.PourOut(jar, scope);

        Assert.False(result.Success);
        Assert.Equal("酒", jar.Liquid);
        Assert.Equal(0, staging.UsedCapacity);
    }

    [Fact]
    public void EmptyReshapeConsumesOnlyCurrentOrderLiquid()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐");
        Assert.True(workshop.AddJar(jar));
        var staging = new WorkshopLiquidStaging(8);
        staging.Receive(101, "酒", 2);
        staging.Receive(202, "酒", 2);
        var scope = new OrderLiquidScope(101, staging);

        Assert.True(workshop.CanReshapeTo(jar, "尖罐", scope).Granted);
        var result = workshop.TryReshape(jar, "尖罐", scope, ReshapeMode.Empty, "酒");

        Assert.True(result.Success);
        Assert.Equal("尖罐", jar.Shape);
        Assert.Equal(1, staging.GetAmount(101, "酒"));
        Assert.Equal(2, staging.GetAmount(202, "酒"));
    }

    [Fact]
    public void FullReshapeConsumesJarLiquidWithoutTouchingOrderStaging()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", "酒");
        Assert.True(workshop.AddJar(jar));
        var staging = new WorkshopLiquidStaging(8);
        staging.Receive(101, "蜜", 2);
        var scope = new OrderLiquidScope(101, staging);

        var result = workshop.TryReshape(jar, "尖罐", scope, ReshapeMode.Full);

        Assert.True(result.Success);
        Assert.Equal("尖罐", jar.Shape);
        Assert.Null(jar.Liquid);
        Assert.Equal(2, staging.GetAmount(101, "蜜"));
    }

    [Fact]
    public void OrderScopedRulebookMatchesWorkshopExecutionForFourOperations()
    {
        AssertParity(
            new Jar("圆罐"),
            WorkshopOperation.PourIn("酒"),
            staging => staging.Receive(101, "酒", 1));
        AssertParity(
            new Jar("圆罐", "酒"),
            WorkshopOperation.PourOut(),
            _ => { });
        AssertParity(
            new Jar("圆罐"),
            WorkshopOperation.ReshapeEmpty("尖罐", "酒"),
            staging => staging.Receive(101, "酒", 1));
        AssertParity(
            new Jar("圆罐", "酒"),
            WorkshopOperation.ReshapeFull("尖罐"),
            _ => { });
    }

    [Fact]
    public void OrderScopedRulebookUsesAggregateCapacityAndBanForPourOut()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", "酒");
        Assert.True(workshop.AddJar(jar));
        var staging = new WorkshopLiquidStaging(2);
        staging.Receive(202, "蜜", 2);
        var scope = new OrderLiquidScope(101, staging);

        Assert.False(WorkshopRulebook.IsAllowed(
            workshop.Rules, scope, JarFacts.Of(jar), WorkshopOperation.PourOut()));

        var bannedWorkshop = CreateWorkshop(ban: new[] { "酒" });
        var bannedJar = new Jar("圆罐", "酒");
        Assert.True(bannedWorkshop.AddJar(bannedJar));
        Assert.False(WorkshopRulebook.IsAllowed(
            bannedWorkshop.Rules, scope, JarFacts.Of(bannedJar), WorkshopOperation.PourOut()));
    }


    [Fact]
    public void ExplicitEmptyReshapeCostCannotBypassWorkshopBan()
    {
        var workshop = CreateWorkshop(ban: new[] { "酒" });
        var jar = new Jar("圆罐");
        Assert.True(workshop.AddJar(jar));
        var staging = new WorkshopLiquidStaging(4);
        staging.Receive(101, "酒", 1);
        var scope = new OrderLiquidScope(101, staging);

        var result = workshop.TryReshape(jar, "尖罐", scope, ReshapeMode.Empty, "酒");

        Assert.False(result.Success);
        Assert.Equal("圆罐", jar.Shape);
        Assert.Equal(1, staging.GetAmount(101, "酒"));
    }

    [Fact]
    public void StagingBanIsAlsoRespectedByPourInAndRulebook()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐");
        Assert.True(workshop.AddJar(jar));
        var staging = new WorkshopLiquidStaging(4, new[] { "酒" });
        staging.Receive(101, "酒", 1); // 被暂存区拒收，不能伪造初始余额
        var scope = new OrderLiquidScope(101, staging);

        Assert.False(WorkshopRulebook.IsAllowed(
            workshop.Rules, scope, JarFacts.Of(jar), WorkshopOperation.PourIn("酒")));
        Assert.False(workshop.PourIn(jar, "酒", scope).Success);
    }

    private static void AssertParity(
        Jar initialJar,
        WorkshopOperation operation,
        System.Action<WorkshopLiquidStaging> seed)
    {
        var ruleWorkshop = CreateWorkshop();
        var ruleJar = initialJar.DeepClone();
        Assert.True(ruleWorkshop.AddJar(ruleJar));
        var ruleStaging = new WorkshopLiquidStaging(8);
        seed(ruleStaging);
        var ruleScope = new OrderLiquidScope(101, ruleStaging);

        var allowed = WorkshopRulebook.IsAllowed(
            ruleWorkshop.Rules, ruleScope, JarFacts.Of(ruleJar), operation);

        var executionWorkshop = CreateWorkshop();
        var executionJar = initialJar.DeepClone();
        Assert.True(executionWorkshop.AddJar(executionJar));
        var executionStaging = new WorkshopLiquidStaging(8);
        seed(executionStaging);
        var executionScope = new OrderLiquidScope(101, executionStaging);

        var result = operation.Kind switch
        {
            WorkshopOperationKind.PourIn =>
                executionWorkshop.PourIn(executionJar, operation.TargetLiquid!, executionScope).Success,
            WorkshopOperationKind.PourOut =>
                executionWorkshop.PourOut(executionJar, executionScope).Success,
            WorkshopOperationKind.ReshapeEmpty =>
                executionWorkshop.TryReshape(executionJar, operation.TargetShape!, executionScope,
                    ReshapeMode.Empty, operation.CostLiquid).Success,
            _ =>
                executionWorkshop.TryReshape(executionJar, operation.TargetShape!, executionScope,
                    ReshapeMode.Full).Success
        };

        Assert.Equal(allowed, result);
    }
}

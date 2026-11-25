using System.Collections.Generic;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class WorkshopOrderLiquidScopeTests
{
    private static JarFacts DefaultFacts(string shape, string? liquid, bool isMarked) =>
        new(shape, liquid, isMarked, JarPolarityResolver.Resolve(liquid, JarSystem.Core.Config.Vocabulary.Default));

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
    public void PourOutDiscardsRejectedOutputAndTransfersOnlyAcceptedStagingAmount()
    {
        var workshop = new Workshop("owned-output", new List<string> { "圆罐" },
            maxJars: 8, maxLiquids: 3, ownedInstanceId: "owned.workshop.output");
        var firstJar = new Jar("圆罐", "酒", id: 1001);
        var secondJar = new Jar("圆罐", "蜜", id: 1002);
        Assert.True(workshop.AddJar(firstJar));
        Assert.True(workshop.AddJar(secondJar));

        var staging = new WorkshopLiquidStaging(2);
        staging.Receive(202, "蜜", 2);
        var scope = new OrderLiquidScope(101, staging, "owned.workshop.output");

        var discarded = workshop.PourOutWithSettlement(firstJar, scope);
        Assert.True(discarded.Success, discarded.Reason);
        Assert.Null(firstJar.Liquid);
        Assert.Equal(2, staging.UsedCapacity);
        Assert.Equal(1, discarded.Receipt!.RequestedAmount);
        Assert.Equal(0, discarded.Receipt.AcceptedAmount);
        Assert.Equal(1, discarded.Receipt.DiscardedAmount);
        Assert.NotNull(discarded.OperationTransition);
        Assert.NotNull(discarded.StagingTransition);
        var outputEffect = Assert.Single(discarded.OperationTransition!.ResourceEffects);
        var discardEffect = Assert.Single(discarded.StagingTransition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Transfer, outputEffect.Kind);
        Assert.Equal("jar.internal-liquid", outputEffect.From!.AccountKind);
        Assert.Equal("workshop.order-output-boundary", outputEffect.To!.AccountKind);
        Assert.Equal(RuleResourceEffectKind.Discard, discardEffect.Kind);
        Assert.Equal(outputEffect.To, discardEffect.From);

        Assert.True(staging.TryConsume(202, "蜜", 1));
        var accepted = workshop.PourOutWithSettlement(secondJar, scope);
        Assert.True(accepted.Success, accepted.Reason);
        Assert.Null(secondJar.Liquid);
        Assert.Equal(1, accepted.Receipt!.AcceptedAmount);
        Assert.Equal(0, accepted.Receipt.DiscardedAmount);
        var acceptedEffect = Assert.Single(accepted.StagingTransition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Transfer, acceptedEffect.Kind);
        Assert.Equal(outputEffect.To, acceptedEffect.From);
        Assert.Equal("workshop.free-liquid-staging", acceptedEffect.To!.AccountKind);
        Assert.Equal(2, staging.UsedCapacity);
    }

    [Fact]
    public void PourOutConsumesJarLiquidAndDiscardsOutputRejectedByWorkshopBan()
    {
        var workshop = CreateWorkshop(ban: new[] { "酒" });
        var jar = new Jar("圆罐", "酒");
        Assert.True(workshop.AddJar(jar));
        var staging = new WorkshopLiquidStaging(4);
        var scope = new OrderLiquidScope(101, staging);

        var result = workshop.PourOutWithSettlement(jar, scope);

        Assert.True(result.Success, result.Reason);
        Assert.Null(jar.Liquid);
        Assert.Equal(0, staging.UsedCapacity);
        Assert.Equal(1, result.Receipt!.RequestedAmount);
        Assert.Equal(0, result.Receipt.AcceptedAmount);
        Assert.Equal(1, result.Receipt.DiscardedAmount);
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
    public void OrdinaryAndOrderScopesProduceIdenticalJarRuleTransitions()
    {
        AssertTransitionParity(
            DefaultFacts("圆罐", null, false),
            WorkshopOperation.PourIn("酒"),
            new Dictionary<string, int> { ["酒"] = 1 },
            staging => staging.Receive(101, "酒", 1));
        AssertTransitionParity(
            DefaultFacts("圆罐", "酒", false),
            WorkshopOperation.PourOut(),
            new Dictionary<string, int>(),
            _ => { });
        AssertTransitionParity(
            DefaultFacts("圆罐", null, false),
            WorkshopOperation.ReshapeEmpty("尖罐", "酒"),
            new Dictionary<string, int> { ["酒"] = 1 },
            staging => staging.Receive(101, "酒", 1));
        AssertTransitionParity(
            DefaultFacts("圆罐", "酒", false),
            WorkshopOperation.ReshapeFull("尖罐"),
            new Dictionary<string, int>(),
            _ => { });
    }

    private static void AssertTransitionParity(
        JarFacts facts,
        WorkshopOperation operation,
        Dictionary<string, int> ordinaryLiquids,
        System.Action<WorkshopLiquidStaging> seedOrderScope)
    {
        var workshop = CreateWorkshop();
        var ordinary = WorkshopRulebook.EvaluateTransition(
            workshop.Rules, workshop.MaxLiquids, facts, operation, ordinaryLiquids);

        var staging = new WorkshopLiquidStaging(8);
        seedOrderScope(staging);
        var orderScope = new OrderLiquidScope(101, staging);
        var order = WorkshopRulebook.EvaluateTransition(
            workshop.Rules, orderScope, facts, operation);

        Assert.Equal(ordinary.Decision, order.Decision);
        Assert.Equal(ordinary.Transition, order.Transition);
    }

    [Fact]
    public void OrderScopedRulebookSeparatesPourOutPolarityFromStagingAdmission()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", "酒");
        Assert.True(workshop.AddJar(jar));
        var staging = new WorkshopLiquidStaging(2);
        staging.Receive(202, "蜜", 2);
        var scope = new OrderLiquidScope(101, staging);

        Assert.True(WorkshopRulebook.IsAllowed(
            workshop.Rules, scope, JarFacts.Of(jar), WorkshopOperation.PourOut()),
            "Storage capacity is settled after production; it does not invalidate Jar chemistry.");

        var bannedWorkshop = CreateWorkshop(ban: new[] { "酒" });
        var bannedJar = new Jar("圆罐", "酒");
        Assert.True(bannedWorkshop.AddJar(bannedJar));
        Assert.True(WorkshopRulebook.IsAllowed(
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



    [Fact]
    public void LegacyWorkshopLiquidTransferRestoresBothInventoriesAfterFirstKindWasMoved()
    {
        var master = CreateWorkshop();
        var slave = CreateWorkshop();
        Assert.True(master.AddLiquid("酒", 3));
        Assert.True(master.AddLiquid("蜜", 2));
        Assert.True(slave.AddLiquid("蜜", 1));
        var masterBefore = new Dictionary<string, int>(master.LiquidStorage);
        var slaveBefore = new Dictionary<string, int>(slave.LiquidStorage);

        var error = Assert.Throws<InvalidOperationException>(() =>
            LegacyWorkshopLiquidTransfer.Execute(
                master, slave,
                new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 1 },
                new ThrowAfterFirstLegacySourceRemovalPort()));

        Assert.Equal("injected legacy source removal failure", error.Message);
        Assert.Equal(masterBefore, master.LiquidStorage);
        Assert.Equal(slaveBefore, slave.LiquidStorage);
    }

    [Fact]
    public void LegacyWorkshopLiquidTransferRestoresDestinationWhenAddMutatesThenThrows()
    {
        var master = CreateWorkshop();
        var slave = CreateWorkshop();
        Assert.True(master.AddLiquid("酒", 2));
        var masterBefore = new Dictionary<string, int>(master.LiquidStorage);
        var slaveBefore = new Dictionary<string, int>(slave.LiquidStorage);

        var error = Assert.Throws<InvalidOperationException>(() =>
            LegacyWorkshopLiquidTransfer.Execute(
                master, slave,
                new Dictionary<string, int> { ["酒"] = 1 },
                new ThrowAfterLegacyDestinationAddPort()));

        Assert.Equal("injected legacy destination add failure", error.Message);
        Assert.Equal(masterBefore, master.LiquidStorage);
        Assert.Equal(slaveBefore, slave.LiquidStorage);
    }

    private sealed class ThrowAfterFirstLegacySourceRemovalPort : ILegacyWorkshopLiquidTransferPort
    {
        private int _removals;

        public bool AddToDestination(Workshop destination, string liquid, int amount) =>
            destination.AddLiquid(liquid, amount);

        public bool RemoveFromSource(Workshop source, string liquid, int amount)
        {
            var removed = source.RemoveLiquid(liquid, amount);
            if (++_removals == 1)
                throw new InvalidOperationException("injected legacy source removal failure");
            return removed;
        }
    }

    private sealed class ThrowAfterLegacyDestinationAddPort : ILegacyWorkshopLiquidTransferPort
    {
        public bool AddToDestination(Workshop destination, string liquid, int amount)
        {
            destination.AddLiquid(liquid, amount);
            throw new InvalidOperationException("injected legacy destination add failure");
        }

        public bool RemoveFromSource(Workshop source, string liquid, int amount) =>
            source.RemoveLiquid(liquid, amount);
    }

    [Fact]
    public void ScopedLiquidTransferCommitsTheWholeBatchAndLeavesOtherOrdersUntouched()
    {
        var master = new WorkshopLiquidStaging(8);
        var slave = new WorkshopLiquidStaging(8);
        master.Receive(303, "酒", 3);
        master.Receive(303, "蜜", 2);
        master.Receive(999, "酒", 1);
        slave.Receive(303, "蜜", 1);
        slave.Receive(999, "蜜", 2);

        Assert.True(OrderLiquidTransfer.TryExecute(
            303, master, slave,
            new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 1 },
            out var reason));

        Assert.Null(reason);
        Assert.Equal(1, master.GetAmount(303, "酒"));
        Assert.Equal(1, master.GetAmount(303, "蜜"));
        Assert.Equal(2, slave.GetAmount(303, "酒"));
        Assert.Equal(2, slave.GetAmount(303, "蜜"));
        Assert.Equal(1, master.GetAmount(999, "酒"));
        Assert.Equal(2, slave.GetAmount(999, "蜜"));
    }

    [Fact]
    public void ScopedLiquidTransferPreflightRejectionDoesNotMutateEitherAccount()
    {
        var master = new WorkshopLiquidStaging(8);
        var slave = new WorkshopLiquidStaging(2, new[] { "蜜" });
        master.Receive(304, "酒", 3);
        master.Receive(304, "蜜", 1);
        slave.Receive(304, "酒", 1);
        var masterBefore = master.GetBalance(304);
        var slaveBefore = slave.GetBalance(304);

        Assert.False(OrderLiquidTransfer.TryExecute(
            304, master, slave,
            new Dictionary<string, int> { ["酒"] = 1, ["蜜"] = 1 },
            out var reason));

        Assert.NotNull(reason);
        Assert.Equal(masterBefore, master.GetBalance(304));
        Assert.Equal(slaveBefore, slave.GetBalance(304));
    }

    [Fact]
    public void ScopedLiquidTransferRestoresBothAccountsWhenDestinationMutationThrows()
    {
        var master = new WorkshopLiquidStaging(8);
        var slave = new WorkshopLiquidStaging(8);
        master.Receive(301, "酒", 3);
        master.Receive(301, "蜜", 2);
        slave.Receive(301, "蜜", 1);
        var masterBefore = master.GetBalance(301);
        var slaveBefore = slave.GetBalance(301);

        var error = Assert.Throws<InvalidOperationException>(() =>
            OrderLiquidTransfer.TryExecute(
                301, master, slave,
                new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 1 },
                out _, new ThrowAfterFirstDestinationMutationPort()));

        Assert.Equal("injected destination mutation failure", error.Message);
        Assert.Equal(masterBefore, master.GetBalance(301));
        Assert.Equal(slaveBefore, slave.GetBalance(301));
        Assert.Equal(5, master.UsedCapacity);
        Assert.Equal(1, slave.UsedCapacity);
    }

    [Fact]
    public void ScopedLiquidTransferRestoresBothAccountsWhenSourceMutationThrowsMidBatch()
    {
        var master = new WorkshopLiquidStaging(8);
        var slave = new WorkshopLiquidStaging(8);
        master.Receive(302, "酒", 3);
        master.Receive(302, "蜜", 2);
        slave.Receive(302, "蜜", 1);
        var masterBefore = master.GetBalance(302);
        var slaveBefore = slave.GetBalance(302);

        var error = Assert.Throws<InvalidOperationException>(() =>
            OrderLiquidTransfer.TryExecute(
                302, master, slave,
                new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 1 },
                out _, new ThrowAfterFirstSourceMutationPort()));

        Assert.Equal("injected source mutation failure", error.Message);
        Assert.Equal(masterBefore, master.GetBalance(302));
        Assert.Equal(slaveBefore, slave.GetBalance(302));
        Assert.Equal(5, master.UsedCapacity);
        Assert.Equal(1, slave.UsedCapacity);
    }

    private sealed class ThrowAfterFirstDestinationMutationPort : IOrderLiquidTransferMutationPort
    {
        private int _receives;

        public bool TryTransferOut(WorkshopLiquidStaging staging, int orderId, string liquid, int amount) =>
            staging.TryTransferOut(orderId, liquid, amount);

        public void Receive(WorkshopLiquidStaging staging, int orderId, string liquid, int amount)
        {
            staging.ReceiveTransferUnchecked(orderId, liquid, amount);
            if (++_receives == 1)
                throw new InvalidOperationException("injected destination mutation failure");
        }
    }

    private sealed class ThrowAfterFirstSourceMutationPort : IOrderLiquidTransferMutationPort
    {
        private int _withdrawals;

        public bool TryTransferOut(WorkshopLiquidStaging staging, int orderId, string liquid, int amount)
        {
            var changed = staging.TryTransferOut(orderId, liquid, amount);
            if (++_withdrawals == 1)
                throw new InvalidOperationException("injected source mutation failure");
            return changed;
        }

        public void Receive(WorkshopLiquidStaging staging, int orderId, string liquid, int amount) =>
            staging.ReceiveTransferUnchecked(orderId, liquid, amount);
    }
}

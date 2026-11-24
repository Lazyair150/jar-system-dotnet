using System.Collections.Generic;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class RuleTransitionContractTests
{
    [Fact]
    public void JarPolarityIsDerivedFromTheAuthoritativeLiquidIdentity()
    {
        Assert.Equal(JarPolarity.Neutral, JarPolarityResolver.Resolve(null));
        Assert.Equal(JarPolarity.Cathode, JarPolarityResolver.Resolve("酒"));
        Assert.Equal(JarPolarity.Anode, JarPolarityResolver.Resolve("蜜"));

        Assert.Equal(JarPolarity.Neutral, new Jar("圆罐").Polarity);
        Assert.Equal(JarPolarity.Cathode, new Jar("圆罐", "酒").Polarity);
        Assert.Equal(JarPolarity.Anode, new Jar("圆罐", "蜜").Polarity);
        Assert.Equal(JarPolarity.Neutral, new JarFacts("圆罐", null, false).Polarity);
        Assert.Equal(JarPolarity.Cathode, new JarFacts("圆罐", "酒", false).Polarity);
        Assert.Equal(JarPolarity.Anode, new JarFacts("圆罐", "蜜", false).Polarity);
        Assert.Throws<System.ArgumentException>(() => JarPolarityResolver.Resolve("未知液体"));
        Assert.Throws<System.ArgumentException>(() => new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("jarInstanceId", "jar.invalid"),
            new KeyValuePair<string, string>("liquid", "酒"),
            new KeyValuePair<string, string>("polarity", nameof(JarPolarity.Anode))
        }));
    }

    private static readonly RuleContext Rules = RuleContext.From(
        new WorkshopCapability(new[] { "圆罐", "方罐" }, new[] { "三角罐" }));

    [Fact]
    public void PolarityRuleContractAcceptsOnlyTheCanonicalOperationEdges()
    {
        JarPolarityRuleContract.Validate("jar.workshop.pour-in",
            new JarPolarityTransition(JarPolarity.Neutral, JarPolarity.Cathode));
        JarPolarityRuleContract.Validate("jar.workshop.pour-in",
            new JarPolarityTransition(JarPolarity.Neutral, JarPolarity.Anode));
        JarPolarityRuleContract.Validate("jar.workshop.pour-out",
            new JarPolarityTransition(JarPolarity.Anode, JarPolarity.Neutral));
        JarPolarityRuleContract.Validate("jar.workshop.reshape-empty",
            new JarPolarityTransition(JarPolarity.Neutral, JarPolarity.Neutral));
        JarPolarityRuleContract.Validate("jar.workshop.reshape-full",
            new JarPolarityTransition(JarPolarity.Cathode, JarPolarity.Neutral));
        JarPolarityRuleContract.Validate("jar.workshop.match",
            new JarPolarityTransition(JarPolarity.Anode, JarPolarity.Anode));

        Assert.Throws<System.ArgumentException>(() => JarPolarityRuleContract.Validate(
            "jar.workshop.pour-in",
            new JarPolarityTransition(JarPolarity.Cathode, JarPolarity.Anode)));
        Assert.Throws<System.ArgumentException>(() => JarPolarityRuleContract.Validate(
            "jar.workshop.reshape-full",
            new JarPolarityTransition(JarPolarity.Neutral, JarPolarity.Anode)));
        Assert.Throws<System.ArgumentException>(() => JarPolarityRuleContract.Validate(
            "jar.workshop.match",
            new JarPolarityTransition(JarPolarity.Cathode, JarPolarity.Neutral)));
    }

    [Fact]
    public void PourInConsumesWorkshopFreeLiquidAndDoesNotTouchJarLiquidAccount()
    {
        var before = new JarFacts("圆罐", null, false);
        var operation = WorkshopOperation.PourIn("酒");
        var evaluation = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int> { ["酒"] = 1 });

        var result = WorkshopRuleTransitionAdapter.Evaluate(
            evaluation, before, operation, "owned.workshop.master", "jar.17");

        Assert.True(result.IsAllowed);
        var effect = Assert.Single(result.Transition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Consume, effect.Kind);
        Assert.Equal("酒", effect.ResourceId);
        Assert.Equal("workshop.free-liquid-staging", effect.From!.AccountKind);
        Assert.Equal("owned.workshop.master", effect.From.OwnerId);
        Assert.Null(effect.To);
        Assert.Equal("圆罐", result.Transition.After.Values["shape"]);
        Assert.Equal("酒", result.Transition.After.Values["liquid"]);
        Assert.Equal(nameof(JarPolarity.Cathode), result.Transition.After.Values["polarity"]);
        Assert.Equal(new JarPolarityTransition(JarPolarity.Neutral, JarPolarity.Cathode),
            result.Transition.PolarityTransition);
        Assert.Equal("true", result.Transition.After.Values["marked"]);
    }

    [Fact]
    public void PourOutProducesIntoTheCurrentWorkshopStagingAccount()
    {
        var before = new JarFacts("圆罐", "酒", false);
        var operation = WorkshopOperation.PourOut();
        var evaluation = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int>());

        var result = WorkshopRuleTransitionAdapter.Evaluate(
            evaluation, before, operation, "owned.workshop.slave", "jar.18");

        var effect = Assert.Single(result.Transition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Produce, effect.Kind);
        Assert.Equal("workshop.free-liquid-staging", effect.To!.AccountKind);
        Assert.Equal("owned.workshop.slave", effect.To.OwnerId);
        Assert.Equal("酒", effect.ResourceId);
    }

    [Fact]
    public void FullReshapeConsumesJarInternalLiquidNotWorkshopFreeLiquid()
    {
        var before = new JarFacts("圆罐", "酒", false);
        var operation = WorkshopOperation.ReshapeFull("方罐");
        var evaluation = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int>());

        var result = WorkshopRuleTransitionAdapter.Evaluate(
            evaluation, before, operation, "owned.workshop.master", "jar.19");

        var effect = Assert.Single(result.Transition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Consume, effect.Kind);
        Assert.Equal("jar.internal-liquid", effect.From!.AccountKind);
        Assert.Equal("jar.19", effect.From.OwnerId);
        Assert.Equal("酒", effect.ResourceId);
        Assert.Equal(string.Empty, result.Transition.After.Values["liquid"]);
        Assert.Equal(nameof(JarPolarity.Neutral), result.Transition.After.Values["polarity"]);
        Assert.Equal(new JarPolarityTransition(JarPolarity.Cathode, JarPolarity.Neutral),
            result.Transition.PolarityTransition);
    }

    [Fact]
    public void RejectedRuleHasNoExecutableTransitionOrResourceEffects()
    {
        var before = new JarFacts("圆罐", "酒", true);
        var operation = WorkshopOperation.PourOut();
        var evaluation = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int>());

        var result = WorkshopRuleTransitionAdapter.Evaluate(
            evaluation, before, operation, "owned.workshop.master", "jar.20");

        Assert.True(result.IsRejected);
        Assert.Null(result.Transition);
        Assert.Equal("jar.already-operated", result.ReasonCode);
    }


    [Fact]
    public void StagingAdmissionTransitionAccountsForPartialCapacityAndDiscard()
    {
        var staging = new WorkshopLiquidStaging(3);
        Assert.Equal(2, staging.Receive(101, "蜜", 2).AcceptedAmount);
        var usedBefore = staging.UsedCapacity;
        var balanceBefore = staging.GetAmount(101, "酒");
        var receipt = staging.Receive(101, "酒", 3);

        var transition = StagingAdmissionTransitionAdapter.FromReceipt(
            "owned.workshop.master", 101, "酒", receipt,
            usedBefore, staging.UsedCapacity, balanceBefore, staging.GetAmount(101, "酒"));

        Assert.Equal("workshop.staging.admission", transition.RuleId);
        Assert.Equal("workshop.staging", transition.Scope);
        Assert.Equal("owned.workshop.master/order/101", transition.Target);
        Assert.Null(transition.PolarityTransition);
        Assert.Equal("1", transition.After.Values["acceptedAmount"]);
        Assert.Equal("2", transition.After.Values["discardedAmount"]);
        Assert.Equal(3, transition.ResourceEffects.Count);
        Assert.Equal(RuleResourceEffectKind.Produce, transition.ResourceEffects[0].Kind);
        Assert.Equal(3, transition.ResourceEffects[0].Amount);
        Assert.Equal(RuleResourceEffectKind.Transfer, transition.ResourceEffects[1].Kind);
        Assert.Equal(1, transition.ResourceEffects[1].Amount);
        Assert.Equal("workshop.free-liquid-staging", transition.ResourceEffects[1].To!.AccountKind);
        Assert.Equal("owned.workshop.master", transition.ResourceEffects[1].To.OwnerId);
        Assert.Equal(RuleResourceEffectKind.Discard, transition.ResourceEffects[2].Kind);
        Assert.Equal(2, transition.ResourceEffects[2].Amount);
        Assert.Equal("workshop.order-output-boundary", transition.ResourceEffects[2].From!.AccountKind);
    }

    [Fact]
    public void StagingAdmissionTransitionRepresentsBanAsFullDiscardWithoutStagingMutation()
    {
        var staging = new WorkshopLiquidStaging(4, new[] { "酒" });
        var receipt = staging.Receive(202, "酒", 2);
        var transition = StagingAdmissionTransitionAdapter.FromReceipt(
            "owned.workshop.slave", 202, "酒", receipt,
            0, staging.UsedCapacity, 0, staging.GetAmount(202, "酒"));

        Assert.Equal(0, receipt.AcceptedAmount);
        Assert.Equal(2, receipt.DiscardedAmount);
        Assert.Equal("staging.liquid-banned", transition.After.Values["reasonCode"]);
        Assert.Equal(2, transition.ResourceEffects.Count);
        Assert.Equal(RuleResourceEffectKind.Produce, transition.ResourceEffects[0].Kind);
        Assert.Equal(RuleResourceEffectKind.Discard, transition.ResourceEffects[1].Kind);
        Assert.Equal(0, staging.UsedCapacity);
    }

    [Fact]
    public void StagingAdmissionTransitionRejectsInconsistentReceiptOrCapacityDelta()
    {
        Assert.Throws<System.ArgumentException>(() =>
            StagingAdmissionTransitionAdapter.FromReceipt(
                "owned.workshop.master", 101, "酒",
                new LiquidInventoryReceipt(3, 1, 2),
                0, 0, 0, 1));
        Assert.Throws<System.ArgumentException>(() =>
            StagingAdmissionTransitionAdapter.FromReceipt(
                "owned.workshop.master", 101, "酒",
                new LiquidInventoryReceipt(3, 1, 1),
                0, 1, 0, 1));
    }

    [Fact]
    public void RuleTransitionSupportsNonJarResourceStateWithoutInventingPolarity()
    {
        var before = new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("usedCapacity", "0")
        });
        var after = new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("usedCapacity", "1")
        });
        var transition = new RuleTransition(
            "workshop.staging.admission", 1, "workshop.staging", "owned.workshop/order/1",
            before, after, System.Array.Empty<RuleResourceEffect>());

        transition.Validate();
        Assert.Null(transition.PolarityTransition);
        Assert.Throws<System.ArgumentException>(() => new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("polarity", nameof(JarPolarity.Neutral))
        }));
    }

    [Fact]
    public void ResourceEffectRejectsAmbiguousAccountEndpoints()
    {
        var invalid = new RuleResourceEffect(
            RuleResourceEffectKind.Produce,
            RuleResourceKind.Liquid,
            "酒",
            1,
            new RuleResourceAccount("workshop.free-liquid-staging", "owned.workshop"),
            new RuleResourceAccount("workshop.free-liquid-staging", "owned.workshop"));

        Assert.Throws<System.ArgumentException>(() => invalid.Validate());
    }


    [Fact]
    public void MatchLifecycleAdapterMarksExactJarWithoutInventingLiquidFlow()
    {
        var before = new JarFacts("圆罐", "酒", false);
        var proof = WorkshopRulebook.EvaluateMatchTransition(Rules, before);

        var result = MatchRuleTransitionAdapter.Evaluate(
            proof, before, "owned.workshop.master", "jar.21");

        Assert.True(result.IsAllowed);
        Assert.Equal("jar.workshop.match", result.Transition!.RuleId);
        Assert.Equal("jar.21", result.Transition.Target);
        Assert.Equal("workshop", result.Transition.Scope);
        Assert.Equal("owned.workshop.master", result.Transition.Before.Values["workshopInstanceId"]);
        Assert.Equal("jar.21", result.Transition.Before.Values["jarInstanceId"]);
        Assert.Equal("false", result.Transition.Before.Values["marked"]);
        Assert.Equal("true", result.Transition.After.Values["marked"]);
        Assert.Equal("圆罐", result.Transition.After.Values["shape"]);
        Assert.Equal("酒", result.Transition.After.Values["liquid"]);
        Assert.Equal(nameof(JarPolarity.Cathode), result.Transition.After.Values["polarity"]);
        Assert.Equal(new JarPolarityTransition(JarPolarity.Cathode, JarPolarity.Cathode),
            result.Transition.PolarityTransition);
        Assert.False(result.Transition.PolarityTransition!.Value.ChangesPolarity);
        Assert.Empty(result.Transition.ResourceEffects);
    }

    [Fact]
    public void MatchLifecycleAdapterPreservesAuthoritativeRejectionWithoutTransition()
    {
        var before = new JarFacts("圆罐", "酒", true);
        var proof = WorkshopRulebook.EvaluateMatchTransition(Rules, before);

        var result = MatchRuleTransitionAdapter.Evaluate(
            proof, before, "owned.workshop.master", "jar.22");

        Assert.True(result.IsRejected);
        Assert.Equal("jar.already-operated", result.ReasonCode);
        Assert.Null(result.Transition);
    }

    [Fact]
    public void MatchLifecycleAdapterRejectsNonMatchProof()
    {
        var before = new JarFacts("圆罐", null, false);
        var operation = WorkshopOperation.PourIn("酒");
        var proof = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int> { ["酒"] = 1 });

        Assert.Throws<System.ArgumentException>(() => MatchRuleTransitionAdapter.Evaluate(
            proof, before, "owned.workshop.master", "jar.23"));
    
}
}

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
        Assert.False(result.Transition.PolarityTransition.ChangesPolarity);
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

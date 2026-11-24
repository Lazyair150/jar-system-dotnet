using System.Collections.Generic;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class RuleTransitionContractTests
{
    private static readonly RuleContext Rules = RuleContext.From(
        new WorkshopCapability(new[] { "圆罐", "方罐" }, new[] { "三角罐" }));

    [Fact]
    public void PourInConsumesWorkshopFreeLiquidAndDoesNotTouchJarLiquidAccount()
    {
        var before = new JarFacts("圆罐", null, false);
        var operation = WorkshopOperation.PourIn("水");
        var evaluation = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int> { ["水"] = 1 });

        var result = WorkshopRuleTransitionAdapter.Evaluate(
            evaluation, before, operation, "owned.workshop.master", "jar.17");

        Assert.True(result.IsAllowed);
        var effect = Assert.Single(result.Transition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Consume, effect.Kind);
        Assert.Equal("水", effect.ResourceId);
        Assert.Equal("workshop.free-liquid-staging", effect.From!.AccountKind);
        Assert.Equal("owned.workshop.master", effect.From.OwnerId);
        Assert.Null(effect.To);
        Assert.Equal("圆罐", result.Transition.After.Values["shape"]);
        Assert.Equal("水", result.Transition.After.Values["liquid"]);
        Assert.Equal("true", result.Transition.After.Values["marked"]);
    }

    [Fact]
    public void PourOutProducesIntoTheCurrentWorkshopStagingAccount()
    {
        var before = new JarFacts("圆罐", "水", false);
        var operation = WorkshopOperation.PourOut();
        var evaluation = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int>());

        var result = WorkshopRuleTransitionAdapter.Evaluate(
            evaluation, before, operation, "owned.workshop.slave", "jar.18");

        var effect = Assert.Single(result.Transition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Produce, effect.Kind);
        Assert.Equal("workshop.free-liquid-staging", effect.To!.AccountKind);
        Assert.Equal("owned.workshop.slave", effect.To.OwnerId);
        Assert.Equal("水", effect.ResourceId);
    }

    [Fact]
    public void FullReshapeConsumesJarInternalLiquidNotWorkshopFreeLiquid()
    {
        var before = new JarFacts("圆罐", "水", false);
        var operation = WorkshopOperation.ReshapeFull("方罐");
        var evaluation = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int>());

        var result = WorkshopRuleTransitionAdapter.Evaluate(
            evaluation, before, operation, "owned.workshop.master", "jar.19");

        var effect = Assert.Single(result.Transition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Consume, effect.Kind);
        Assert.Equal("jar.internal-liquid", effect.From!.AccountKind);
        Assert.Equal("jar.19", effect.From.OwnerId);
        Assert.Equal("水", effect.ResourceId);
    }

    [Fact]
    public void RejectedRuleHasNoExecutableTransitionOrResourceEffects()
    {
        var before = new JarFacts("圆罐", "水", true);
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
            "水",
            1,
            new RuleResourceAccount("workshop.free-liquid-staging", "owned.workshop"),
            new RuleResourceAccount("workshop.free-liquid-staging", "owned.workshop"));

        Assert.Throws<System.ArgumentException>(() => invalid.Validate());
    }


    [Fact]
    public void MatchLifecycleAdapterMarksExactJarWithoutInventingLiquidFlow()
    {
        var before = new JarFacts("圆罐", "水", false);
        var proof = WorkshopRulebook.EvaluateMatchTransition(Rules, before);

        var result = MatchRuleTransitionAdapter.Evaluate(
            proof, before, "owned.workshop.master", "jar.21");

        Assert.True(result.IsAllowed);
        Assert.Equal("jar.workshop.match", result.Transition!.RuleId);
        Assert.Equal("jar.21", result.Transition.Target);
        Assert.Equal("workshop", result.Transition.Scope);
        Assert.Equal("false", result.Transition.Before.Values["marked"]);
        Assert.Equal("true", result.Transition.After.Values["marked"]);
        Assert.Equal("圆罐", result.Transition.After.Values["shape"]);
        Assert.Equal("水", result.Transition.After.Values["liquid"]);
        Assert.Empty(result.Transition.ResourceEffects);
    }

    [Fact]
    public void MatchLifecycleAdapterPreservesAuthoritativeRejectionWithoutTransition()
    {
        var before = new JarFacts("圆罐", "水", true);
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
        var operation = WorkshopOperation.PourIn("水");
        var proof = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int> { ["水"] = 1 });

        Assert.Throws<System.ArgumentException>(() => MatchRuleTransitionAdapter.Evaluate(
            proof, before, "owned.workshop.master", "jar.23"));
    
}

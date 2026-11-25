using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class RuleTransitionContractTests
{
    private static JarFacts DefaultFacts(string shape, string? liquid, bool isMarked) =>
        new(shape, liquid, isMarked, JarPolarityResolver.Resolve(liquid, JarSystem.Core.Config.Vocabulary.Default));

    [Fact]
    public void JarPolarityResolvesThroughWorldVocabularyAndSnapshotsEnforceThreeStates()
    {
        Assert.Equal(JarPolarity.Neutral, JarPolarityResolver.Resolve(null, Vocabulary.Default));
        Assert.Equal(JarPolarity.Cathode, JarPolarityResolver.Resolve("酒", Vocabulary.Default));
        Assert.Equal(JarPolarity.Anode, JarPolarityResolver.Resolve("蜜", Vocabulary.Default));

        Assert.Equal(JarPolarity.Neutral, new Jar("圆罐").Polarity);
        Assert.Equal(JarPolarity.Cathode, new Jar("圆罐", "酒").Polarity);
        Assert.Equal(JarPolarity.Anode, new Jar("圆罐", "蜜").Polarity);
        Assert.Equal(JarPolarity.Neutral, DefaultFacts("圆罐", null, false, JarPolarity.Neutral).Polarity);
        Assert.Equal(JarPolarity.Cathode, DefaultFacts("圆罐", "酒", false, JarPolarity.Cathode).Polarity);
        Assert.Equal(JarPolarity.Anode, DefaultFacts("圆罐", "蜜", false, JarPolarity.Anode).Polarity);
        Assert.Throws<System.ArgumentException>(() => JarPolarityResolver.Resolve("未知液体", Vocabulary.Default));
        Assert.Throws<System.ArgumentException>(() => new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("jarInstanceId", "jar.invalid-empty"),
            new KeyValuePair<string, string>("liquid", string.Empty),
            new KeyValuePair<string, string>("polarity", nameof(JarPolarity.Anode))
        }));
        Assert.Throws<System.ArgumentException>(() => new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("jarInstanceId", "jar.invalid-neutral"),
            new KeyValuePair<string, string>("liquid", "酒"),
            new KeyValuePair<string, string>("polarity", nameof(JarPolarity.Neutral))
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
        var before = DefaultFacts("圆罐", null, false);
        var operation = WorkshopOperation.PourIn("酒");
        var evaluation = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int> { ["酒"] = 1 });

        var result = WorkshopRuleTransitionAdapter.Evaluate(
            evaluation, before, operation, "owned.workshop.master", "jar.17");

        Assert.True(result.IsAllowed);
        var effect = Assert.Single(result.Transition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Transfer, effect.Kind);
        Assert.Equal("酒", effect.ResourceId);
        Assert.Equal("workshop.free-liquid-staging", effect.From!.AccountKind);
        Assert.Equal("owned.workshop.master", effect.From.OwnerId);
        Assert.Equal("jar.internal-liquid", effect.To!.AccountKind);
        Assert.Equal("jar.17", effect.To.OwnerId);
        Assert.Equal("圆罐", result.Transition.After.Values["shape"]);
        Assert.Equal("酒", result.Transition.After.Values["liquid"]);
        Assert.Equal(nameof(JarPolarity.Cathode), result.Transition.After.Values["polarity"]);
        Assert.Equal(nameof(WorkshopPolarityMode.Unrestricted), result.Transition.Before.Values["workshopPolarityMode"]);
        Assert.Equal(nameof(WorkshopPolarityMode.Unrestricted), result.Transition.After.Values["workshopPolarityMode"]);
        Assert.Equal(new JarPolarityTransition(JarPolarity.Neutral, JarPolarity.Cathode),
            result.Transition.PolarityTransition);
        Assert.Equal("true", result.Transition.After.Values["marked"]);
    }

    [Fact]
    public void PourOutTransfersJarLiquidToAnOrderOutputBoundaryBeforeAdmission()
    {
        var before = DefaultFacts("圆罐", "酒", false);
        var operation = WorkshopOperation.PourOut();
        var evaluation = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int>());

        var result = WorkshopRuleTransitionAdapter.Evaluate(
            evaluation, before, operation, "owned.workshop.slave", "jar.18",
            scope: "workshop", orderId: 303);

        var effect = Assert.Single(result.Transition!.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Transfer, effect.Kind);
        Assert.Equal("jar.internal-liquid", effect.From!.AccountKind);
        Assert.Equal("jar.18", effect.From.OwnerId);
        Assert.Equal("workshop.order-output-boundary", effect.To!.AccountKind);
        Assert.Equal("owned.workshop.slave/order/303", effect.To.OwnerId);
        Assert.Equal(nameof(WorkshopPolarityMode.Unrestricted), result.Transition.Before.Values["workshopPolarityMode"]);
        Assert.Equal("酒", effect.ResourceId);
    }

    [Fact]
    public void FullReshapeConsumesJarInternalLiquidNotWorkshopFreeLiquid()
    {
        var before = DefaultFacts("圆罐", "酒", false);
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
        var before = DefaultFacts("圆罐", "酒", true);
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
        var settlement = staging.ReceiveWithTransition("owned.workshop.master", 101, "酒", 3);
        var receipt = settlement.Receipt;
        var transition = settlement.Transition;

        Assert.Equal("workshop.staging.admission", transition.RuleId);
        Assert.Equal("workshop.staging", transition.Scope);
        Assert.Equal("owned.workshop.master/order/101", transition.Target);
        Assert.Null(transition.PolarityTransition);
        Assert.Equal("1", transition.After.Values["acceptedAmount"]);
        Assert.Equal("2", transition.After.Values["discardedAmount"]);
        Assert.Equal(2, transition.ResourceEffects.Count);
        Assert.Equal("3", transition.Before.Values["outputBoundaryAmount"]);
        Assert.Equal("0", transition.After.Values["outputBoundaryAmount"]);
        Assert.Equal(RuleResourceEffectKind.Transfer, transition.ResourceEffects[0].Kind);
        Assert.Equal(1, transition.ResourceEffects[0].Amount);
        Assert.Equal("workshop.order-output-boundary", transition.ResourceEffects[0].From!.AccountKind);
        Assert.Equal("workshop.free-liquid-staging", transition.ResourceEffects[0].To!.AccountKind);
        Assert.Equal("owned.workshop.master", transition.ResourceEffects[0].To!.OwnerId);
        Assert.Equal(RuleResourceEffectKind.Discard, transition.ResourceEffects[1].Kind);
        Assert.Equal(2, transition.ResourceEffects[1].Amount);
        Assert.Equal("workshop.order-output-boundary", transition.ResourceEffects[1].From!.AccountKind);
    }

    [Fact]
    public void StagingAdmissionTransitionRepresentsBanAsFullDiscardWithoutStagingMutation()
    {
        var staging = new WorkshopLiquidStaging(4, new[] { "酒" });
        var settlement = staging.ReceiveWithTransition("owned.workshop.slave", 202, "酒", 2);
        var receipt = settlement.Receipt;
        var transition = settlement.Transition;

        Assert.Equal(0, receipt.AcceptedAmount);
        Assert.Equal(2, receipt.DiscardedAmount);
        Assert.Equal("staging.liquid-banned", transition.After.Values["reasonCode"]);
        Assert.Single(transition.ResourceEffects);
        Assert.Equal(RuleResourceEffectKind.Discard, transition.ResourceEffects[0].Kind);
        Assert.Equal(2, transition.ResourceEffects[0].Amount);
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
    public void WorkshopRoleTransferTransitionPreservesOneWayExactOwnership()
    {
        var transition = WorkshopRoleTransferTransitionAdapter.Create(
            "owned.workshop.master", "owned.workshop.slave",
            new[] { 31, 32, 33 }, new[] { 31, 32 },
            new Dictionary<string, int> { ["酒"] = 3, ["蜜"] = 1 },
            new Dictionary<string, int> { ["酒"] = 2 },
            System.Array.Empty<string>(), System.Array.Empty<string>(),
            destinationJarCount: 0, destinationJarCapacity: 2,
            destinationStagingUsedCapacity: 1, destinationStagingCapacity: 3);

        Assert.Equal("workshop.role-transfer", transition.RuleId);
        Assert.Equal("owned.workshop.master->owned.workshop.slave", transition.Target);
        Assert.Null(transition.PolarityTransition);
        Assert.Equal(3, transition.ResourceEffects.Count);
        Assert.All(transition.ResourceEffects, effect =>
        {
            Assert.Equal(RuleResourceEffectKind.Transfer, effect.Kind);
            Assert.Equal("owned.workshop.master", effect.From!.OwnerId);
            Assert.Equal("owned.workshop.slave", effect.To!.OwnerId);
        });
        Assert.Equal(RuleResourceKind.Jar, transition.ResourceEffects[0].ResourceKind);
        Assert.Equal("31", transition.ResourceEffects[0].ResourceId);
        Assert.Equal(RuleResourceKind.Jar, transition.ResourceEffects[1].ResourceKind);
        Assert.Equal("32", transition.ResourceEffects[1].ResourceId);
        Assert.Equal(RuleResourceKind.Liquid, transition.ResourceEffects[2].ResourceKind);
        Assert.Equal("酒", transition.ResourceEffects[2].ResourceId);
        Assert.Equal(2, transition.ResourceEffects[2].Amount);
        Assert.Equal("1", transition.After.Values["sourceJarCount"]);
        Assert.Equal("2", transition.After.Values["destinationJarCount"]);
        Assert.Equal("3", transition.After.Values["destinationStagingUsedCapacity"]);
    }

    [Fact]
    public void WorkshopRoleTransferRejectsDestinationBanAndCapacityBeforeCreatingEffects()
    {
        var sourceJars = new[] { 41 };
        var sourceLiquids = new Dictionary<string, int> { ["蜜"] = 1, ["酒"] = 2 };
        Assert.Throws<System.ArgumentException>(() =>
            WorkshopRoleTransferTransitionAdapter.Create(
                "master", "slave", sourceJars, sourceJars,
                sourceLiquids, new Dictionary<string, int> { ["蜜"] = 1 },
                System.Array.Empty<string>(), new[] { "蜜" }, 0, 1, 0, 4));
        Assert.Throws<System.ArgumentException>(() =>
            WorkshopRoleTransferTransitionAdapter.Create(
                "master", "slave", sourceJars, sourceJars,
                sourceLiquids, new Dictionary<string, int> { ["酒"] = 2 },
                System.Array.Empty<string>(), System.Array.Empty<string>(), 0, 1, 3, 4));
        Assert.Throws<System.ArgumentException>(() =>
            WorkshopRoleTransferTransitionAdapter.Create(
                "master", "slave", sourceJars, sourceJars,
                sourceLiquids, new Dictionary<string, int> { ["酒"] = 3 },
                System.Array.Empty<string>(), System.Array.Empty<string>(), 0, 1, 0, 4));
    }

    [Fact]
    public void WorkshopRoleTransferRejectsSourceBannedLiquidButAllowsOppositePolarityJar()
    {
        var jars = new[] { 61 };
        Assert.Throws<System.ArgumentException>(() =>
            WorkshopRoleTransferTransitionAdapter.Create(
                "master", "slave", jars, jars,
                new Dictionary<string, int> { ["酒"] = 1 },
                new Dictionary<string, int> { ["酒"] = 1 },
                new[] { "酒" }, System.Array.Empty<string>(), 0, 1, 0, 2));

        var oppositePolarityTransfer = WorkshopRoleTransferTransitionAdapter.Create(
            "master", "slave", jars, jars,
            new Dictionary<string, int>(), new Dictionary<string, int>(),
            System.Array.Empty<string>(), new[] { "蜜" }, 0, 1, 0, 2);
        Assert.Contains(oppositePolarityTransfer.ResourceEffects, effect =>
            effect.ResourceKind == RuleResourceKind.Jar &&
            effect.Kind == RuleResourceEffectKind.Transfer &&
            effect.ResourceId == "61");
        Assert.DoesNotContain(oppositePolarityTransfer.Before.Values.Keys,
            key => key.Contains("Polarity", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WorkshopRoleTransferRejectsForeignOrDuplicateJarIdentityAndSameOwner()
    {
        Assert.Throws<System.ArgumentException>(() =>
            WorkshopRoleTransferTransitionAdapter.Create(
                "master", "slave", new[] { 51 }, new[] { 52 },
                new Dictionary<string, int>(), new Dictionary<string, int>(),
                System.Array.Empty<string>(), System.Array.Empty<string>(), 0, 1, 0, 1));
        Assert.Throws<System.ArgumentException>(() =>
            WorkshopRoleTransferTransitionAdapter.Create(
                "master", "slave", new[] { 51 }, new[] { 51, 51 },
                new Dictionary<string, int>(), new Dictionary<string, int>(),
                System.Array.Empty<string>(), System.Array.Empty<string>(), 0, 2, 0, 1));
        Assert.Throws<System.ArgumentException>(() =>
            WorkshopRoleTransferTransitionAdapter.Create(
                "master", "master", new[] { 51 }, new[] { 51 },
                new Dictionary<string, int>(), new Dictionary<string, int>(),
                System.Array.Empty<string>(), System.Array.Empty<string>(), 0, 1, 0, 1));
    }

    [Fact]
    public void JarHandoffIsIndependentOfEverySourceAndDestinationPolarityBanCombination()
    {
        var banProfiles = new[]
        {
            System.Array.Empty<string>(),
            new[] { "蜜" },
            new[] { "酒" },
            new[] { "酒", "蜜" }
        };

        foreach (var sourceBan in banProfiles)
        foreach (var destinationBan in banProfiles)
        {
            var transition = WorkshopRoleTransferTransitionAdapter.Create(
                "source-instance", "destination-instance",
                new[] { 101 }, new[] { 101 },
                new Dictionary<string, int>(), new Dictionary<string, int>(),
                sourceBan, destinationBan,
                destinationJarCount: 0, destinationJarCapacity: 1,
                destinationStagingUsedCapacity: 0, destinationStagingCapacity: 0);

            var jarEffect = Assert.Single(transition.ResourceEffects);
            Assert.Equal(RuleResourceEffectKind.Transfer, jarEffect.Kind);
            Assert.Equal(RuleResourceKind.Jar, jarEffect.ResourceKind);
            Assert.Equal("101", jarEffect.ResourceId);
            Assert.Equal("source-instance", jarEffect.From!.OwnerId);
            Assert.Equal("destination-instance", jarEffect.To!.OwnerId);
            Assert.Null(transition.PolarityTransition);
        }
    }

    [Fact]
    public void WorkshopRoleTransferRejectsDuplicateIdentityInSourceInventorySnapshot()
    {
        Assert.Throws<System.ArgumentException>(() =>
            WorkshopRoleTransferTransitionAdapter.Create(
                "master", "slave", new[] { 51, 51 }, new[] { 51 },
                new Dictionary<string, int>(), new Dictionary<string, int>(),
                System.Array.Empty<string>(), System.Array.Empty<string>(),
                0, 1, 0, 1));
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
        var before = DefaultFacts("圆罐", "酒", false);
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
        var before = DefaultFacts("圆罐", "酒", true);
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
        var before = DefaultFacts("圆罐", null, false);
        var operation = WorkshopOperation.PourIn("酒");
        var proof = WorkshopRulebook.EvaluateTransition(
            Rules, 5, before, operation, new Dictionary<string, int> { ["酒"] = 1 });

        Assert.Throws<System.ArgumentException>(() => MatchRuleTransitionAdapter.Evaluate(
            proof, before, "owned.workshop.master", "jar.23"));
    
}
}

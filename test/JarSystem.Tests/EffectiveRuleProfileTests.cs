using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class EffectiveRuleProfileTests
{
    [Fact]
    public void ContributionOrderDoesNotChangeCanonicalFingerprint()
    {
        var first = Contribution("rule.alpha", "instance.a", "slot.a", 10);
        var second = Contribution("rule.beta", "instance.b", "slot.b", 20);

        var forward = EffectiveRuleProfile.Create("board.demo", 1, 7, [first, second]);
        var reverse = EffectiveRuleProfile.Create("board.demo", 1, 7, [second, first]);

        Assert.Equal(forward.Fingerprint, reverse.Fingerprint);
        Assert.Equal(new[] { "slot.a", "slot.b" }, forward.Contributions.Select(x => x.Source.SlotId));
    }

    [Fact]
    public void SameDefinitionDifferentOwnedInstancesRemainIndependentContributions()
    {
        var first = Contribution("rule.grant-mark", "owned.copy.one", "slot.one", 1);
        var second = Contribution("rule.grant-mark", "owned.copy.two", "slot.two", 1);

        var profile = EffectiveRuleProfile.Create("board.demo", 1, 1, [first, second]);

        Assert.Equal(2, profile.Contributions.Count);
        Assert.NotEqual(profile.Contributions[0].Source.SourceInstanceId, profile.Contributions[1].Source.SourceInstanceId);
    }

    [Fact]
    public void RuleContextOnlyProjectsContributionsFromItsRequestedScope()
    {
        var workshop = Contribution("rule.workshop-mark", "instance.workshop", "slot.workshop", 1);
        var ship = workshop with
        {
            RuleId = "rule.ship-ban",
            Effect = RuleContributionEffect.BanLiquid,
            Target = "酒",
            Scope = "ship",
            Source = workshop.Source with { SourceInstanceId = "instance.ship", SlotId = "ship.core" }
        };
        var profile = EffectiveRuleProfile.Create("board.demo", 1, 1, [workshop, ship]);

        var context = RuleContext.From(profile, "workshop");

        Assert.True(context.IsAdditionalMarked("circle"));
        Assert.False(context.IsBanned("酒"));
        Assert.Equal("workshop", context.RuleScope);
    }

    [Fact]
    public void RuleContextProjectsOnlyTheRequestedBoardSlotAndCanSeedRuntimeModifiers()
    {
        var masterMark = Contribution("rule.master-mark", "owned.master", "slot.master", 1);
        var slaveBan = Contribution("rule.slave-ban", "owned.slave", "slot.slave", 1) with
        {
            Effect = RuleContributionEffect.BanLiquid,
            Target = "酒"
        };
        var profile = EffectiveRuleProfile.Create("board.demo", 1, 1, [masterMark, slaveBan]);

        var master = RuleContext.From(profile, "workshop", "slot.master", mutableStack: true);
        var slave = RuleContext.From(profile, "workshop", "slot.slave", mutableStack: true);

        Assert.True(master.IsAdditionalMarked("circle"));
        Assert.False(master.IsBanned("酒"));
        Assert.False(slave.IsAdditionalMarked("circle"));
        Assert.True(slave.IsBanned("酒"));

        using (master.Stack.Push(new SuppressModifier
        {
            Name = "runtime-suppress",
            Operation = "pour",
            Reason = "暂时禁止"
        }))
        {
            Assert.Equal("暂时禁止", master.SuppressionReason("pour", null, null));
        }
        Assert.Null(master.SuppressionReason("pour", null, null));
    }

    [Fact]
    public void CapturedRuntimeModifiersExtendRatherThanReplaceInstalledBoardProvenance()
    {
        var installed = Contribution("rule.installed-mark", "owned.workshop", "slot.master", 1);
        var profile = EffectiveRuleProfile.Create(
            "board.demo", 1, 1, [installed],
            new string('a', 64), new string('b', 64));
        var context = RuleContext.From(profile, "workshop", "slot.master", mutableStack: true);

        using (context.Stack.Push(new SuppressModifier
        {
            Name = "temporary-ban-pour",
            Source = "runtime-event",
            Operation = "pour",
            Reason = "event restriction"
        }))
        {
            var captured = context.CaptureEffectiveProfile()!;
            Assert.Equal(profile.AssignmentFingerprint, captured.AssignmentFingerprint);
            Assert.Equal(profile.SourceProfileFingerprint, captured.SourceProfileFingerprint);
            Assert.Contains(captured.Contributions, x =>
                x.RuleId == "rule.installed-mark" &&
                x.Source.SourceInstanceId == "owned.workshop" &&
                x.Source.SlotId == "slot.master");
            Assert.Contains(captured.Contributions, x =>
                x.Effect == RuleContributionEffect.SuppressOperation &&
                x.Source.SourceKind == "temporary-modifier");
        }
    }

    [Fact]
    public void DuplicateContributionIdentityIsRejected()
    {
        var contribution = Contribution("rule.alpha", "instance.a", "slot.a", 1);

        Assert.Throws<ArgumentException>(() =>
            EffectiveRuleProfile.Create("board.demo", 1, 1, [contribution, contribution]));
    }

    [Fact]
    public void SourceRevisionParticipatesInFingerprint()
    {
        var first = Contribution("rule.alpha", "instance.a", "slot.a", 1);
        var progressed = first with
        {
            Source = first.Source with { SourceRevision = 2 },
            Parameters = [new RuleContributionParameter("strength", "2")]
        };

        var before = EffectiveRuleProfile.Create("board.demo", 1, 1, [first]);
        var after = EffectiveRuleProfile.Create("board.demo", 1, 2, [progressed]);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    private static RuleContribution Contribution(string ruleId, string instanceId, string slotId, int priority) =>
        new(
            ruleId,
            1,
            RuleContributionEffect.GrantAdditionalMark,
            "circle",
            "workshop",
            priority,
            new RuleContributionSource("installed-component", "component.workshop", 1, instanceId, 1, slotId),
            [new RuleContributionParameter("shape", "circle")]);
}

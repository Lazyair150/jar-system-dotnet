using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class WorkshopRuleContributionAdapterTests
{
    [Fact]
    public void CapabilityContributionsRoundTripThroughEffectiveProfile()
    {
        var capability = new WorkshopCapability(
            baseMarks: ["base"],
            additionalMarks: ["extra"],
            ban: ["禁酒"]);
        var source = Source();
        var profile = WorkshopRuleContributionAdapter.Build("board.demo", 1, 1, source, capability, ModifierStack.Empty);

        var context = RuleContext.From(profile);

        Assert.True(context.IsBaseMarked("base"));
        Assert.True(context.IsAdditionalMarked("extra"));
        Assert.True(context.IsBanned("酒"));
        Assert.Equal(profile.Fingerprint, context.EffectiveProfile?.Fingerprint);
    }

    [Fact]
    public void TemporaryGrantContributionRetainsModifierSemantics()
    {
        var stack = new ModifierStack();
        using var scope = stack.Push(new GrantModifier
        {
            Name = "installed-temporary-grant",
            Source = "component.skill",
            Shape = "temporary-shape",
            Window = (0, 5)
        });
        var profile = WorkshopRuleContributionAdapter.Build(
            "board.demo", 1, 1, Source(), new WorkshopCapability(baseMarks: ["base"]), stack);

        var context = RuleContext.From(profile);

        Assert.True(context.IsAdditionalMarked("temporary-shape"));
    }

    [Fact]
    public void OpaqueRequireContributionRemainsUndetermined()
    {
        var stack = new ModifierStack();
        using var scope = stack.Push(new RequireModifier
        {
            Name = "opaque-require",
            Source = "installed-component",
            Text = "external condition",
            Violation = null
        });
        var profile = WorkshopRuleContributionAdapter.Build(
            "board.demo", 1, 1, Source(), new WorkshopCapability(baseMarks: ["base"]), stack);

        var context = RuleContext.From(profile);

        Assert.True(context.HasUnresolvableRequirements());
    }

    private static RuleContributionSource Source() =>
        new("installed-component", "workshop.alpha", 1, "owned.workshop.alpha", 3, "workshop.master");
}

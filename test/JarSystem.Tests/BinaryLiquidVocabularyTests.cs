using System;
using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class BinaryLiquidVocabularyTests
{
    private static Vocabulary CreateSpaceVocabulary() => new(
        new[] { "圆罐", "方罐" },
        new[] { "燃料", "冷却液" },
        new Dictionary<string, LiquidPolarity>(StringComparer.Ordinal)
        {
            ["燃料"] = LiquidPolarity.Anode,
            ["冷却液"] = LiquidPolarity.Cathode
        });

    [Fact]
    public void CustomLiquidNamesResolveOnlyThroughTheWorldMapping()
    {
        var vocabulary = CreateSpaceVocabulary();
        var fuel = new Jar("圆罐", "燃料", vocabulary: vocabulary);
        var coolant = new Jar("方罐", "冷却液", vocabulary: vocabulary);

        Assert.Equal(JarPolarity.Anode, fuel.Polarity);
        Assert.Equal(JarPolarity.Cathode, coolant.Polarity);
        Assert.Equal(JarPolarity.Neutral, new Jar("圆罐", vocabulary: vocabulary).Polarity);
    }

    [Fact]
    public void MappingIsIndependentOfLiquidListOrderAndNames()
    {
        var vocabulary = new Vocabulary(
            new[] { "圆罐" },
            new[] { "冷却液", "燃料" },
            new Dictionary<string, LiquidPolarity>(StringComparer.Ordinal)
            {
                ["燃料"] = LiquidPolarity.Anode,
                ["冷却液"] = LiquidPolarity.Cathode
            });

        Assert.Equal(JarPolarity.Anode, new Jar("圆罐", "燃料", vocabulary: vocabulary).Polarity);
        Assert.Equal(JarPolarity.Cathode, new Jar("圆罐", "冷却液", vocabulary: vocabulary).Polarity);
    }

    [Fact]
    public void NonDefaultWorldCannotInferPolarityFromNamesOrOrder()
    {
        Assert.Throws<ArgumentException>(() =>
            new Vocabulary(new[] { "圆罐" }, new[] { "燃料", "冷却液" }));

        Assert.Throws<ArgumentException>(() =>
            new Vocabulary(
                new[] { "圆罐" },
                new[] { "燃料", "冷却液" },
                new Dictionary<string, LiquidPolarity>
                {
                    ["燃料"] = LiquidPolarity.Anode
                }));

        Assert.Throws<ArgumentException>(() =>
            new Vocabulary(
                new[] { "圆罐" },
                new[] { "燃料", "冷却液" },
                new Dictionary<string, LiquidPolarity>
                {
                    ["燃料"] = LiquidPolarity.Anode,
                    ["冷却液"] = LiquidPolarity.Anode
                }));
    }

    [Fact]
    public void RenamingLiquidIdentitiesPreservesWorldPolarityStructure()
    {
        var original = CreateSpaceVocabulary();
        var renamed = new Vocabulary(
            new[] { "圆罐", "方罐" },
            new[] { "fuel", "coolant" },
            new Dictionary<string, LiquidPolarity>(StringComparer.Ordinal)
            {
                ["fuel"] = LiquidPolarity.Anode,
                ["coolant"] = LiquidPolarity.Cathode
            });

        Assert.Equal(
            new Jar("圆罐", "燃料", vocabulary: original).Polarity,
            new Jar("圆罐", "fuel", vocabulary: renamed).Polarity);
        Assert.Equal(
            new Jar("方罐", "冷却液", vocabulary: original).Polarity,
            new Jar("方罐", "coolant", vocabulary: renamed).Polarity);
        Assert.Equal(
            original.ResolveLiquidPolarity("燃料"),
            renamed.ResolveLiquidPolarity("fuel"));
        Assert.Equal(
            original.ResolveLiquidPolarity("冷却液"),
            renamed.ResolveLiquidPolarity("coolant"));
    }

    [Fact]
    public void SwappingExplicitMappingSwapsPolarityWithoutRenamingLiquids()
    {
        var swapped = new Vocabulary(
            new[] { "圆罐" },
            new[] { "燃料", "冷却液" },
            new Dictionary<string, LiquidPolarity>(StringComparer.Ordinal)
            {
                ["燃料"] = LiquidPolarity.Cathode,
                ["冷却液"] = LiquidPolarity.Anode
            });

        Assert.Equal(JarPolarity.Cathode, new Jar("圆罐", "燃料", vocabulary: swapped).Polarity);
        Assert.Equal(JarPolarity.Anode, new Jar("圆罐", "冷却液", vocabulary: swapped).Polarity);
    }

    [Fact]
    public void StagingBanDoesNotBecomeJarPolarityRestriction()
    {
        var vocabulary = CreateSpaceVocabulary();
        var capability = new WorkshopCapability(
            ban: new[] { "燃料" },
            vocabulary: vocabulary);

        Assert.True(capability.IsBanned("燃料"));
        Assert.Equal(WorkshopPolarityMode.Unrestricted, capability.PolarityMode);
        Assert.True(capability.CanOperateJar(JarPolarity.Anode));
        Assert.True(capability.CanOperateJar(JarPolarity.Cathode));
        Assert.True(capability.CanOperateJar(JarPolarity.Neutral));
    }


    [Fact]
    public void CustomLiquidBanRejectsOnlyStagingAdmissionNotJarPourOut()
    {
        var vocabulary = CreateSpaceVocabulary();
        var workshop = new Workshop(
            "custom-world-workshop",
            new List<string> { "圆罐" },
            ban: new List<string> { "燃料" },
            maxJars: 4,
            maxLiquids: 4,
            vocabulary: vocabulary);
        var jar = new Jar("圆罐", "燃料", vocabulary: vocabulary);
        Assert.True(workshop.AddJar(jar));

        var staging = new WorkshopLiquidStaging(4, new[] { "燃料" });
        var scope = new OrderLiquidScope(1, staging);
        var result = workshop.PourOutWithSettlement(jar, scope);

        Assert.True(result.Success, result.Reason);
        Assert.Null(jar.Liquid);
        Assert.Equal(0, result.Receipt!.AcceptedAmount);
        Assert.Equal(1, result.Receipt.DiscardedAmount);
        Assert.Equal(0, staging.UsedCapacity);
    }

    [Fact]
    public void WorldOptionsSerializesAndValidatesExplicitPolarityMapping()
    {
        var options = new WorldOptions
        {
            Shapes = new List<string> { "圆罐" },
            Liquids = new List<string> { "燃料", "冷却液" },
            LiquidPolarities = new Dictionary<string, LiquidPolarity>(StringComparer.Ordinal)
            {
                ["燃料"] = LiquidPolarity.Anode,
                ["冷却液"] = LiquidPolarity.Cathode
            }
        };

        var restored = WorldOptionsJson.FromJson(WorldOptionsJson.ToJson(options));
        Assert.Equal(LiquidPolarity.Anode, restored.LiquidPolarities["燃料"]);
        Assert.Equal(LiquidPolarity.Cathode, restored.LiquidPolarities["冷却液"]);
        Assert.Equal(JarPolarity.Anode, new Jar("圆罐", "燃料", vocabulary: restored.BuildVocabulary()).Polarity);
    }

    [Fact]
    public void DefaultLegacyVocabularyRetainsExplicitCompatibilityMapping()
    {
        Assert.Equal(JarPolarity.Cathode, new Jar("圆罐", "酒").Polarity);
        Assert.Equal(JarPolarity.Anode, new Jar("圆罐", "蜜").Polarity);
    }
}

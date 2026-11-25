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

        Assert.Empty(options.Validate().Errors);
        var restored = WorldOptionsJson.FromJson(WorldOptionsJson.ToJson(options));
        Assert.Empty(restored.Validate().Errors);
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

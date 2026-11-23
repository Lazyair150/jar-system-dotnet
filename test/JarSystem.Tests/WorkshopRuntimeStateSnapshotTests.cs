using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class WorkshopRuntimeStateSnapshotTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    [Fact]
    public void RestoreRebuildsJarIdentityStateAndLegacyLiquidBalances()
    {
        var workshop = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 5, vocabulary: Words, liquidStagingCapacity: 7);
        var jar = new Jar("圆罐", "酒", vocabulary: Words);
        Assert.True(workshop.AddJar(jar));
        Assert.True(workshop.MarkJar(jar).Success);
        workshop.AddLiquid("酒", 2);

        var snapshot = workshop.CaptureRuntimeState();
        workshop.ClearMark(jar);
        workshop.RemoveJar(jar);
        Assert.True(workshop.AddJar(new Jar("方罐", vocabulary: Words)));
        workshop.AddLiquid("酒", 1);

        workshop.RestoreRuntimeState(snapshot);

        var restoredJar = Assert.Single(workshop.Jars);
        Assert.Equal(jar.Id, restoredJar.Id);
        Assert.Equal("圆罐", restoredJar.Shape);
        Assert.Equal("酒", restoredJar.Liquid);
        Assert.True(restoredJar.IsMarked);
        Assert.Equal(2, workshop.LiquidStorage["酒"]);
        Assert.Equal(7, workshop.LiquidStagingCapacity);
    }

    [Fact]
    public void RestoreRejectsDifferentWorkshopConfigurationBeforeChangingInventory()
    {
        var source = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 5, vocabulary: Words);
        source.AddJar(new Jar("圆罐", vocabulary: Words));
        var snapshot = source.CaptureRuntimeState();

        var target = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 3, maxLiquids: 5, vocabulary: Words);
        var existing = new Jar("圆罐", vocabulary: Words);
        target.AddJar(existing);

        Assert.Throws<System.InvalidOperationException>(() => target.RestoreRuntimeState(snapshot));
        Assert.Same(existing, Assert.Single(target.Jars));
    }

    [Fact]
    public void RestoreRejectsBannedFreeLiquidWithoutPartialMutation()
    {
        var workshop = new Workshop("禁酒主台", new List<string> { "圆罐" },
            ban: new List<string> { "酒" }, maxJars: 4, maxLiquids: 5, vocabulary: Words);
        var existing = new Jar("圆罐", vocabulary: Words);
        workshop.AddJar(existing);
        var invalidLiquids = new Dictionary<string, int>(workshop.LiquidStorage)
        {
            ["酒"] = 1
        };
        var invalid = new WorkshopRuntimeStateSnapshot(
            "禁酒主台", 4, 5, workshop.LiquidStagingCapacity,
            workshop.BaseMarks, workshop.AdditionalMarks, workshop.Ban,
            new[] { existing }, invalidLiquids);

        Assert.Throws<DomainValidationException>(() => workshop.RestoreRuntimeState(invalid));
        Assert.Same(existing, Assert.Single(workshop.Jars));
        Assert.Equal(0, workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void RestoreRejectsDuplicateJarIdentityWithoutPartialMutation()
    {
        var workshop = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 5, vocabulary: Words);
        var existing = new Jar("圆罐", vocabulary: Words);
        workshop.AddJar(existing);
        var duplicate = new Jar("圆罐", id: existing.Id, vocabulary: Words);
        var invalid = new WorkshopRuntimeStateSnapshot(
            "主台", 4, 5, workshop.LiquidStagingCapacity,
            workshop.BaseMarks, workshop.AdditionalMarks, workshop.Ban,
            new[] { existing, duplicate }, workshop.LiquidStorage);

        Assert.Throws<DomainValidationException>(() => workshop.RestoreRuntimeState(invalid));
        Assert.Same(existing, Assert.Single(workshop.Jars));
    }
}

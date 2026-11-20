using System;
using System.Collections.Generic;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldV2HostServiceTests
{
    private static WorldV2Options ValidOptions(string masterName = "主台") => new()
    {
        Shapes = new List<string> { "圆罐", "方罐" },
        Liquids = new List<string> { "酒", "蜜" },
        Producers = new List<WorldV2ProducerOptions>
        {
            new()
            {
                Name = "陶工",
                Rates = new Dictionary<string, double> { ["圆罐"] = 1.0 }
            }
        },
        LiquidProductionRates = new Dictionary<string, double>
        {
            ["酒"] = 0.5,
            ["蜜"] = 0
        },
        InitialLiquidStock = new Dictionary<string, int> { ["酒"] = 3 },
        JarFlowRatePerLogicalTime = 1,
        LiquidFlowRatePerLogicalTime = 1,
        Workshops = new List<WorldV2WorkshopOptions>
        {
            new()
            {
                Name = masterName,
                BaseMarks = new List<string> { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10
            }
        },
        MasterWorkshopName = masterName,
        ExecutionLogicalTimePerUnit = 1000,
        PassiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot,
        ActiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot
    };

    [Fact]
    public void ReplaceCreatesAndPublishesInitialWorld()
    {
        using var host = new WorldV2HostService();

        Assert.False(host.HasWorld);
        Assert.Throws<InvalidOperationException>(() => _ = host.Current);

        var created = host.Replace(ValidOptions());

        Assert.True(host.HasWorld);
        Assert.Same(created, host.Current);
        Assert.False(created.IsDisposed);
    }

    [Fact]
    public void SuccessfulReplacePublishesNewWorldAndDisposesPreviousWorld()
    {
        using var host = new WorldV2HostService();
        var first = host.Replace(ValidOptions("第一主台"));

        var second = host.Replace(ValidOptions("第二主台"));

        Assert.NotSame(first, second);
        Assert.Same(second, host.Current);
        Assert.Equal("第二主台", host.Current.Roster.Master!.Name);
        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    [Fact]
    public void FailedReplaceKeepsPreviousWorldAliveAndCurrent()
    {
        using var host = new WorldV2HostService();
        var previous = host.Replace(ValidOptions());

        var invalid = ValidOptions();
        invalid.MasterWorkshopName = "不存在";

        Assert.Throws<ArgumentException>(() => host.Replace(invalid));

        Assert.Same(previous, host.Current);
        Assert.False(previous.IsDisposed);
    }

    [Fact]
    public void HostsAreIndependentAndDisposeIsIdempotent()
    {
        var firstHost = new WorldV2HostService();
        var secondHost = new WorldV2HostService();
        var firstWorld = firstHost.Replace(ValidOptions());
        var secondWorld = secondHost.Replace(ValidOptions());

        firstHost.Dispose();
        firstHost.Dispose();

        Assert.True(firstWorld.IsDisposed);
        Assert.False(secondWorld.IsDisposed);
        Assert.Same(secondWorld, secondHost.Current);
        Assert.False(firstHost.HasWorld);
        Assert.Throws<ObjectDisposedException>(() => firstHost.Replace(ValidOptions()));
        Assert.Throws<ObjectDisposedException>(() => _ = firstHost.Current);

        secondHost.Dispose();
        Assert.True(secondWorld.IsDisposed);
    }
}

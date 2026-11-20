using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using JarSystem.WorldV2Bridge;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldV2BridgeSessionTests
{
    private static WorldV2Options Options() => new()
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
            ["酒"] = 0,
            ["蜜"] = 0
        },
        InitialLiquidStock = new Dictionary<string, int> { ["酒"] = 1 },
        JarFlowRatePerLogicalTime = 1,
        LiquidFlowRatePerLogicalTime = 1,
        Workshops = new List<WorldV2WorkshopOptions>
        {
            new()
            {
                Name = "主台",
                BaseMarks = new List<string> { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10
            },
            new()
            {
                Name = "从台",
                BaseMarks = new List<string> { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10
            }
        },
        MasterWorkshopName = "主台",
        SlaveWorkshopName = "从台",
        ExecutionLogicalTimePerUnit = 1_000,
        PassiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot,
        ActiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot
    };

    [Fact]
    public void SessionOwnsHostAndRunsWorldV2CommandSnapshotChain()
    {
        using var session = new WorldV2BridgeSession();

        Assert.False(session.HasWorld);

        session.CreateOrReplaceWorld(Options());
        Assert.True(session.HasWorld);

        var advance = session.AdvanceWorld(1_000);
        Assert.Equal(1_000, advance.LogicalTimeDeltaMilli);
        Assert.Equal(1, advance.ProducedJarCount);
        Assert.Equal(1, advance.FlowedJarCount);
        Assert.False(advance.RuntimeFaulted);

        var demandId = session.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A",
            DemandAreaKind.Active,
            0));

        var snapshot = session.GetWorldSnapshot();
        Assert.Equal(1_000, snapshot.WorldTimeMilli);
        Assert.Equal(1_000, snapshot.LogicalTimeMilli);
        Assert.Single(snapshot.Demands);
        Assert.Equal(demandId, snapshot.Demands[0].Id);
        Assert.Equal("主台", snapshot.Master!.Name);

        session.PauseWorld();
        var pausedAdvance = session.AdvanceWorld(1_000);
        Assert.Equal(0, pausedAdvance.LogicalTimeDeltaMilli);
        Assert.True(session.GetWorldSnapshot().IsPaused);

        session.ResumeWorld();
        session.SetWorldSpeedRatio(2.0);
        var fastAdvance = session.AdvanceWorld(1_000);
        Assert.Equal(2_000, fastAdvance.LogicalTimeDeltaMilli);
        Assert.Equal(3_000, session.GetWorldSnapshot().LogicalTimeMilli);
    }

    [Fact]
    public void SessionDisposeIsIdempotentAndRejectsFurtherCalls()
    {
        var session = new WorldV2BridgeSession();

        session.Dispose();
        session.Dispose();

        Assert.Throws<System.ObjectDisposedException>(() => _ = session.HasWorld);
        Assert.Throws<System.ObjectDisposedException>(() => session.GetWorldSnapshot());
    }
}

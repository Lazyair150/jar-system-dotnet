using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldV2ApplicationServiceTests
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
        LiquidProductionRates = new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 },
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
            }
        },
        MasterWorkshopName = "主台",
        ExecutionLogicalTimePerUnit = 1_000,
        PassiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot,
        ActiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot
    };

    [Fact]
    public void ApplicationServiceRunsActiveDemandEndToEndWithoutExposingContext()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);

        app.CreateOrReplaceWorld(Options());
        var advance = app.AdvanceWorld(1_000);

        Assert.Equal(1_000, advance.LogicalTimeDeltaMilli);
        Assert.Equal(1, advance.ProducedJarCount);
        Assert.Equal(1, advance.FlowedJarCount);
        Assert.Equal(1, advance.FlowedLiquids["酒"]);
        Assert.False(advance.RuntimeFaulted);

        var requirements = new List<WorldV2RequirementInput>
        {
            new("圆罐", "酒")
        };
        var demandId = app.AddJarDemand(new WorldV2AddJarDemandRequest(
            requirements, "A", DemandAreaKind.Active, 0));
        requirements.Clear();

        var beforeStart = app.GetWorldSnapshot();
        var demand = Assert.Single(beforeStart.Demands);
        Assert.Equal(demandId, demand.Id);
        Assert.Equal(DemandAreaKind.Active, demand.Area);
        Assert.Single(demand.Requirements);
        Assert.Equal("圆罐", demand.Requirements[0].Shape);
        Assert.Equal("酒", demand.Requirements[0].Liquid);

        var start = app.StartActiveDemand(demandId);
        Assert.True(start.Success);
        Assert.Null(start.Reason);

        var executing = app.GetWorldSnapshot();
        Assert.Single(executing.ActiveSessions);
        Assert.Equal(demandId, executing.ActiveSessions[0].DemandId);
        Assert.Equal(1, executing.ReservationCount);

        app.AdvanceWorld(1_000);

        var completed = app.GetWorldSnapshot();
        Assert.Empty(completed.ActiveSessions);
        Assert.Empty(completed.Demands);
        Assert.Equal(0, completed.ReservationCount);
        Assert.Equal(2_000, completed.LogicalTimeMilli);
        Assert.False(completed.RuntimeFaulted);
    }

    [Fact]
    public void SnapshotIsDetachedFromLaterWorldChanges()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        app.AdvanceWorld(1_000);
        var first = app.GetWorldSnapshot();
        var firstJarCount = first.Master!.Jars.Count;

        app.AdvanceWorld(1_000);
        var second = app.GetWorldSnapshot();

        Assert.Equal(1, firstJarCount);
        Assert.Equal(1, first.Master.Jars.Count);
        Assert.Equal(2, second.Master!.Jars.Count);
    }

    [Fact]
    public void MoveDemandChangesOnlyItsAreaAndMissingDemandReturnsFalse()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        var id = app.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A", DemandAreaKind.Passive, 0));

        Assert.True(app.MoveDemand(id, DemandAreaKind.Active));
        Assert.False(app.MoveDemand(id + 999, DemandAreaKind.Active));

        var demand = Assert.Single(app.GetWorldSnapshot().Demands);
        Assert.Equal(DemandAreaKind.Active, demand.Area);
    }
}

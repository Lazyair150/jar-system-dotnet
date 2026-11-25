using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
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
        Assert.Throws<System.ObjectDisposedException>(() => session.ImportProducedJars(
            "disposed-parser-batch",
            new Dictionary<string, int> { ["圆罐"] = 1 }));
    }

    [Fact]
    public void FailedWorldReplacementPreservesThePreviouslyRunningWorld()
    {
        using var session = new WorldV2BridgeSession();
        session.CreateOrReplaceWorld(Options());
        session.AdvanceWorld(1_000);

        var demandId = session.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A",
            DemandAreaKind.Active,
            0));

        var invalidOptions = Options();
        invalidOptions.Workshops.Add(new WorldV2WorkshopOptions
        {
            Name = "主台",
            BaseMarks = new List<string> { "圆罐" }
        });

        Assert.Throws<System.ArgumentException>(
            () => session.CreateOrReplaceWorld(invalidOptions));

        Assert.True(session.HasWorld);
        var snapshot = session.GetWorldSnapshot();
        Assert.Equal(1_000, snapshot.WorldTimeMilli);
        Assert.Equal(1_000, snapshot.LogicalTimeMilli);
        Assert.Equal("主台", snapshot.Master!.Name);
        Assert.Equal(demandId, Assert.Single(snapshot.Demands).Id);
    }

    [Fact]
    public void SuccessfulWorldReplacementPublishesFreshWorldWithoutOldState()
    {
        using var session = new WorldV2BridgeSession();
        session.CreateOrReplaceWorld(Options());
        session.AdvanceWorld(1_000);
        session.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A",
            DemandAreaKind.Active,
            0));

        var replacementOptions = Options();
        replacementOptions.MasterWorkshopName = "从台";
        replacementOptions.SlaveWorkshopName = "主台";

        session.CreateOrReplaceWorld(replacementOptions);

        var snapshot = session.GetWorldSnapshot();
        Assert.Equal(0, snapshot.WorldTimeMilli);
        Assert.Equal(0, snapshot.LogicalTimeMilli);
        Assert.Equal("从台", snapshot.Master!.Name);
        Assert.Equal("主台", snapshot.Slave!.Name);
        Assert.Empty(snapshot.Demands);
        Assert.Empty(snapshot.ActiveSessions);
        Assert.Equal(0, snapshot.ReservationCount);
        Assert.False(snapshot.RuntimeFaulted);
    }


    [Fact]
    public void GodotBridgeRestoresVersionedJsonByAtomicHostReplacement()
    {
        using var session = new WorldV2BridgeSession();
        var options = Options();
        session.CreateOrReplaceWorld(options);
        var originalWorldId = session.GetWorldInstanceId();
        session.AdvanceWorld(1_000);

        var json = session.CaptureExecutionCheckpointJson();
        Assert.Contains("\"schemaVersion\":" + WorldV2CheckpointJsonCodec.CurrentSchemaVersion, json);

        session.AdvanceWorld(2_000);
        Assert.Equal(3_000, session.GetWorldSnapshot().WorldTimeMilli);

        session.RestoreExecutionCheckpointJson(session.CaptureWorldOptions(), json);
        var restored = session.GetWorldSnapshot();
        Assert.Equal(1_000, restored.WorldTimeMilli);
        Assert.Equal(1_000, restored.LogicalTimeMilli);
        Assert.Equal("主台", restored.Master!.Name);
        Assert.Equal("从台", restored.Slave!.Name);
        Assert.Equal(originalWorldId, session.GetWorldInstanceId());
    }

    [Fact]
    public void GodotBridgeRestoresExecutionCheckpointAfterWorldRecreation()
    {
        using var session = new WorldV2BridgeSession();
        session.CreateOrReplaceWorld(Options());
        session.AdvanceWorld(1_000);

        var demandId = session.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A", DemandAreaKind.Active, 0));
        Assert.True(session.StartActiveDemand(demandId).Success);

        var checkpoint = session.CaptureExecutionCheckpoint();
        var originalWorldId = session.GetWorldInstanceId();
        session.CreateOrReplaceWorld(Options());
        Assert.NotEqual(originalWorldId, session.GetWorldInstanceId());
        session.RestoreExecutionCheckpoint(checkpoint);
        Assert.Equal(originalWorldId, session.GetWorldInstanceId());

        var restored = session.GetWorldSnapshot();
        Assert.Single(restored.ActiveSessions);
        Assert.Equal(demandId, restored.ActiveSessions[0].DemandId);
        Assert.Equal(1_000, restored.WorldTimeMilli);

        session.AdvanceWorld(1_000);
        Assert.Empty(session.GetWorldSnapshot().ActiveSessions);
        Assert.Empty(session.GetWorldSnapshot().Demands);
    }
    [Fact]
    public void GodotBridgeExposesPendingOrderCycleEventsAndAcknowledgment()
    {
        using var session = new WorldV2BridgeSession();
        session.CreateOrReplaceWorld(Options());
        session.AdvanceWorld(1_000);

        var demandId = session.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A", DemandAreaKind.Active, 0, "ship.order.buff-guard"));
        Assert.True(session.StartActiveDemand(demandId).Success);
        session.AdvanceWorld(1_000);

        var first = Assert.Single(session.GetPendingOrderCycleEvents());
        var second = Assert.Single(session.GetPendingOrderCycleEvents());
        Assert.Equal(first.EventId, second.EventId);
        Assert.Equal("ship.order.buff-guard", first.OrderDefinitionKey);
        Assert.True(session.AcknowledgeOrderCycleEvent(first.EventId));
        Assert.False(session.AcknowledgeOrderCycleEvent(first.EventId));
        Assert.Empty(session.GetPendingOrderCycleEvents());
    }



    [Fact]
    public void ProducerRateUpdatesAreAtomicAndPreserveProgressAcrossCheckpointRestore()
    {
        using var session = new WorldV2BridgeSession();
        var options = Options();
        session.CreateOrReplaceWorld(options);

        // 0.5 logical time at rate 1.0 leaves half a Jar in the production accumulator.
        var partial = session.AdvanceWorld(500);
        Assert.Equal(0, partial.ProducedJarCount);

        var checkpointJson = session.CaptureExecutionCheckpointJson();
        var nextRates = new Dictionary<string, double> { ["圆罐"] = 2.0 };
        session.UpdateProducerRates("陶工", nextRates);

        // The previous half-progress is retained: another quarter time at rate 2.0 completes one Jar.
        var completed = session.AdvanceWorld(250);
        Assert.Equal(1, completed.ProducedJarCount);
        Assert.Equal(0, completed.FlowedJarCount);

        // Restore returns both the original rate and its fractional production state.
        session.RestoreExecutionCheckpointJson(options, checkpointJson);
        session.UpdateProducerRates("陶工", nextRates);
        var restored = session.AdvanceWorld(250);
        Assert.Equal(1, restored.ProducedJarCount);
        Assert.Equal(0, restored.FlowedJarCount);
    }

    [Fact]
    public void InvalidProducerRateBatchDoesNotPartiallyChangeConfiguredRates()
    {
        using var session = new WorldV2BridgeSession();
        session.CreateOrReplaceWorld(Options());

        Assert.Throws<DomainValidationException>(() =>
            session.UpdateProducerRates("陶工", new Dictionary<string, double>
            {
                ["圆罐"] = 0.0,
                ["方罐"] = 1.0
            }));

        var advance = session.AdvanceWorld(1_000);
        Assert.Equal(1, advance.ProducedJarCount);
        Assert.Equal(1, advance.FlowedJarCount);
    }

}

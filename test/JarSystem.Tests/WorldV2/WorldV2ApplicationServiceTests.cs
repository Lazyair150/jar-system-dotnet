using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
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
            },
            new()
            {
                Name = "从台",
                BaseMarks = new List<string> { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10
            },
            new()
            {
                Name = "备用台",
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
    public void PausedWorldDoesNotRunAutomaticPassiveScheduling()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());
        app.AdvanceWorld(1_000);

        var demandId = app.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A", DemandAreaKind.Passive, 0));

        app.PauseWorld();
        var pausedAdvance = app.AdvanceWorld(1_000);
        var paused = app.GetWorldSnapshot();

        Assert.Equal(0, pausedAdvance.LogicalTimeDeltaMilli);
        Assert.Empty(pausedAdvance.StartedDemandIds);
        Assert.Empty(paused.ActiveSessions);
        Assert.Single(paused.Demands);
        Assert.Equal(demandId, paused.Demands[0].Id);

        app.ResumeWorld();
        var resumedAdvance = app.AdvanceWorld(0);

        Assert.Contains(demandId, resumedAdvance.StartedDemandIds);
        Assert.Single(app.GetWorldSnapshot().ActiveSessions);
    }

    [Fact]
    public void ApplicationServiceControlsPauseAndWorldSpeedThroughWorldClock()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        app.PauseWorld();
        var pausedAdvance = app.AdvanceWorld(1_000);
        var paused = app.GetWorldSnapshot();

        Assert.Equal(0, pausedAdvance.LogicalTimeDeltaMilli);
        Assert.True(paused.IsPaused);
        Assert.Equal(0, paused.WorldTimeMilli);
        Assert.Equal(0, paused.LogicalTimeMilli);

        app.ResumeWorld();
        app.SetWorldSpeedRatio(2.0);
        var fastAdvance = app.AdvanceWorld(1_000);
        var fast = app.GetWorldSnapshot();

        Assert.Equal(2_000, fastAdvance.LogicalTimeDeltaMilli);
        Assert.False(fast.IsPaused);
        Assert.Equal(1_000, fast.WorldTimeMilli);
        Assert.Equal(2_000, fast.LogicalTimeMilli);
        Assert.Equal(2_000, fast.SpeedRatioMilli);
    }

    [Fact]
    public void AddJarDemandCopiesCallerRequirementsBeforeEnteringHostOperation()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        var mutableRequirements = new List<WorldV2RequirementInput>
        {
            new("圆罐", "酒")
        };
        var request = new WorldV2AddJarDemandRequest(
            mutableRequirements, "A", DemandAreaKind.Active, 0);

        var id = app.AddJarDemand(request);
        mutableRequirements[0] = new WorldV2RequirementInput("方罐", "蜜");
        mutableRequirements.Clear();

        var demand = Assert.Single(app.GetWorldSnapshot().Demands);
        Assert.Equal(id, demand.Id);
        Assert.Equal("圆罐", Assert.Single(demand.Requirements).Shape);
        Assert.Equal("酒", demand.Requirements[0].Liquid);
    }

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
        Assert.Empty(advance.FlowedLiquids);
        Assert.Equal(1, app.GetWorldSnapshot().ShipLiquidInventory!.Balances["酒"]);
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

        var checkpoint = app.CaptureExecutionResourceCheckpoint();
        Assert.Equal(0, checkpoint.ShipLiquidInventory.UsedCapacity);
        Assert.Equal(1, checkpoint.Admission.MasterStaging!.Accounts[demandId]["酒"]);
        Assert.Single(checkpoint.Admission.ActiveOrders);
        Assert.Single(checkpoint.Reservations.Reservations);
        Assert.Single(checkpoint.Sessions);
        Assert.Equal(1_000, checkpoint.Clock.WorldTimeMilli);

        var funding = Assert.Single(app.GetResourceLedger().Events,
            item => item.Cause == "flow" && item.Name == "酒");
        Assert.Equal("自机液库", funding.From);
        Assert.Equal("主台", funding.To);
        Assert.Equal(1, funding.Amount);

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
        Assert.Equal(1, checkpoint.Admission.MasterStaging!.Accounts[demandId]["酒"]);
        Assert.Single(checkpoint.Sessions);
        Assert.False(completed.RuntimeFaulted);
    }

    [Fact]
    public void LiquidProductionReceiptsAndLedgerAccountForAcceptanceOverflowAndCrossLiquidCompetition()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        var options = Options();
        options.ShipLiquidCapacity = 2;
        options.LiquidProductionRates = new Dictionary<string, double>
        {
            ["酒"] = 2,
            ["蜜"] = 1
        };
        app.CreateOrReplaceWorld(options);

        var first = app.AdvanceWorld(1_000);
        Assert.Empty(first.FlowedLiquids);
        Assert.Collection(first.LiquidProductionReceipts,
            honey =>
            {
                Assert.Equal("蜜", honey.Liquid);
                Assert.Equal(1, honey.ProducedAmount);
                Assert.Equal(1, honey.AcceptedAmount);
                Assert.Equal(0, honey.DiscardedAmount);
            },
            alcohol =>
            {
                Assert.Equal("酒", alcohol.Liquid);
                Assert.Equal(2, alcohol.ProducedAmount);
                Assert.Equal(0, alcohol.AcceptedAmount);
                Assert.Equal(2, alcohol.DiscardedAmount);
            });

        var snapshot = app.GetWorldSnapshot();
        Assert.Equal(2, snapshot.ShipLiquidInventory!.UsedCapacity);
        Assert.Equal(1, snapshot.ShipLiquidInventory.Balances["酒"]);
        Assert.Equal(1, snapshot.ShipLiquidInventory.Balances["蜜"]);
        Assert.All(snapshot.Master!.Liquids.Values, amount => Assert.Equal(0, amount));
        Assert.All(snapshot.Slave!.Liquids.Values, amount => Assert.Equal(0, amount));

        var ledgerBeforeSecondTick = app.GetResourceLedger();
        var second = app.AdvanceWorld(1_000);
        Assert.Equal(3, second.LiquidProductionReceipts.Sum(item => item.ProducedAmount));
        Assert.Equal(0, second.LiquidProductionReceipts.Sum(item => item.AcceptedAmount));
        Assert.Equal(3, second.LiquidProductionReceipts.Sum(item => item.DiscardedAmount));

        var ledger = app.GetResourceLedger();
        Assert.Equal(7, ledger.Events.Sum(item => item.Amount));
        Assert.Equal(1, ledger.Events.Count(item => item.Cause == "stock"));
        Assert.Equal(1, ledger.Events.Count(item => item.Cause == "production"));
        Assert.Equal(3, ledger.Events.Count(item => item.Cause == "discard"));
        Assert.Equal(ledger.LastSeq, ledger.Events.Last().Seq);

        var incremental = app.GetResourceLedger(ledgerBeforeSecondTick.LastSeq);
        Assert.Equal(2, incremental.Events.Count);
        Assert.All(incremental.Events, item => Assert.Equal("discard", item.Cause));
        Assert.Equal(ledger.LastSeq, incremental.LastSeq);
    }

    [Fact]
    public void ApplicationServiceReplacementInterruptsActiveDemandAndCommitsMasterRole()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        app.AdvanceWorld(1_000);
        var demandId = app.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A", DemandAreaKind.Active, 5_000));

        Assert.True(app.StartActiveDemand(demandId).Success);
        var beforeReplacement = app.GetWorldSnapshot();
        Assert.Single(beforeReplacement.ActiveSessions);
        Assert.Equal(1, beforeReplacement.ReservationCount);
        var spareBeforeReplacement = Assert.Single(beforeReplacement.SpareWorkshops);
        Assert.Equal("备用台", spareBeforeReplacement.Name);
        Assert.False(spareBeforeReplacement.HasResources);

        var begin = app.BeginWorkshopReplacement(
            WorkshopRole.Master, "备用台", phaseDurationLogicalMilli: 1_000);

        Assert.True(begin.Success);
        Assert.NotNull(begin.Replacement);
        Assert.False(begin.Replacement!.IsFinished);
        Assert.Equal("主台", begin.Replacement.OldWorkshopName);
        Assert.Equal("备用台", begin.Replacement.TargetWorkshopName);
        Assert.Equal(1_000, begin.Replacement.PhaseDurationLogicalMilli);
        Assert.Equal(0, begin.Replacement.PhaseElapsedLogicalMilli);
        Assert.True(begin.Replacement.InterruptionCommitted);

        var interrupted = app.GetWorldSnapshot();
        Assert.Empty(interrupted.ActiveSessions);
        Assert.Equal(0, interrupted.ReservationCount);
        var interruptedDemand = Assert.Single(interrupted.Demands);
        Assert.True(interruptedDemand.IsCoolingDown);

        app.AdvanceWorld(2_000);

        var completed = app.GetWorldSnapshot();
        Assert.Equal("备用台", completed.Master!.Name);
        var displacedMaster = Assert.Single(completed.SpareWorkshops);
        Assert.Equal("主台", displacedMaster.Name);
        Assert.False(displacedMaster.HasResources);
        Assert.NotNull(completed.Replacement);
        Assert.True(completed.Replacement!.IsFinished);
        Assert.True(completed.Replacement.Succeeded);
        Assert.False(completed.Replacement.InterruptionOutcomeUncertain);
        Assert.Empty(completed.ActiveSessions);
        Assert.Equal(0, completed.ReservationCount);
        Assert.True(Assert.Single(completed.Demands).IsCoolingDown);
        Assert.False(completed.RuntimeFaulted);
    }

    [Fact]
    public void ApplicationServiceCanCancelMasterReplacementAndRestoreMigrationSnapshot()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        app.AdvanceWorld(1_000);
        var before = app.GetWorldSnapshot();
        Assert.Single(before.Master!.Jars);
        Assert.Equal(1, before.ShipLiquidInventory!.Balances["酒"]);
        Assert.Equal(0, before.Master.Liquids["酒"]);

        var demandId = app.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A", DemandAreaKind.Active, 5_000));
        Assert.True(app.StartActiveDemand(demandId).Success);

        var begin = app.BeginWorkshopReplacement(
            WorkshopRole.Master, "备用台", phaseDurationLogicalMilli: 1_000);
        Assert.True(begin.Success);
        host.ExecuteExclusive(context =>
        {
            var target = context.Roster.Find("备用台")!;
            Assert.Equal(1, context.Admission.GetPrefillBalances(target).GetValueOrDefault("酒"));
            Assert.Equal(0, context.ShipLiquidInventory!.GetAmount("酒"));
        });

        // 替换开始时真实执行会话已被中断，订单进入冷却且预留被释放。
        var interrupted = app.GetWorldSnapshot();
        Assert.Empty(interrupted.ActiveSessions);
        Assert.Equal(0, interrupted.ReservationCount);
        Assert.True(Assert.Single(interrupted.Demands).IsCoolingDown);

        // 第一个逻辑阶段完成重组，但尚未完成转移和岗位提交。
        app.AdvanceWorld(1_000);
        var reshaped = app.GetWorldSnapshot();
        Assert.Equal("主台", reshaped.Master!.Name);
        Assert.Null(Assert.Single(reshaped.Master.Jars).Liquid);
        Assert.Equal(0, reshaped.Master.Liquids["酒"]);
        Assert.Equal(0, reshaped.ShipLiquidInventory!.Balances.GetValueOrDefault("酒"));
        Assert.False(reshaped.Replacement!.IsFinished);
        Assert.Equal(1, reshaped.Replacement.CompletedPhases);

        var cancel = app.CancelWorkshopReplacement("用户取消主台替换");

        Assert.True(cancel.Success);
        Assert.NotNull(cancel.Replacement);
        Assert.True(cancel.Replacement!.IsFinished);
        Assert.False(cancel.Replacement.Succeeded);
        Assert.True(cancel.Replacement.InterruptionCommitted);
        Assert.Equal("用户取消主台替换", cancel.Replacement.FailureReason);

        var after = app.GetWorldSnapshot();
        host.ExecuteExclusive(context =>
        {
            var target = context.Roster.Find("备用台")!;
            Assert.Empty(context.Admission.GetPrefillBalances(target));
        });
        Assert.Equal("主台", after.Master!.Name);
        Assert.Single(after.Master.Jars);
        Assert.Null(after.Master.Jars[0].Liquid);
        Assert.Equal(1, after.ShipLiquidInventory!.Balances["酒"]);
        Assert.Equal(0, after.Master.Liquids["酒"]);
        var returnedEvents = app.GetResourceLedger().Events
            .Where(item => item.Cause == "return" && item.Kind == "liquid" && item.Name == "酒")
            .ToArray();
        Assert.Contains(returnedEvents, item =>
            item.From == "订单暂存" && item.To == "自机液库");
        Assert.Contains(returnedEvents, item =>
            item.From == "主台预置" && item.To == "自机液库" &&
            item.Reason == "主工作台切换回滚");
        Assert.False(after.RuntimeFaulted);
        Assert.Empty(after.ActiveSessions);
        Assert.Equal(0, after.ReservationCount);
        var retainedDemand = Assert.Single(after.Demands);
        Assert.Equal(demandId, retainedDemand.Id);
        Assert.True(retainedDemand.IsCoolingDown);

        // 已提交的订单中断属于不可逆副作用，取消迁徙不会把它撤销。
        var secondCancel = app.CancelWorkshopReplacement();
        Assert.False(secondCancel.Success);
        Assert.Contains("已经结束", secondCancel.Error);
    }

    [Fact]
    public void ApplicationServiceCanReplaceSlaveWorkshopWithoutMovingResources()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        var begin = app.BeginWorkshopReplacement(
            WorkshopRole.Slave, "备用台", phaseDurationLogicalMilli: 1_000);

        Assert.True(begin.Success);
        Assert.NotNull(begin.Replacement);
        Assert.Equal(WorkshopRole.Slave, begin.Replacement!.Role);

        app.AdvanceWorld(1_000);

        var completed = app.GetWorldSnapshot();
        Assert.Equal("主台", completed.Master!.Name);
        Assert.Equal("备用台", completed.Slave!.Name);
        var displacedSlave = Assert.Single(completed.SpareWorkshops);
        Assert.Equal("从台", displacedSlave.Name);
        Assert.False(displacedSlave.HasResources);
        Assert.True(completed.Replacement!.IsFinished);
        Assert.True(completed.Replacement.Succeeded);
        Assert.False(completed.RuntimeFaulted);
    }

    [Fact]
    public void ApplicationServiceRejectsUnknownWorkshopReplacementTarget()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        var result = app.BeginWorkshopReplacement(
            WorkshopRole.Master, "不存在的工作台", phaseDurationLogicalMilli: 1_000);

        Assert.False(result.Success);
        Assert.Contains("找不到目标工作台", result.Error);
        Assert.Null(result.Replacement);
        Assert.Equal("主台", app.GetWorldSnapshot().Master!.Name);
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
        Assert.Single(first.Master.Jars);
        Assert.Collection(second.Master!.Jars, _ => { }, _ => { });
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

    [Fact]
    public void ApplicationServiceRestoresCheckpointAfterReplacingTheEntireWorldContext()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());
        app.AdvanceWorld(1_000);

        var demandId = app.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A", DemandAreaKind.Active, 0));
        Assert.True(app.StartActiveDemand(demandId).Success);

        var checkpoint = app.CaptureExecutionResourceCheckpoint();
        app.CreateOrReplaceWorld(Options());
        Assert.Empty(app.GetWorldSnapshot().ActiveSessions);

        app.RestoreExecutionResourceCheckpoint(checkpoint);

        var restored = app.GetWorldSnapshot();
        Assert.Single(restored.ActiveSessions);
        Assert.Equal(demandId, restored.ActiveSessions[0].DemandId);
        Assert.Single(restored.Demands);
        Assert.Equal(demandId, restored.Demands[0].Id);
        Assert.Equal(1_000, restored.WorldTimeMilli);

        var finished = app.AdvanceWorld(1_000);
        Assert.False(finished.RuntimeFaulted);
        Assert.Empty(app.GetWorldSnapshot().ActiveSessions);
        Assert.Empty(app.GetWorldSnapshot().Demands);
    }
    [Fact]
    public void SuccessfulOrderCycleOutboxSurvivesJsonRestoreAndAcknowledgesIdempotently()
    {
        using var firstHost = new WorldV2HostService();
        var firstApp = new WorldV2ApplicationService(firstHost);
        firstApp.CreateOrReplaceWorld(Options());
        firstApp.AdvanceWorld(1_000);

        var demandId = firstApp.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            "A", DemandAreaKind.Active, 0, "ship.order.buff-overdrive"));
        Assert.True(firstApp.StartActiveDemand(demandId).Success);
        firstApp.AdvanceWorld(1_000);

        var firstRead = Assert.Single(firstApp.GetPendingOrderCycleEvents());
        var repeatedRead = Assert.Single(firstApp.GetPendingOrderCycleEvents());
        Assert.Equal(firstRead, repeatedRead);
        Assert.Equal("ship.order.buff-overdrive", firstRead.OrderDefinitionKey);
        Assert.Equal(demandId, firstRead.DemandId);
        Assert.Equal(1, firstRead.CycleSequence);

        var json = WorldV2CheckpointJsonCodec.EncodeExecutionResourceCheckpoint(
            firstApp.CaptureExecutionResourceCheckpoint());

        using var restoredHost = new WorldV2HostService();
        var restoredApp = new WorldV2ApplicationService(restoredHost);
        restoredApp.CreateOrReplaceWorld(Options());
        restoredApp.RestoreExecutionResourceCheckpointJson(Options(), json);

        var restoredEvent = Assert.Single(restoredApp.GetPendingOrderCycleEvents());
        Assert.Equal(firstRead.EventId, restoredEvent.EventId);
        Assert.Equal(firstRead.WorldInstanceId, restoredEvent.WorldInstanceId);
        Assert.True(restoredApp.AcknowledgeOrderCycleEvent(restoredEvent.EventId));
        Assert.False(restoredApp.AcknowledgeOrderCycleEvent(restoredEvent.EventId));
        Assert.Empty(restoredApp.GetPendingOrderCycleEvents());
    }



    [Fact]
    public void ExternalProducedJarImportIsIdempotentAndRejectsOperationIdPayloadReuse()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        var first = app.ImportProducedJars("stone-batch-001",
            new Dictionary<string, int> { ["圆罐"] = 2, ["方罐"] = 1 });
        Assert.False(first.WasDuplicate);
        Assert.Equal(3, first.SubmittedJarCount);
        Assert.Equal(3, first.StoredJarCount);
        Assert.Equal(0, first.DiscardedJarCount);
        Assert.Equal(3, app.GetWorldSnapshot().ResourceJars.Count);

        var duplicate = app.ImportProducedJars("stone-batch-001",
            new Dictionary<string, int> { ["方罐"] = 1, ["圆罐"] = 2 });
        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(3, app.GetWorldSnapshot().ResourceJars.Count);

        Assert.Throws<DomainValidationException>(() =>
            app.ImportProducedJars("stone-batch-001",
                new Dictionary<string, int> { ["圆罐"] = 3 }));
        Assert.Equal(3, app.GetWorldSnapshot().ResourceJars.Count);
    }

    [Fact]
    public void ExternalProducedJarImportUsesWarehouseCapacityAndRecordsDiscardedOverflow()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        var result = app.ImportProducedJars("stone-batch-overflow",
            new Dictionary<string, int> { ["圆罐"] = Constants.WAREHOUSE_MAX_JARS + 1 });

        Assert.Equal(Constants.WAREHOUSE_MAX_JARS + 1, result.SubmittedJarCount);
        Assert.Equal(Constants.WAREHOUSE_MAX_JARS, result.StoredJarCount);
        Assert.Equal(1, result.DiscardedJarCount);
        Assert.Equal(Constants.WAREHOUSE_MAX_JARS, app.GetWorldSnapshot().ResourceJars.Count);
        var ledger = app.GetResourceLedger();
        Assert.Contains(ledger.Events, item =>
            item.Cause == "discard" && item.Kind == "jar" && item.Name == "圆罐" && item.Amount == 1);
    }

    [Fact]
    public void ExternalProducedJarImportRejectsUnknownShapeBeforeAnyMutation()
    {
        using var host = new WorldV2HostService();
        var app = new WorldV2ApplicationService(host);
        app.CreateOrReplaceWorld(Options());

        Assert.Throws<DomainValidationException>(() =>
            app.ImportProducedJars("stone-batch-invalid",
                new Dictionary<string, int> { ["未知罐"] = 1 }));
        Assert.Empty(app.GetWorldSnapshot().ResourceJars);
        Assert.Empty(app.GetResourceLedger().Events);
    }


    [Fact]
    public void ExternalProducedJarImportDeduplicationSurvivesExecutionCheckpointRestore()
    {
        using var firstHost = new WorldV2HostService();
        var firstApp = new WorldV2ApplicationService(firstHost);
        firstApp.CreateOrReplaceWorld(Options());
        firstApp.ImportProducedJars("stone-batch-checkpoint",
            new Dictionary<string, int> { ["方罐"] = 2 });

        var checkpointJson = WorldV2CheckpointJsonCodec.EncodeExecutionResourceCheckpoint(
            firstApp.CaptureExecutionResourceCheckpoint());

        using var restoredHost = new WorldV2HostService();
        var restoredApp = new WorldV2ApplicationService(restoredHost);
        restoredApp.CreateOrReplaceWorld(Options());
        restoredApp.RestoreExecutionResourceCheckpointJson(Options(), checkpointJson);

        var duplicate = restoredApp.ImportProducedJars("stone-batch-checkpoint",
            new Dictionary<string, int> { ["方罐"] = 2 });

        Assert.True(duplicate.WasDuplicate);
        Assert.Equal(2, restoredApp.GetWorldSnapshot().ResourceJars.Count);
    }

}

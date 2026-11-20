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
        Assert.Equal(1, before.Master.Liquids["酒"]);

        var begin = app.BeginWorkshopReplacement(
            WorkshopRole.Master, "备用台", phaseDurationLogicalMilli: 1_000);
        Assert.True(begin.Success);

        // 第一个逻辑阶段完成重组，但尚未完成转移和岗位提交。
        app.AdvanceWorld(1_000);
        var reshaped = app.GetWorldSnapshot();
        Assert.Equal("主台", reshaped.Master!.Name);
        Assert.Equal("酒", Assert.Single(reshaped.Master.Jars).Liquid);
        Assert.Equal(0, reshaped.Master.Liquids["酒"]);
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
        Assert.Equal("主台", after.Master!.Name);
        Assert.Single(after.Master.Jars);
        Assert.Null(after.Master.Jars[0].Liquid);
        Assert.Equal(1, after.Master.Liquids["酒"]);
        Assert.False(after.RuntimeFaulted);

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
}

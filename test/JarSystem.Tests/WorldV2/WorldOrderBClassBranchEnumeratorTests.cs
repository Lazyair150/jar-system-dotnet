using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.World;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderBClassBranchEnumeratorTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static (WorldSnapshot World, WorldOrderRuleCandidateProfile Master,
        WorldOrderRuleCandidateProfile Slave) Profiles(
        Workshop master,
        Workshop slave,
        IReadOnlyList<Requirement> requirements)
    {
        var world = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master, slave }, master, slave));
        var specification = new JarOrderSpecification(
            requirements, Constants.OrderType.B, Words);
        var masterProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Slave!, "Slave", specification);
        return (world, masterProfile, slaveProfile);
    }

    [Fact]
    public void MasterBannedFullJarCanCrossHandoffAndBeSelfConsumedByPermissiveSlave()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        var slave = new Workshop("允许用酒的从台",
            new List<string> { "圆罐", "方罐" });
        master.AddJar(new Jar("圆罐", "酒", id: 9101));

        var input = Profiles(master, slave, new[]
        {
            new Requirement { Shape = "方罐" }
        });
        var result = WorldOrderBClassBranchEnumerator.Enumerate(
            910, input.Master, input.Slave, input.World);

        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Feasible, result.Status);
        Assert.NotNull(result.Assembly);
        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Assembly!.Status);
        var plan = result.Assembly.CandidatePlan!;
        Assert.True(plan.IsPartitioned);
        Assert.Empty(plan.MasterSteps);
        Assert.Equal(new[] { 9101 }, plan.TransferJarIds);
        Assert.Empty(plan.TransferLiquids);
        var step = Assert.Single(plan.SlaveSteps);
        Assert.Equal("RESHAPE", step.Operation!.Type);
        Assert.True(step.Operation.FullReshape);
        Assert.Equal("酒", step.Operation.CostLiquid);
        Assert.Equal(9101, step.Before![step.Operation.JarIndex!.Value].Id);
        Assert.Equal("方罐", step.After![step.Operation.JarIndex!.Value].Shape);
        Assert.Null(step.After[step.Operation.JarIndex!.Value].Liquid);
        Assert.Single(result.Assembly.ResourceReplay!.SlaveBoundaryGroup!.Requirements);
    }

    [Fact]
    public void BClassFullJarCannotBeSelfConsumedWhenBothWorkshopsBanItsLiquid()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        var slave = new Workshop("也禁酒的从台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", "酒", id: 9121));

        var input = Profiles(master, slave, new[]
        {
            new Requirement { Shape = "方罐" }
        });
        var result = WorldOrderBClassBranchEnumerator.Enumerate(
            912, input.Master, input.Slave, input.World);

        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Infeasible, result.Status);
        Assert.Null(result.Assembly);
    }

    [Fact]
    public void BClassMixedBranchKeepsMasterAndSlaveLedgersSeparate()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        var slaveRules = new ModifierStack();
        slaveRules.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Reason = "从台不直接匹配圆罐"
        });
        var slave = new Workshop("允许用酒的从台",
            new List<string> { "圆罐", "方罐" }, modifiers: slaveRules);
        master.AddJar(new Jar("圆罐", id: 9151));
        master.AddJar(new Jar("圆罐", "酒", id: 9152));

        var input = Profiles(master, slave, new[]
        {
            new Requirement { Shape = "圆罐" },
            new Requirement { Shape = "方罐" }
        });
        var result = WorldOrderBClassBranchEnumerator.Enumerate(
            915, input.Master, input.Slave, input.World);

        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Feasible, result.Status);
        var plan = result.Assembly!.CandidatePlan!;
        Assert.Equal(1, plan.DirectCount);
        Assert.Single(plan.MasterSteps);
        Assert.Equal("MATCH", plan.MasterSteps[0].Operation!.Type);
        Assert.Single(plan.SlaveSteps);
        Assert.Equal("RESHAPE", plan.SlaveSteps[0].Operation!.Type);
        Assert.Equal(new[] { 9151, 9152 }, plan.TransferJarIds.OrderBy(x => x));
        Assert.Equal(9151, plan.MasterSteps[0].Before![plan.MasterSteps[0].Operation!.JarIndex!.Value].Id);
        Assert.Equal(9152, plan.SlaveSteps[0].Before![plan.SlaveSteps[0].Operation!.JarIndex!.Value].Id);
        Assert.Empty(result.Assembly.ResourceReplay!.TransferLiquids);
    }

    [Fact]
    public void SlavePreparationJarCanProduceLiquidForLaterSlaveRequirement()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        var slaveRules = new ModifierStack();
        slaveRules.Push(new SuppressModifier
        {
            Operation = "RESHAPE",
            Shape = "圆罐",
            Liquid = "酒",
            Reason = "强制从台先倒出游离液体再改形"
        });
        var slave = new Workshop("允许用酒的从台",
            new List<string> { "圆罐", "方罐" }, modifiers: slaveRules);
        master.AddJar(new Jar("圆罐", id: 9701));
        master.AddJar(new Jar("圆罐", "酒", id: 9702));

        var input = Profiles(master, slave, new[]
        {
            new Requirement { Shape = "方罐" }
        });
        var result = WorldOrderBClassBranchEnumerator.Enumerate(
            970, input.Master, input.Slave, input.World);

        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Feasible, result.Status);
        var plan = result.Assembly!.CandidatePlan!;
        Assert.Empty(plan.MasterSteps);
        Assert.Equal(new[] { 9701, 9702 }, plan.TransferJarIds.OrderBy(x => x));
        Assert.Equal(new[] { "POUR_OUT", "RESHAPE" },
            plan.SlaveSteps.Select(x => x.Operation!.Type));
        Assert.Equal(9702, plan.SlaveSteps[0].Before![plan.SlaveSteps[0].Operation!.JarIndex!.Value].Id);
        Assert.Equal(9701, plan.SlaveSteps[1].Before![plan.SlaveSteps[1].Operation!.JarIndex!.Value].Id);
        Assert.Equal(1, plan.SlaveSteps[0].AfterLiquids.GetValueOrDefault("酒"));
        Assert.Equal(0, plan.SlaveSteps[1].AfterLiquids.GetValueOrDefault("酒"));
        Assert.DoesNotContain(plan.TransferLiquids, item => item.Value > 0);
    }

    [Fact]
    public void BClassCannotReuseOnePhysicalJarForTwoRequirementIdentities()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" });
        var slave = new Workshop("从台", new List<string> { "圆罐", "方罐" });
        master.AddJar(new Jar("圆罐", id: 9201));
        var input = Profiles(master, slave, new[]
        {
            new Requirement { Shape = "圆罐" },
            new Requirement { Shape = "圆罐" }
        });

        var result = WorldOrderBClassBranchEnumerator.Enumerate(
            920, input.Master, input.Slave, input.World);

        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Infeasible, result.Status);
        Assert.Null(result.Assembly);
    }

    [Fact]
    public void BClassBudgetExhaustionIsUndetermined()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" });
        var slave = new Workshop("从台", new List<string> { "圆罐", "方罐" });
        master.AddJar(new Jar("圆罐", id: 9301));
        var input = Profiles(master, slave, new[]
        {
            new Requirement { Shape = "圆罐" }
        });

        var result = WorldOrderBClassBranchEnumerator.Enumerate(
            930, input.Master, input.Slave, input.World,
            maxBranches: 1, maxNodes: 1);

        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Undetermined, result.Status);
    }
}

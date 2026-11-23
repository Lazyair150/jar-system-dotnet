using System.Linq;
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

        Assert.True(result.Status == WorldOrderBClassBranchEnumerationStatus.Feasible, result.Reason);
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
            Liquid = "蜜",
            Reason = "只禁止满酒罐改形，允许倒酒后供空罐改形"
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

        Assert.True(result.Status == WorldOrderBClassBranchEnumerationStatus.Feasible, result.Reason);
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
    public void MasterPreparationOutputCanCrossAsFreeLiquidForSlavePourIn()
    {
        var masterRules = new ModifierStack();
        masterRules.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Reason = "主台不直接匹配圆罐"
        });
        masterRules.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Reason = "主台不执行倒入操作"
        });
        var slaveRules = new ModifierStack();
        slaveRules.Push(new SuppressModifier
        {
            Operation = "POUR_OUT",
            Reason = "从台不执行倒出操作"
        });
        slaveRules.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Liquid = "蜜",
            Reason = "从台不直接交付满蜜罐"
        });
        slaveRules.Push(new SuppressModifier
        {
            Operation = "RESHAPE",
            Shape = "圆罐",
            Liquid = "蜜",
            Reason = "从台不使用满蜜罐改形"
        });
        var master = new Workshop("主台",
            new List<string> { "圆罐" }, modifiers: masterRules);
        var slave = new Workshop("从台",
            new List<string> { "圆罐" }, modifiers: slaveRules);
        master.AddJar(new Jar("圆罐", id: 9851));
        master.AddJar(new Jar("圆罐", "蜜", id: 9852));

        var input = Profiles(master, slave, new[]
        {
            new Requirement { Shape = "圆罐", Liquid = "蜜" }
        });
        var result = WorldOrderBClassBranchEnumerator.Enumerate(
            985, input.Master, input.Slave, input.World);

        Assert.True(result.Status == WorldOrderBClassBranchEnumerationStatus.Feasible, result.Reason);
        var plan = result.Assembly!.CandidatePlan!;
        Assert.Equal(new[] { 9851 }, plan.TransferJarIds);
        Assert.Equal(1, plan.TransferLiquids.GetValueOrDefault("蜜"));
        Assert.Equal("POUR_OUT", Assert.Single(plan.MasterSteps).Operation!.Type);
        Assert.Equal("POUR_IN", Assert.Single(plan.SlaveSteps).Operation!.Type);
        Assert.Equal(9851, plan.SlaveSteps[0].Before![plan.SlaveSteps[0].Operation!.JarIndex!.Value].Id);
        Assert.Equal("蜜", plan.SlaveSteps[0].After![plan.SlaveSteps[0].Operation!.JarIndex!.Value].Liquid);
    }

    [Fact]
    public void TwoIdenticalMasterPreparationJarsCanBothFundTwoSlaveRequirements()
    {
        var master = new Workshop(
            "主台", new List<string> { "圆罐", "方罐" },
            liquidStagingCapacity: 2);
        var slave = new Workshop("从台", new List<string> { "圆罐", "方罐" });
        master.AddJar(new Jar("圆罐", "酒", id: 9861));
        master.AddJar(new Jar("圆罐", "酒", id: 9862));
        master.AddJar(new Jar("方罐", id: 9863));
        master.AddJar(new Jar("方罐", id: 9864));

        var input = Profiles(master, slave, new[]
        {
            new Requirement { Shape = "方罐", Liquid = "酒" },
            new Requirement { Shape = "方罐", Liquid = "酒" }
        });
        var result = WorldOrderBClassBranchEnumerator.Enumerate(
            986, input.Master, input.Slave, input.World);

        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Feasible, result.Status);
        var plan = result.Assembly!.CandidatePlan!;
        Assert.Equal(4, plan.TransferJarIds.Count);
        Assert.Equal(2, plan.TransferLiquids.GetValueOrDefault("酒"));
        Assert.Equal(2, plan.MasterSteps.Count);
        Assert.Equal(2, plan.SlaveSteps.Count);
        Assert.All(plan.MasterSteps, step => Assert.Equal("POUR_OUT", step.Operation!.Type));
        Assert.All(plan.SlaveSteps, step => Assert.Equal("POUR_IN", step.Operation!.Type));
        Assert.Equal(
            new[] { 9861, 9862 },
            plan.MasterSteps.Select(step =>
                step.Before![step.Operation!.JarIndex!.Value].Id!.Value).OrderBy(id => id));
        Assert.Equal(
            new[] { 9863, 9864 },
            plan.SlaveSteps.Select(step =>
                step.Before![step.Operation!.JarIndex!.Value].Id!.Value).OrderBy(id => id));
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
    public void BClassCannotUsePreExistingSlaveInventory()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" });
        var slave = new Workshop("从台", new List<string> { "圆罐", "方罐" });
        master.AddJar(new Jar("圆罐", id: 9801));
        slave.AddJar(new Jar("圆罐", id: 9802));
        var input = Profiles(master, slave, new[]
        {
            new Requirement { Shape = "圆罐" }
        });

        var result = WorldOrderBClassBranchEnumerator.Enumerate(
            980, input.Master, input.Slave, input.World);

        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Infeasible, result.Status);
        Assert.Null(result.Assembly);

        var emptyMaster = new Workshop("主台", new List<string> { "圆罐", "方罐" });
        var fundedSlave = new Workshop("已有游离液体的从台", new List<string> { "圆罐", "方罐" });
        emptyMaster.AddJar(new Jar("圆罐", id: 9803));
        fundedSlave.AddLiquid("酒", 1);
        var liquidInput = Profiles(emptyMaster, fundedSlave, new[]
        {
            new Requirement { Shape = "圆罐" }
        });
        var liquidResult = WorldOrderBClassBranchEnumerator.Enumerate(
            981, liquidInput.Master, liquidInput.Slave, liquidInput.World);

        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Infeasible, liquidResult.Status);
        Assert.Null(liquidResult.Assembly);
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

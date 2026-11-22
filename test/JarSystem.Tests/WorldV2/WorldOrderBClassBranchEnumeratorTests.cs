using System.Collections.Generic;
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

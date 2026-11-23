using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Solver;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderBClassDifferentialTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    [Fact]
    public void ConcreteEnumeratorMatchesPartitionOracleForMasterBannedFullJarHandoff()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        var slave = new Workshop("允许用酒的从台",
            new List<string> { "圆罐", "方罐" });
        master.AddJar(new Jar("圆罐", "酒", id: 9501));

        var world = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master, slave }, master, slave));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "方罐" } },
            Constants.OrderType.B, Words);
        var demand = new OrderDemand(specification, 0);

        // The legacy entry remains the differential oracle; it is intentionally not
        // replaced by the concrete enumerator in this slice.
        var legacy = new JarOrderSpecificationSolver(new StateSpaceSolver())
            .Solve(demand, world);
        var masterProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Slave!, "Slave", specification);
        var concrete = WorldOrderBClassBranchEnumerator.Enumerate(
            demand.Id, masterProfile, slaveProfile, world);

        Assert.True(legacy.Status == WorldOrderSolveStatus.Feasible, legacy.Reason);
        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Feasible, concrete.Status);
        Assert.NotNull(legacy.Plan);
        Assert.NotNull(concrete.Assembly);
        var plan = concrete.Assembly!.CandidatePlan!;
        Assert.Equal(legacy.Plan!.TransferJarIds.OrderBy(x => x),
            plan.TransferJarIds.OrderBy(x => x));
        Assert.DoesNotContain(legacy.Plan.TransferLiquids, item => item.Value > 0);
        Assert.DoesNotContain(plan.TransferLiquids, item => item.Value > 0);
        Assert.Equal(legacy.Plan.SlaveSteps.Count, plan.SlaveSteps.Count);
        Assert.Equal("RESHAPE", Assert.Single(plan.SlaveSteps).Operation!.Type);
        Assert.True(plan.SlaveSteps[0].Operation!.FullReshape);
    }

    [Fact]
    public void ConcreteEnumeratorAndPartitionOracleBothRejectWhenBothRolesBanJarLiquid()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        var slave = new Workshop("也禁酒的从台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", "酒", id: 9511));

        var world = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master, slave }, master, slave));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "方罐" } },
            Constants.OrderType.B, Words);
        var demand = new OrderDemand(specification, 0);
        var legacy = new JarOrderSpecificationSolver(new StateSpaceSolver())
            .Solve(demand, world);
        var masterProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Slave!, "Slave", specification);
        var concrete = WorldOrderBClassBranchEnumerator.Enumerate(
            demand.Id, masterProfile, slaveProfile, world);

        Assert.Equal(WorldOrderSolveStatus.Infeasible, legacy.Status);
        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Infeasible, concrete.Status);
    }
}

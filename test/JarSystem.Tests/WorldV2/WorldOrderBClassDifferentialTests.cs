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

        // Compare the new production branch against the retained legacy oracle.
        var legacy = new JarOrderSpecificationSolver(new StateSpaceSolver())
            .SolvePartitionOracleForDifferentialTesting(demand, world);
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

    [Theory]
    [InlineData("酒", WorkshopPolarityMode.Unrestricted, true)]
    [InlineData("酒", WorkshopPolarityMode.Cathode, true)]
    [InlineData("酒", WorkshopPolarityMode.Anode, true)]
    [InlineData("酒", WorkshopPolarityMode.NeutralOnly, true)]
    [InlineData("蜜", WorkshopPolarityMode.Unrestricted, true)]
    [InlineData("蜜", WorkshopPolarityMode.Cathode, true)]
    [InlineData("蜜", WorkshopPolarityMode.Anode, true)]
    [InlineData("蜜", WorkshopPolarityMode.NeutralOnly, true)]
    public void AAndBEnumeratorsShareTheSameWorkshopPolarityGate(
        string? liquid, WorkshopPolarityMode mode, bool expected)
    {
        var bans = mode switch
        {
            WorkshopPolarityMode.Unrestricted => new List<string>(),
            WorkshopPolarityMode.Cathode => new List<string> { "蜜" },
            WorkshopPolarityMode.Anode => new List<string> { "酒" },
            WorkshopPolarityMode.NeutralOnly => new List<string> { "酒", "蜜" },
            _ => throw new System.ArgumentOutOfRangeException(nameof(mode))
        };
        Assert.Equal(WorkshopPolarityMode.Unrestricted,
            new WorkshopCapability(ban: bans).PolarityMode);
        Assert.True(expected); // Every full-Jar operation is independent of staging Ban.

        var masterA = new Workshop("A主台", new List<string> { "圆罐", "方罐" }, ban: bans);
        masterA.AddJar(new Jar("圆罐", liquid, id: 9601));
        var worldA = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { masterA }, masterA));
        var specificationA = new JarOrderSpecification(
            new[] { new Requirement { Shape = "方罐" } }, Constants.OrderType.A, Words);
        var profileA = WorldOrderRuleCandidateCatalog.Build(
            worldA, worldA.Master!, "Master", specificationA);
        var resultA = WorldOrderAClassBranchEnumerator.Enumerate(
            960, profileA, worldA);

        var masterB = new Workshop("B主台", new List<string> { "圆罐", "方罐" }, ban: bans);
        var slaveB = new Workshop("B从台", new List<string> { "圆罐", "方罐" }, ban: bans);
        masterB.AddJar(new Jar("圆罐", liquid, id: 9602));
        var worldB = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { masterB, slaveB }, masterB, slaveB));
        var specificationB = new JarOrderSpecification(
            new[] { new Requirement { Shape = "方罐" } }, Constants.OrderType.B, Words);
        var masterProfile = WorldOrderRuleCandidateCatalog.Build(
            worldB, worldB.Master!, "Master", specificationB);
        var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
            worldB, worldB.Slave!, "Slave", specificationB);
        var resultB = WorldOrderBClassBranchEnumerator.Enumerate(
            961, masterProfile, slaveProfile, worldB);

        var expectedStatusA = expected
            ? WorldOrderAClassBranchEnumerationStatus.Feasible
            : WorldOrderAClassBranchEnumerationStatus.Infeasible;
        var expectedStatusB = expected
            ? WorldOrderBClassBranchEnumerationStatus.Feasible
            : WorldOrderBClassBranchEnumerationStatus.Infeasible;
        Assert.Equal(expectedStatusA, resultA.Status);
        Assert.Equal(expectedStatusB, resultB.Status);
    }

    [Fact]
    public void ConcreteEnumeratorAndPartitionOracleAllowJarOperationWhenBothWorkshopsBanItsLiquid()
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
            .SolvePartitionOracleForDifferentialTesting(demand, world);
        var masterProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Slave!, "Slave", specification);
        var concrete = WorldOrderBClassBranchEnumerator.Enumerate(
            demand.Id, masterProfile, slaveProfile, world);

        Assert.Equal(WorldOrderSolveStatus.Feasible, legacy.Status);
        Assert.Equal(WorldOrderBClassBranchEnumerationStatus.Feasible, concrete.Status);
    }
}

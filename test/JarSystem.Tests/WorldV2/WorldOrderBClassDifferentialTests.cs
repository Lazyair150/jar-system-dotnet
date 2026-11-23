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
    public void ConcreteEnumeratorMatchesPartitionOracleForMasterProducedSlaveConsumedLiquid()
    {
        var masterRules = new ModifierStack();
        masterRules.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Reason = "强制主台从满罐倒出液体"
        });
        var slaveRules = new ModifierStack();
        slaveRules.Push(new SuppressModifier
        {
            Operation = "POUR_OUT",
            Reason = "从台只能使用主台交接的游离液体"
        });
        slaveRules.Push(new SuppressModifier
        {
            Operation = "RESHAPE",
            Shape = "圆罐",
            Liquid = "酒",
            Reason = "从台必须消耗交接的游离液体"
        });
        var master = new Workshop("主台",
            new List<string> { "圆罐" }, modifiers: masterRules);
        var slave = new Workshop("从台",
            new List<string> { "方罐" }, modifiers: slaveRules);
        master.AddJar(new Jar("圆罐", id: 9601));
        master.AddJar(new Jar("圆罐", "酒", id: 9602));

        var world = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master, slave }, master, slave));
        var specification = new JarOrderSpecification(new[]
        {
            new Requirement { Shape = "方罐" },
            new Requirement { Shape = "圆罐" }
        }, Constants.OrderType.B, Words);
        var demand = new OrderDemand(specification, 0);

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
        var legacyPlan = legacy.Plan!;
        var plan = concrete.Assembly!.CandidatePlan!;
        Assert.Equal(new[] { 9601, 9602 }, legacyPlan.TransferJarIds.OrderBy(x => x));
        Assert.Equal(legacyPlan.TransferJarIds.OrderBy(x => x),
            plan.TransferJarIds.OrderBy(x => x));
        Assert.Equal(1, legacyPlan.TransferLiquids.GetValueOrDefault("酒"));
        Assert.Equal(1, plan.TransferLiquids.GetValueOrDefault("酒"));
        Assert.Equal("POUR_OUT", Assert.Single(plan.MasterSteps).Operation!.Type);
        Assert.Equal("RESHAPE", Assert.Single(plan.SlaveSteps).Operation!.Type);
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

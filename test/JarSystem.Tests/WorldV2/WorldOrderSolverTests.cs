using System.Collections.Generic;
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

public sealed class WorldOrderSolverTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static OrderDemand Demand(string type = Constants.OrderType.A, string shape = "圆罐") =>
        new(new JarOrderSpecification(
            new[] { new Requirement { Shape = shape } }, type, Words),
            0, new SequentialIdGenerator());

    private static WorldSnapshot Snapshot(Workshop master, Workshop? slave = null)
    {
        var workshops = new List<Workshop> { master };
        if (slave != null) workshops.Add(slave);
        return WorldSnapshotFactory.Capture(new WorkshopRoster(workshops, master, slave));
    }

    private static WorldOrderSolverRegistry Registry() =>
        new(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });

    [Fact]
    public void RegistryDispatchesJarSpecificationAndBindsResultToDemandAndSnapshotVersion()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var snapshot = Snapshot(master);
        var demand = Demand();

        var result = Registry().Solve(demand, snapshot);

        Assert.Equal(demand.Id, result.DemandId);
        Assert.Equal(snapshot.Version, result.WorldVersion);
        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Equal("single_phase", result.CheckMethod);
        Assert.Equal(1, result.DirectCount);
        Assert.NotNull(result.Plan);
        Assert.False(result.Plan!.IsPartitioned);
        Assert.NotEmpty(result.Plan!.MasterSteps);
    }

    [Fact]
    public void UnsupportedSpecificationIsNotSilentlyTreatedAsInfeasible()
    {
        var registry = Registry();
        var demand = new OrderDemand(new ForeignSpecification(), 0, new SequentialIdGenerator());
        var snapshot = Snapshot(new Workshop("主台"));

        var result = registry.Solve(demand, snapshot);

        Assert.Equal(WorldOrderSolveStatus.UnsupportedSpecification, result.Status);
    }

    [Fact]
    public void OpaqueSnapshotRuleProducesUndeterminedInsteadOfFalseInfeasibility()
    {
        var stack = new ModifierStack();
        var condition = true;
        stack.Push(new RequireModifier
        {
            Text = "外部条件",
            Violation = () => condition ? null : "不满足"
        });
        var master = new Workshop("主台", modifiers: stack);
        master.AddJar(new Jar("圆罐"));
        var snapshot = Snapshot(master);

        var result = Registry().Solve(Demand(), snapshot);

        Assert.Equal(WorldOrderSolveStatus.Undetermined, result.Status);
        Assert.Contains("无法静态投影", result.Reason ?? string.Empty);
    }

    [Fact]
    public void BSpecificationUsesMasterSlavePartitionSearch()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        master.AddJar(new Jar("圆罐"));
        var snapshot = Snapshot(master, slave);
        var demand = Demand(Constants.OrderType.B);

        var result = Registry().Solve(demand, snapshot);

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Equal("partition", result.CheckMethod);
        Assert.Equal(1, result.DirectCount);
        Assert.NotNull(result.Plan);
        Assert.True(result.Plan.IsPartitioned);
    }

    [Fact]
    public void BSpecificationRequiresBothWorkshopRoles()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));

        var result = Registry().Solve(Demand(Constants.OrderType.B), Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.InvalidWorld, result.Status);
    }

    [Fact]
    public void WholeShapeSuppressionIsProjectedFromTheSnapshot()
    {
        var stack = new ModifierStack();
        stack.Push(new SuppressModifier { Shape = "圆罐" });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        master.AddJar(new Jar("圆罐"));

        var result = Registry().Solve(Demand(), Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.Infeasible, result.Status);
    }

    [Fact]
    public void ExhaustedSolverBudgetIsUndeterminedNotInfeasible()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var solver = new JarOrderSpecificationSolver(new StateSpaceSolver()) { NodeBudget = 1 };
        var registry = new WorldOrderSolverRegistry(new[] { solver });

        var result = registry.Solve(Demand(), Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.Undetermined, result.Status);
    }

    [Fact]
    public void RegistryDoesNotSolveDemandDuringItsCooldown()
    {
        var demand = new OrderDemand(
            new JarOrderSpecification(new[] { new Requirement { Shape = "圆罐" } }, Constants.OrderType.A, Words),
            1_000, new SequentialIdGenerator());
        demand.EnterCooldown();
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));

        var result = Registry().Solve(demand, Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.NotReady, result.Status);
    }

    [Fact]
    public void BSpecificationRejectsNonEmptySlaveSnapshot()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        master.AddJar(new Jar("圆罐"));
        slave.AddJar(new Jar("方罐"));

        var result = Registry().Solve(Demand(Constants.OrderType.B), Snapshot(master, slave));

        Assert.Equal(WorldOrderSolveStatus.InvalidWorld, result.Status);
    }

    private sealed class ForeignSpecification : IOrderSpecification
    {
    }
}

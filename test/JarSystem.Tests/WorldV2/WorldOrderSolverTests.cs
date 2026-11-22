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
    public void RuleCandidateProfileIsReusedAcrossResourceOnlyChanges()
    {
        var stack = new ModifierStack();
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        var roster = new WorkshopRoster(new[] { master }, master);
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
            Constants.OrderType.A, Words);
        var book = new WorldOrderRuleCandidateBook();

        var firstSnapshot = WorldSnapshotFactory.Capture(roster);
        var first = book.GetOrBuild(firstSnapshot, firstSnapshot.Master!, "Master", specification);
        var options = Assert.Single(first.Requirements).Candidates;
        Assert.Contains(options, candidate => candidate.Operation == "MATCH");
        Assert.Contains(options, candidate => candidate.Operation == "POUR_IN");
        Assert.True(first.CanStructurallyCoverEveryRequirement);

        master.AddLiquid("蜜", 1);
        var resourceChangedSnapshot = WorldSnapshotFactory.Capture(roster);
        Assert.NotEqual(firstSnapshot.ResourceVersion, resourceChangedSnapshot.ResourceVersion);
        var reused = book.GetOrBuild(
            resourceChangedSnapshot, resourceChangedSnapshot.Master!, "Master", specification);

        Assert.Same(first, reused);
        Assert.Equal(1, book.CacheMisses);
        Assert.Equal(1, book.CacheHits);

        stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "禁止灌酒"
        });
        var ruleChangedSnapshot = WorldSnapshotFactory.Capture(roster);
        var rebuilt = book.GetOrBuild(
            ruleChangedSnapshot, ruleChangedSnapshot.Master!, "Master", specification);

        Assert.NotSame(first, rebuilt);
        Assert.DoesNotContain(
            Assert.Single(rebuilt.Requirements).Candidates,
            candidate => candidate.Operation == "POUR_IN");
        Assert.Contains(
            Assert.Single(rebuilt.Requirements).Candidates,
            candidate => candidate.Operation == "MATCH");
        Assert.Equal(2, book.CacheMisses);
    }

    [Fact]
    public void OpaqueRequireProducesUndeterminedRuleCandidateProfile()
    {
        var stack = new ModifierStack();
        stack.Push(new RequireModifier
        {
            Text = "外部世界条件",
            Violation = () => null
        });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        var snapshot = Snapshot(master);
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐" } },
            Constants.OrderType.A, Words);

        var profile = WorldOrderRuleCandidateCatalog.Build(
            snapshot, snapshot.Master!, "Master", specification);

        Assert.True(profile.IsUndetermined);
        Assert.False(profile.CanStructurallyCoverEveryRequirement);
        Assert.Contains("无法静态重建", profile.Reason ?? string.Empty);
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
    public void OperationSpecificSuppressionDoesNotDeleteJarOrBlockMatch()
    {
        var stack = new ModifierStack();
        stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "禁止灌酒"
        });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        master.AddJar(new Jar("圆罐"));

        var result = Registry().Solve(Demand(), Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.NotNull(result.Plan);
        Assert.Equal("MATCH", Assert.Single(result.Plan!.MasterSteps).Operation!.Type);
    }

    [Fact]
    public void GrantModifierParticipatesInTheSameRuleContextUsedByTheSolver()
    {
        var stack = new ModifierStack();
        stack.Push(new GrantModifier { Shape = "方罐", Source = "test" });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        master.AddJar(new Jar("圆罐"));
        master.AddLiquid("酒", 1);
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "方罐" } },
                Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());

        var result = Registry().Solve(demand, Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Contains(result.Plan!.MasterSteps, step => step.Operation?.Type == "RESHAPE");
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

    [Fact]
    public void RegistryRejectsSolverResultBoundToAnotherDemand()
    {
        var demand = Demand();
        var snapshot = Snapshot(new Workshop("主台"));
        var registry = new WorldOrderSolverRegistry(new[]
        {
            new MismatchedResultSolver()
        });

        Assert.Throws<System.InvalidOperationException>(() => registry.Solve(demand, snapshot));
    }

    private sealed class MismatchedResultSolver : IWorldOrderSpecificationSolver
    {
        public System.Type SpecificationType => typeof(JarOrderSpecification);

        public WorldOrderSolveResult Solve(OrderDemand demand, WorldSnapshot snapshot) =>
            new(demand.Id + 1, snapshot.Version, WorldOrderSolveStatus.Infeasible);
    }

    private sealed class ForeignSpecification : IOrderSpecification
    {
    }

    [Fact]
    public void ShipInventoryIsTheSolverSupplySourceWhenPresent()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        master.AddLiquid("蜜", 1); // 旧工作台池不再为新路径提供液体
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());

        var suppliedByShip = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master }, master),
            new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 }));
        var first = Registry().Solve(demand, suppliedByShip);

        Assert.Equal(WorldOrderSolveStatus.Feasible, first.Status);

        master.AddLiquid("酒", 1);
        var shipWithoutWine = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master }, master),
            new ShipLiquidInventory(4, new Dictionary<string, int> { ["蜜"] = 1 }));
        var second = Registry().Solve(demand, shipWithoutWine);

        Assert.Equal(WorldOrderSolveStatus.Infeasible, second.Status);
    }


    [Fact]
    public void BPartitionCountsRequiredSlaveTransferAgainstMasterStagingCapacity()
    {
        var master = new Workshop("主台", new List<string>(),
            maxJars: 4, maxLiquids: 1, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 2 });
        var snapshot = WorldSnapshotFactory.Capture(roster, ship);
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[]
                {
                    new Requirement { Shape = "圆罐", Liquid = "酒" },
                    new Requirement { Shape = "圆罐", Liquid = "酒" }
                }, Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());

        var result = Registry().Solve(demand, snapshot);

        Assert.Equal(WorldOrderSolveStatus.Infeasible, result.Status);
        Assert.Contains("主台订单液体路径及从台交接预留", result.Reason ?? string.Empty);
    }

}

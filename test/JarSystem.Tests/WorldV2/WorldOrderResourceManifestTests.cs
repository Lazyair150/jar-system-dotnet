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

public sealed class WorldOrderResourceManifestTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static WorldOrderSolveResult Solve(
        string type, Requirement requirement, Workshop master, Workshop? slave = null)
    {
        var roster = new WorkshopRoster(
            slave == null ? new[] { master } : new[] { master, slave }, master, slave);
        var snapshot = WorldSnapshotFactory.Capture(roster);
        var demand = new OrderDemand(
            new JarOrderSpecification(new[] { requirement }, type, Words),
            0, new SequentialIdGenerator());
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        return registry.Solve(demand, snapshot);
    }

    [Fact]
    public void APlanReservesOnlyTheJarAndPeakFreeLiquidActuallyNeeded()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var jar = new Jar("圆罐");
        master.AddJar(jar);
        master.AddLiquid("酒", 2);

        var result = Solve(Constants.OrderType.A,
            new Requirement { Shape = "圆罐", Liquid = "酒" }, master);
        var snapshot = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master }, master));

        Assert.True(result.IsFeasible);
        var manifest = WorldOrderResourceManifest.Create(result, snapshot);

        Assert.Equal(new[] { jar.Id!.Value }, manifest.MasterJarIds);
        Assert.Equal(new[] { jar.Id!.Value }, manifest.LockedJarIds);
        Assert.Equal(1, manifest.MasterInitialLiquidAmounts["酒"]);
        Assert.Empty(manifest.TransferJarIds);
        Assert.Empty(manifest.TransferLiquids);
    }

    [Fact]
    public void BPlanLocksTheTransferredDeliveryJarEvenWhenMasterPathIsEmpty()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        var jar = new Jar("圆罐");
        master.AddJar(jar);

        var result = Solve(Constants.OrderType.B,
            new Requirement { Shape = "圆罐" }, master, slave);
        var snapshot = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master, slave }, master, slave));

        Assert.True(result.IsFeasible);
        Assert.True(result.Plan!.IsPartitioned);
        Assert.Empty(result.Plan.MasterSteps);
        Assert.NotEmpty(result.Plan.SlaveSteps);

        var manifest = WorldOrderResourceManifest.Create(result, snapshot);

        Assert.Equal(new[] { jar.Id!.Value }, manifest.TransferJarIds);
        Assert.Equal(new[] { jar.Id!.Value }, manifest.LockedJarIds);
        Assert.Empty(manifest.MasterJarIds);
    }

    [Fact]
    public void ManifestRejectsAPlanBoundToAnOlderWorldVersion()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var roster = new WorkshopRoster(new[] { master }, master);
        var oldSnapshot = WorldSnapshotFactory.Capture(roster);
        var demand = new OrderDemand(
            new JarOrderSpecification(new[] { new Requirement { Shape = "圆罐" } },
                Constants.OrderType.A, Words), 0, new SequentialIdGenerator());
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        var result = registry.Solve(demand, oldSnapshot);

        master.AddLiquid("酒", 1);
        var currentSnapshot = WorldSnapshotFactory.Capture(roster);

        Assert.Throws<System.InvalidOperationException>(
            () => WorldOrderResourceManifest.Create(result, currentSnapshot));
    }

    [Fact]
    public void BManifestCountsOnlySlaveRequiredLiquidAgainstSlaveCapacity()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxLiquids: 1, vocabulary: Words);
        var sourceJar = new Jar("圆罐", "蜜", vocabulary: Words);
        var toolJar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(sourceJar);
        master.AddJar(toolJar);
        master.AddLiquid("酒", 1);

        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var snapshot = WorldSnapshotFactory.Capture(roster);

        var masterBefore = new List<JarState>
        {
            new() { Id = sourceJar.Id, Shape = "圆罐", Liquid = "蜜" },
            new() { Id = toolJar.Id, Shape = "圆罐" }
        };
        var masterAfter = new List<JarState>
        {
            new() { Id = sourceJar.Id, Shape = "圆罐", IsMarked = true },
            new() { Id = toolJar.Id, Shape = "圆罐" }
        };
        var masterStep = new SolverStep
        {
            Step = 0,
            Requirement = new Requirement { Shape = "圆罐" },
            Operation = new SolverOperation
            {
                Type = "POUR_OUT", JarIndex = 0, TargetLiquid = "蜜",
                Consumed = new Dictionary<string, int>()
            },
            Before = masterBefore,
            After = masterAfter,
            AfterLiquids = new Dictionary<string, int> { ["酒"] = 1, ["蜜"] = 1 }
        };

        var slaveBefore = new List<JarState>
        {
            new() { Id = toolJar.Id, Shape = "圆罐", MasterOrigin = true }
        };
        var slaveAfter = new List<JarState>
        {
            new() { Id = toolJar.Id, Shape = "圆罐", Liquid = "酒", IsMarked = true, MasterOrigin = true }
        };
        var slaveStep = new SolverStep
        {
            Step = 0,
            Requirement = new Requirement { Shape = "圆罐", Liquid = "酒" },
            Operation = new SolverOperation
            {
                Type = "POUR_IN", JarIndex = 0, TargetLiquid = "酒",
                Consumed = new Dictionary<string, int> { ["酒"] = 1 }
            },
            Before = slaveBefore,
            After = slaveAfter,
            AfterLiquids = new Dictionary<string, int>()
        };

        var outcome = new PartitionSearchOutcome
        {
            MasterPath = new List<SolverStep> { masterStep },
            SlavePath = new List<SolverStep> { slaveStep },
            TransferJarIds = new List<int> { sourceJar.Id!.Value, toolJar.Id!.Value },
            TransferLiquids = new Dictionary<string, int> { ["酒"] = 1 }
        };
        var plan = WorldOrderSolvePlan.Partitioned(outcome);
        var result = new WorldOrderSolveResult(
            1, snapshot.Version, WorldOrderSolveStatus.Feasible, plan: plan);

        var manifest = WorldOrderResourceManifest.Create(result, snapshot);

        Assert.Equal(1, manifest.MasterInitialLiquidAmounts["酒"]);
        Assert.Equal(1, manifest.TransferLiquids["酒"]);
        Assert.Equal(1, manifest.MasterFinalLiquidBalances["酒"]);
        Assert.Equal(1, manifest.MasterFinalLiquidBalances["蜜"]);
        Assert.Equal(2, manifest.MasterStagingPeakCapacity);
        Assert.Equal(1, manifest.SlaveStagingPeakCapacity);
    }
}

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

        Assert.Equal(new[] { jar.Id }, manifest.MasterJarIds);
        Assert.Equal(new[] { jar.Id }, manifest.LockedJarIds);
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

        Assert.Equal(new[] { jar.Id }, manifest.TransferJarIds);
        Assert.Equal(new[] { jar.Id }, manifest.LockedJarIds);
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
}

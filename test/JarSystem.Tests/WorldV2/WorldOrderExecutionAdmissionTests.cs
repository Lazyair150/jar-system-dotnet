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

public sealed class WorldOrderExecutionAdmissionTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static OrderDemand Demand(string shape, string? liquid, SequentialIdGenerator ids)
    {
        return new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = shape, Liquid = liquid } },
                Constants.OrderType.A, Words),
            0, ids);
    }

    private static (OrderDemandBoard Board, WorldOrderSolveBook Book,
        WorkshopRoster Roster, WorldResourceReservationBook Reservations,
        WorldOrderExecutionAdmission Admission) Prepare(
        Workshop master, params OrderDemand[] demands)
    {
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot,
            DemandAreaFulfillmentPolicy.OneShot);
        foreach (var demand in demands)
            board.AddDemand(demand, DemandAreaKind.Active);

        var roster = new WorkshopRoster(new[] { master }, master);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        var snapshot = WorldSnapshotFactory.Capture(roster);
        foreach (var demand in demands)
            book.Evaluate(demand.Id, snapshot, registry);

        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations);
        return (board, book, roster, reservations, admission);
    }

    [Fact]
    public void AdmissionReservesJarAndInitialLiquidThenReleaseMakesThemAvailable()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 2);

        var ids = new SequentialIdGenerator();
        var first = Demand("圆罐", "酒", ids);
        var second = Demand("圆罐", "酒", ids);
        var setup = Prepare(master, first, second);

        var accepted = setup.Admission.TryAdmit(first.Id);
        Assert.True(accepted.Success, accepted.Reason);
        Assert.NotNull(accepted.Manifest);
        Assert.True(setup.Reservations.IsJarReserved(jar.Id!.Value));
        Assert.Equal(first.Id, setup.Reservations.JarReservationOwner(jar.Id.Value));
        Assert.Equal(1, setup.Reservations.ReservedLiquidAmount("酒"));

        var conflict = setup.Admission.TryAdmit(second.Id);
        Assert.False(conflict.Success);
        Assert.Contains("预留", conflict.Reason);

        Assert.True(setup.Admission.Release(first.Id));
        Assert.False(setup.Reservations.IsJarReserved(jar.Id.Value));

        var acceptedAfterRelease = setup.Admission.TryAdmit(second.Id);
        Assert.True(acceptedAfterRelease.Success, acceptedAfterRelease.Reason);
    }

    [Fact]
    public void FailedLiquidReservationDoesNotLeavePartialJarReservation()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            vocabulary: Words);
        var roundJar = new Jar("圆罐", vocabulary: Words);
        var squareJar = new Jar("方罐", vocabulary: Words);
        master.AddJar(roundJar);
        master.AddJar(squareJar);
        master.AddLiquid("酒", 1);

        var ids = new SequentialIdGenerator();
        var roundDemand = Demand("圆罐", "酒", ids);
        var squareDemand = Demand("方罐", "酒", ids);
        var setup = Prepare(master, roundDemand, squareDemand);

        var first = setup.Admission.TryAdmit(roundDemand.Id);
        Assert.True(first.Success, first.Reason);
        Assert.True(setup.Reservations.IsJarReserved(roundJar.Id!.Value));

        var second = setup.Admission.TryAdmit(squareDemand.Id);
        Assert.False(second.Success);
        Assert.Contains("液体", second.Reason);
        Assert.False(setup.Reservations.IsJarReserved(squareJar.Id!.Value));
        Assert.Null(setup.Reservations.Find(squareDemand.Id));
        Assert.Equal(1, setup.Reservations.Count);
    }

    [Fact]
    public void BAdmissionReservesMasterStartingLiquidOnlyOnceAndChecksSlaveTransfer()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);

        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot,
            DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        var snapshot = WorldSnapshotFactory.Capture(roster);
        var solved = book.Evaluate(demand.Id, snapshot, registry);
        Assert.True(solved.Result!.IsFeasible, solved.Result.Reason);
        Assert.True(solved.Result.Plan!.IsPartitioned);

        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations);
        var admitted = admission.TryAdmit(demand.Id);

        Assert.True(admitted.Success, admitted.Reason);
        Assert.Equal(1, admitted.Manifest!.MasterInitialLiquidAmounts["酒"]);
        Assert.Equal(1, admitted.Manifest.TransferLiquids["酒"]);
        Assert.Equal(new[] { jar.Id!.Value }, admitted.Manifest.LockedJarIds);
        Assert.Equal(1, reservations.ReservedLiquidAmount("酒"));
    }

    [Fact]
    public void OnlyOnePartitionedOrderCanReserveTheSingleSlaveRole()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxJars: 8, maxLiquids: 8, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐", "方罐" },
            maxJars: 8, maxLiquids: 8, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        master.AddJar(new Jar("方罐", vocabulary: Words));
        master.AddLiquid("酒", 1);
        master.AddLiquid("蜜", 1);

        var ids = new SequentialIdGenerator();
        var first = new OrderDemand(new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
            Constants.OrderType.B, Words), 0, ids);
        var second = new OrderDemand(new JarOrderSpecification(
            new[] { new Requirement { Shape = "方罐", Liquid = "蜜" } },
            Constants.OrderType.B, Words), 0, ids);
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot,
            DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(first, DemandAreaKind.Active);
        board.AddDemand(second, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        var snapshot = WorldSnapshotFactory.Capture(roster);
        Assert.True(book.Evaluate(first.Id, snapshot, registry).Result!.IsFeasible);
        Assert.True(book.Evaluate(second.Id, snapshot, registry).Result!.IsFeasible);

        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations);

        var firstAdmission = admission.TryAdmit(first.Id);
        var secondAdmission = admission.TryAdmit(second.Id);

        Assert.True(firstAdmission.Success, firstAdmission.Reason);
        Assert.False(secondAdmission.Success);
        Assert.Contains("从工作台正由订单", secondAdmission.Reason);
        Assert.Equal(first.Id, reservations.Find(first.Id)!.DemandId);
        Assert.Null(reservations.Find(second.Id));
        Assert.True(admission.Release(first.Id));

        var secondAfterRelease = admission.TryAdmit(second.Id);
        Assert.True(secondAfterRelease.Success, secondAfterRelease.Reason);
    }

    [Fact]
    public void AdmissionRejectsSolveResultAfterWorldVersionChanges()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var demand = Demand("圆罐", null, new SequentialIdGenerator());
        var setup = Prepare(master, demand);

        Assert.True(setup.Book.Find(demand.Id)!.State == WorldOrderSolveState.Current);
        master.AddLiquid("蜜", 1);

        var result = setup.Admission.TryAdmit(demand.Id);

        Assert.False(result.Success);
        Assert.Contains("版本", result.Reason);
        Assert.Equal(WorldOrderSolveState.Dirty, setup.Book.Find(demand.Id)!.State);
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void RemovedDemandCannotBeAdmitted()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var demand = Demand("圆罐", null, new SequentialIdGenerator());
        var setup = Prepare(master, demand);
        setup.Board.RemoveDemand(demand.Id);

        var result = setup.Admission.TryAdmit(demand.Id);

        Assert.False(result.Success);
        Assert.Contains("不在需求板", result.Reason);
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void ShipInventoryAdmissionCreatesReservedOrderScopeAndReturnsUnusedLiquidOnRelease()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        // 旧池只作为当前求解快照的兼容投影；新准入实际扣除自机液库。
        master.AddLiquid("酒", 1);
        var demand = Demand("圆罐", "酒", new SequentialIdGenerator());
        var setup = Prepare(master, demand);
        var ship = new ShipLiquidInventory(4,
            new Dictionary<string, int> { ["酒"] = 2 });
        var admission = new WorldOrderExecutionAdmission(
            setup.Board, setup.Book, setup.Roster, setup.Reservations, ship);

        var admitted = admission.TryAdmit(demand.Id);

        Assert.True(admitted.Success, admitted.Reason);
        Assert.Equal(1, ship.GetAmount("酒"));
        var scopes = admission.GetLiquidScopes(demand.Id);
        Assert.True(scopes.HasValue);
        Assert.Equal(1, scopes.Value.Master.GetAmount("酒"));
        Assert.Equal(1, scopes.Value.Master.Staging.ReservedCapacity);
        Assert.True(admission.Release(demand.Id));
        Assert.Equal(2, ship.GetAmount("酒"));
        Assert.Equal(0, scopes.Value.Master.Staging.UsedCapacity);
        Assert.Equal(0, scopes.Value.Master.Staging.ReservedCapacity);
    }

    [Fact]
    public void ShipInventoryAdmissionRejectsWhenOrderPeakCannotFitSharedWorkshopCapacity()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxLiquids: 1, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        master.AddJar(new Jar("方罐", vocabulary: Words));
        master.AddLiquid("酒", 1);
        master.AddLiquid("蜜", 1);
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[]
                {
                    new Requirement { Shape = "圆罐", Liquid = "酒" },
                    new Requirement { Shape = "方罐", Liquid = "蜜" }
                }, Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());
        var setup = Prepare(master, demand);
        var ship = new ShipLiquidInventory(4,
            new Dictionary<string, int> { ["酒"] = 1, ["蜜"] = 1 });
        var admission = new WorldOrderExecutionAdmission(
            setup.Board, setup.Book, setup.Roster, setup.Reservations, ship);

        var admitted = admission.TryAdmit(demand.Id);

        Assert.False(admitted.Success);
        Assert.Contains("容量", admitted.Reason);
        Assert.Equal(1, ship.GetAmount("酒"));
        Assert.Equal(0, setup.Reservations.Count);
    }


    [Fact]
    public void ConcurrentShipLiquidAdmissionsReserveAggregateStagingWithoutDoubleSubtractingInventory()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        master.AddJar(new Jar("方罐", vocabulary: Words));
        master.AddLiquid("酒", 2);
        var ids = new SequentialIdGenerator();
        var first = Demand("圆罐", "酒", ids);
        var second = Demand("方罐", "酒", ids);
        var setup = Prepare(master, first, second);
        var ship = new ShipLiquidInventory(4,
            new Dictionary<string, int> { ["酒"] = 2 });
        var admission = new WorldOrderExecutionAdmission(
            setup.Board, setup.Book, setup.Roster, setup.Reservations, ship);

        var firstAdmitted = admission.TryAdmit(first.Id);
        var secondAdmitted = admission.TryAdmit(second.Id);

        Assert.True(firstAdmitted.Success, firstAdmitted.Reason);
        Assert.True(secondAdmitted.Success, secondAdmitted.Reason);
        Assert.Equal(0, ship.GetAmount("酒"));
        Assert.Equal(2, admission.GetLiquidScopes(first.Id)!.Value.Master.Staging.UsedCapacity);
        Assert.Equal(2, admission.GetLiquidScopes(first.Id)!.Value.Master.Staging.ReservedCapacity);

        Assert.True(admission.Release(first.Id));
        Assert.True(admission.Release(second.Id));
        Assert.Equal(2, ship.GetAmount("酒"));
    }

}

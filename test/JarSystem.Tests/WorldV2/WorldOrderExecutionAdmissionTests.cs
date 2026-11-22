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

    private static void ReevaluateWithShip(
        WorldOrderSolveBook book, WorkshopRoster roster, ShipLiquidInventory ship,
        params int[] demandIds)
    {
        var snapshot = WorldSnapshotFactory.Capture(roster, ship);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        foreach (var demandId in demandIds)
            book.Evaluate(demandId, snapshot, registry);
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
    public void BAdmissionSnapshotRestoresBothOrderScopesAndReservationsWithoutWithdrawingShipAgain()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 6);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 5);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        var ship = new ShipLiquidInventory(8, new Dictionary<string, int> { ["酒"] = 1 });

        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot, DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        var solveSnapshot = WorldSnapshotFactory.Capture(roster, ship);
        var solved = book.Evaluate(demand.Id, solveSnapshot, registry);
        Assert.True(solved.Result!.IsFeasible, solved.Result.Reason);
        Assert.True(solved.Result.Plan!.IsPartitioned);

        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations, ship);
        var admitted = admission.TryAdmit(demand.Id);
        Assert.True(admitted.Success, admitted.Reason);
        Assert.Equal(0, ship.GetAmount("酒"));
        var beforeRestore = admission.GetLiquidScopes(demand.Id);
        Assert.True(beforeRestore.HasValue);
        Assert.Equal(1, beforeRestore.Value.Master.GetAmount("酒"));
        Assert.NotNull(beforeRestore.Value.Slave);

        var admissionSnapshot = admission.Capture();
        var reservationSnapshot = reservations.Capture();
        Assert.Single(admissionSnapshot.ActiveOrders);
        Assert.True(admissionSnapshot.ActiveOrders[0].UsesSlave);
        Assert.Equal(admitted.Manifest!.MasterStagingPeakCapacity,
            admissionSnapshot.MasterStaging!.ReservedCapacityByOrder[demand.Id]);
        Assert.Equal(admitted.Manifest.SlaveStagingPeakCapacity,
            admissionSnapshot.SlaveStaging!.ReservedCapacityByOrder[demand.Id]);

        var restoredReservations = WorldResourceReservationBook.Restore(reservationSnapshot);
        var restoredAdmission = new WorldOrderExecutionAdmission(
            board, book, roster, restoredReservations, ship);
        restoredAdmission.Restore(admissionSnapshot);

        Assert.Equal(0, ship.GetAmount("酒"));
        Assert.Equal(1, restoredReservations.Count);
        Assert.Equal(demand.Id, restoredReservations.Find(demand.Id)!.DemandId);
        var restoredScopes = restoredAdmission.GetLiquidScopes(demand.Id);
        Assert.True(restoredScopes.HasValue);
        Assert.Equal(1, restoredScopes.Value.Master.GetAmount("酒"));
        Assert.Equal(0, restoredScopes.Value.Slave!.GetAmount("酒"));

        Assert.True(OrderLiquidTransfer.TryExecute(demand.Id,
            restoredScopes.Value.Master.Staging,
            restoredScopes.Value.Slave.Staging,
            new Dictionary<string, int> { ["酒"] = 1 }, out var transferReason), transferReason);
        Assert.Equal(0, restoredScopes.Value.Master.GetAmount("酒"));
        Assert.Equal(1, restoredScopes.Value.Slave.GetAmount("酒"));
    }

    [Fact]
    public void NewAdmissionRefreshesEmptyStagingWhenWorkshopBanRulesChange()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            vocabulary: Words, liquidStagingCapacity: 4);
        master.AddJar(new Jar("圆罐", "酒", vocabulary: Words));
        var ids = new SequentialIdGenerator();
        var firstDemand = Demand("圆罐", "酒", ids);
        var setup = Prepare(master, firstDemand);
        var ship = new ShipLiquidInventory(4);
        ReevaluateWithShip(setup.Book, setup.Roster, ship, firstDemand.Id);
        var admission = new WorldOrderExecutionAdmission(
            setup.Board, setup.Book, setup.Roster, setup.Reservations, ship);

        var first = admission.TryAdmit(firstDemand.Id);
        Assert.True(first.Success, first.Reason);
        Assert.True(admission.Release(firstDemand.Id));

        master.ReplaceCapability(new WorkshopCapability(
            master.BaseMarks, master.AdditionalMarks, new[] { "酒" }));
        var secondDemand = Demand("圆罐", "酒", ids);
        setup.Board.AddDemand(secondDemand, DemandAreaKind.Active);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        setup.Book.Evaluate(secondDemand.Id,
            WorldSnapshotFactory.Capture(setup.Roster, ship), registry);

        var second = admission.TryAdmit(secondDemand.Id);

        // The Jar still contains 酒. A newly captured Master ban must reject operating it;
        // an empty staging account does not make the prefilled Jar exempt from the workshop rule.
        Assert.False(second.Success);
        Assert.False(admission.GetLiquidScopes(secondDemand.Id).HasValue);
    }

    [Fact]
    public void AdmissionRestoreRejectsMismatchBetweenActiveScopesAndReservationOwnership()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            vocabulary: Words, liquidStagingCapacity: 4);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            vocabulary: Words, liquidStagingCapacity: 4);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 });
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot, DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        book.Evaluate(demand.Id, WorldSnapshotFactory.Capture(roster, ship), registry);
        var sourceReservations = new WorldResourceReservationBook();
        var sourceAdmission = new WorldOrderExecutionAdmission(
            board, book, roster, sourceReservations, ship);
        Assert.True(sourceAdmission.TryAdmit(demand.Id).Success);
        var captured = sourceAdmission.Capture();

        var forged = new WorldOrderExecutionAdmissionSnapshot(
            captured.MasterStaging, captured.SlaveStaging,
            new[] { new WorldOrderExecutionScopeSnapshot(demand.Id, UsesSlave: false) });
        var restored = new WorldOrderExecutionAdmission(
            board, book, roster,
            WorldResourceReservationBook.Restore(sourceReservations.Capture()), ship);

        Assert.Throws<InvalidOperationException>(() => restored.Restore(forged));
    }

    [Fact]
    public void AdmissionRebindsSolveResultAfterResourceOnlyWorldVersionChanges()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var demand = Demand("圆罐", null, new SequentialIdGenerator());
        var setup = Prepare(master, demand);

        Assert.True(setup.Book.Find(demand.Id)!.State == WorldOrderSolveState.Current);
        master.AddLiquid("蜜", 1);

        var result = setup.Admission.TryAdmit(demand.Id);

        Assert.True(result.Success, result.Reason);
        Assert.NotNull(result.Manifest);
        Assert.Equal(WorldOrderSolveState.Current, setup.Book.Find(demand.Id)!.State);
        Assert.Equal(1, setup.Reservations.Count);
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
        ReevaluateWithShip(setup.Book, setup.Roster, ship, demand.Id);
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
    public void AdmissionCannotStartWhenSolverRejectsAnOrderPeakAboveSharedWorkshopCapacity()
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
        ReevaluateWithShip(setup.Book, setup.Roster, ship, demand.Id);
        var admission = new WorldOrderExecutionAdmission(
            setup.Board, setup.Book, setup.Roster, setup.Reservations, ship);

        var admitted = admission.TryAdmit(demand.Id);

        Assert.False(admitted.Success);
        Assert.False(setup.Book.Find(demand.Id)!.Result!.IsFeasible,
            "求解器应先按工作台合计暂存容量排除该执行路径");
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
        ReevaluateWithShip(setup.Book, setup.Roster, ship, first.Id, second.Id);
        var admission = new WorldOrderExecutionAdmission(
            setup.Board, setup.Book, setup.Roster, setup.Reservations, ship);

        var firstAdmitted = admission.TryAdmit(first.Id);
        ReevaluateWithShip(setup.Book, setup.Roster, ship, second.Id);
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


    [Fact]
    public void NewOrderStagingUsesIndependentAggregateCapacityNotLegacyPerLiquidLimit()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxJars: 4, maxLiquids: 1, vocabulary: Words, liquidStagingCapacity: 2);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        master.AddJar(new Jar("方罐", vocabulary: Words));
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
        ReevaluateWithShip(setup.Book, setup.Roster, ship, demand.Id);
        var admission = new WorldOrderExecutionAdmission(
            setup.Board, setup.Book, setup.Roster, setup.Reservations, ship);

        var admitted = admission.TryAdmit(demand.Id);

        Assert.True(admitted.Success, admitted.Reason);
        var scope = admission.GetLiquidScopes(demand.Id)!.Value.Master;
        Assert.Equal(1, master.MaxLiquids);
        Assert.Equal(2, master.LiquidStagingCapacity);
        Assert.Equal(2, scope.Staging.Capacity);
        Assert.Equal(2, scope.Staging.UsedCapacity);
        Assert.Equal(1, scope.GetAmount("酒"));
        Assert.Equal(1, scope.GetAmount("蜜"));
    }

    [Fact]
    public void ReservationBookOwnsOneLeaseAndLegacyReservationIsOnlyItsProjection()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand("圆罐", "酒", new SequentialIdGenerator());
        var setup = Prepare(master, demand);

        var admitted = setup.Admission.TryAdmit(demand.Id);

        Assert.True(admitted.Success, admitted.Reason);
        var lease = Assert.IsType<OrderResourceLease>(
            setup.Reservations.FindLease(demand.Id));
        Assert.Equal(demand.Id, lease.DemandId);
        Assert.Equal(admitted.Manifest!.WorldVersion, lease.WorldVersion);
        Assert.Equal(new[] { jar.Id!.Value }, lease.LockedJarIds);
        Assert.Equal(1, lease.InitialLiquidSupply["酒"]);
        Assert.False(lease.UsesSlave);
        Assert.Equal(OrderResourceLeaseStage.Admitted, lease.Stage);
        Assert.Throws<System.InvalidOperationException>(
            () => setup.Reservations.MarkTransferPending(demand.Id));
        setup.Reservations.MarkMasterExecuting(demand.Id);
        Assert.Equal(OrderResourceLeaseStage.MasterExecuting, lease.Stage);
        var projection = lease.Reservation;
        Assert.Same(projection, lease.Reservation);
        Assert.Equal(projection.Stage, setup.Reservations.Find(demand.Id)!.Stage);
        Assert.True(setup.Admission.Release(demand.Id));
        Assert.Null(setup.Reservations.FindLease(demand.Id));
    }


    [Fact]
    public void LeaseWireRoundTripPreservesSettlementReceiptAndRebuildsReservationIndexes()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand("圆罐", "酒", new SequentialIdGenerator());
        var setup = Prepare(master, demand);
        var admitted = setup.Admission.TryAdmit(demand.Id);
        Assert.True(admitted.Success, admitted.Reason);

        var lease = setup.Reservations.FindLease(demand.Id)!;
        lease.RecordSettlement(new LiquidBatchReceipt(
            new Dictionary<string, int> { ["酒"] = 2 },
            new Dictionary<string, int> { ["酒"] = 1 },
            new Dictionary<string, int> { ["酒"] = 1 }));

        var json = WorldV2CheckpointJsonCodec.Encode(
            setup.Reservations.CaptureWire(), configurationFingerprint: null);
        var wire = WorldV2CheckpointJsonCodec.Decode<WorldResourceReservationBookWireDto>(json);
        var restored = WorldResourceReservationBook.RestoreWire(wire.Payload);
        var restoredLease = Assert.IsType<OrderResourceLease>(
            restored.FindLease(demand.Id));

        Assert.Equal(lease.SettlementOperationId, restoredLease.SettlementOperationId);
        Assert.Equal(OrderResourceSettlementStage.ResourcesReturned, restoredLease.SettlementStage);
        Assert.Equal(2, restoredLease.SettlementReceipt!.Requested["酒"]);
        Assert.Equal(1, restoredLease.SettlementReceipt.Accepted["酒"]);
        Assert.Equal(1, restoredLease.SettlementReceipt.Discarded["酒"]);
        Assert.Equal(demand.Id, restored.JarReservationOwner(jar.Id!.Value));
    }

    [Fact]
    public void MasterPrefillIsIncludedInSolveManifestAndFundsOrderBeforeShipInventory()
    {
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" }, vocabulary: Words);
        var target = new Workshop("目标主台", new List<string> { "圆罐" },
            vocabulary: Words, liquidStagingCapacity: 4);
        var jar = new Jar("圆罐", vocabulary: Words);
        target.AddJar(jar);
        var roster = new WorkshopRoster(new[] { oldMaster, target }, oldMaster);
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot, DemandAreaFulfillmentPolicy.OneShot);
        var demand = Demand("圆罐", "酒", new SequentialIdGenerator());
        board.AddDemand(demand, DemandAreaKind.Active);
        var book = new WorldOrderSolveBook(board);
        var reservations = new WorldResourceReservationBook();
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 });
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations, ship);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });

        var prefill = admission.PrefillMasterWorkshop(target, new[] { "酒" });
        Assert.Equal(1, prefill.Accepted["酒"]);
        Assert.Equal(0, ship.GetAmount("酒"));
        roster.CommitMasterReplacement(oldMaster, target);

        var solveSnapshot = admission.CaptureWorldSnapshot();
        Assert.Equal(1, solveSnapshot.MasterPrefillLiquidBalances["酒"]);
        var solved = book.Evaluate(demand.Id, solveSnapshot, registry);
        Assert.True(solved.Result!.IsFeasible, solved.Result.Reason);

        var admitted = admission.TryAdmit(demand.Id);
        Assert.True(admitted.Success, admitted.Reason);
        Assert.Equal(0, ship.GetAmount("酒"));
        Assert.Empty(admission.Capture().MasterStaging!.PrefillBalances);
        var lease = reservations.FindLease(demand.Id)!;
        var scopes = lease.LiquidScopes
            ?? throw new System.InvalidOperationException("准入后租约缺少主台液体范围。");
        Assert.Equal(1, scopes.Master.GetAmount("酒"));
        Assert.Null(scopes.Slave);
        Assert.Same(scopes.Master, admission.GetLiquidScopes(demand.Id)!.Value.Master);
    }


    [Fact]
    public void OrderConsumesOnlyItsRequiredShareOfMasterPrefillBeforeUsingShipInventory()
    {
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" }, vocabulary: Words);
        var target = new Workshop("目标主台", new List<string> { "圆罐" },
            vocabulary: Words, liquidStagingCapacity: 3);
        target.AddJar(new Jar("圆罐", vocabulary: Words));
        var roster = new WorkshopRoster(new[] { oldMaster, target }, oldMaster);
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot, DemandAreaFulfillmentPolicy.OneShot);
        var demand = Demand("圆罐", "酒", new SequentialIdGenerator());
        board.AddDemand(demand, DemandAreaKind.Active);
        var book = new WorldOrderSolveBook(board);
        var reservations = new WorldResourceReservationBook();
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 4 });
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations, ship);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });

        var prefill = admission.PrefillMasterWorkshop(target, new[] { "酒" });
        Assert.Equal(3, prefill.Accepted["酒"]);
        Assert.Equal(1, ship.GetAmount("酒"));
        roster.CommitMasterReplacement(oldMaster, target);

        var solveSnapshot = admission.CaptureWorldSnapshot();
        Assert.Equal(3, solveSnapshot.MasterPrefillLiquidBalances["酒"]);
        var solved = book.Evaluate(demand.Id, solveSnapshot, registry);
        Assert.True(solved.Result!.IsFeasible, solved.Result.Reason);

        var admitted = admission.TryAdmit(demand.Id);

        Assert.True(admitted.Success, admitted.Reason);
        Assert.Equal(1, ship.GetAmount("酒"));
        Assert.Equal(2, admission.GetPrefillBalances(target)["酒"]);
        var scopes = admission.GetLiquidScopes(demand.Id)
            ?? throw new System.InvalidOperationException("准入后租约缺少主台液体范围。");
        Assert.Equal(1, scopes.Master.GetAmount("酒"));
        Assert.Null(scopes.Slave);
    }

}

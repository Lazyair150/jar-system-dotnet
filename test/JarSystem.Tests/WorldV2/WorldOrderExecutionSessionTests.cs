using System;
using System.Collections.Generic;
using System.Globalization;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using JarSystem.Core.Solver;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderExecutionSessionTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static (OrderDemandBoard Board, WorldOrderSolveBook Book,
        WorkshopRoster Roster, WorldOrderExecutionAdmission Admission,
        WorldResourceReservationBook Reservations) Prepare(
        Workshop master, Workshop? slave, OrderDemand demand,
        DemandAreaFulfillmentPolicy passiveFulfillment = DemandAreaFulfillmentPolicy.OneShot,
        DemandAreaFulfillmentPolicy activeFulfillment = DemandAreaFulfillmentPolicy.OneShot)
    {
        var board = new OrderDemandBoard(passiveFulfillment, activeFulfillment);
        board.AddDemand(demand, DemandAreaKind.Active);
        IEnumerable<Workshop> workshops = slave == null
            ? new Workshop[] { master }
            : new Workshop[] { master, slave };
        var roster = new WorkshopRoster(workshops, master, slave);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        book.Evaluate(demand.Id, WorldSnapshotFactory.Capture(roster), registry);
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations);
        return (board, book, roster, admission, reservations);
    }

    private static ResourceWorldRuntime CreateRuntime(
        OrderDemandBoard board, WorkshopRoster roster,
        ResourceLedger? resourceLedger = null, Func<int>? resourceLedgerTick = null)
    {
        var resources = new ResourceArea(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        return new ResourceWorldRuntime(resources, roster: roster, demandBoard: board,
            resourceLedger: resourceLedger, resourceLedgerTick: resourceLedgerTick);
    }

    private sealed class ThrowingInterruptionPort : IOrderInterruptionPort
    {
        public (bool Success, string? Error) InterruptForWorkshopReplacement() =>
            throw new InvalidOperationException("模拟中断结果不确定");
    }

    private sealed class ThrowAfterSourceJarRemovalPort : IOrderJarTransferPort
    {
        public bool Move(Workshop source, Workshop destination, Jar jar)
        {
            if (!ReferenceEquals(source.TransferOut(jar), jar)) return false;
            throw new InvalidOperationException("Injected failure after source Jar removal");
        }
    }

    private sealed class ThrowOnSecondJarMovePort : IOrderJarTransferPort
    {
        private int _moveCount;

        public bool Move(Workshop source, Workshop destination, Jar jar)
        {
            if (!ReferenceEquals(source.TransferOut(jar), jar)) return false;
            _moveCount++;
            if (_moveCount == 2)
                throw new InvalidOperationException("Injected failure on second Jar move");
            return destination.TransferIn(jar);
        }
    }
    private sealed class ThrowAtOrderTransferBoundary : IOrderTransferFaultPort
    {
        private readonly string _boundary;
        private readonly string? _compensationBoundary;
        public ThrowAtOrderTransferBoundary(string boundary, string? compensationBoundary = null)
        {
            _boundary = boundary;
            _compensationBoundary = compensationBoundary;
        }
        private void Hit(string boundary)
        {
            if (_boundary == boundary)
                throw new InvalidOperationException("Injected B-class transfer failure: " + boundary);
        }
        public void AfterJarMoves() => Hit("jar-moves");
        public void AfterLiquidTransfer() => Hit("liquid-transfer");
        public void BeforeLedgerPublication() => Hit("ledger-publication");
        public void BeforeCompensation()
        {
            if (_compensationBoundary == "compensation")
                throw new InvalidOperationException("Injected B-class compensation failure");
        }
    }

    private sealed class ThrowAtSlaveRollbackBoundary : ISlaveRollbackFaultPort
    {
        private readonly bool _failCompensation;
        public ThrowAtSlaveRollbackBoundary(bool failCompensation = false) =>
            _failCompensation = failCompensation;
        public void AfterJarMoves() { }
        public void AfterLiquidMoves() =>
            throw new InvalidOperationException("Injected SlaveRollback failure after liquid movement");
        public void BeforeLedgerPublication() { }
        public void BeforeCompensation()
        {
            if (_failCompensation)
                throw new InvalidOperationException("Injected SlaveRollback compensation failure");
        }
        public void AfterLedgerPublication() { }
    }

    private sealed class ThrowAtSettlementBoundary : IOrderSettlementFaultPort
    {
        private readonly string _boundary;
        private readonly bool _failCompensation;
        public ThrowAtSettlementBoundary(string boundary, bool failCompensation = false)
        {
            _boundary = boundary;
            _failCompensation = failCompensation;
        }
        private void Hit(string boundary)
        {
            if (_boundary == boundary)
                throw new InvalidOperationException("Injected settlement failure: " + boundary);
        }
        public void AfterMasterDrain() => Hit("master-drain");
        public void AfterSlaveDrain() => Hit("slave-drain");
        public void AfterShipReturnBeforeReceipt() => Hit("ship-return-before-receipt");
        public void BeforeCompensation()
        {
            if (_failCompensation)
                throw new InvalidOperationException("Injected settlement compensation failure");
        }
        public void AfterLedgerBatchRecorded() => Hit("ledger-batch");
        public void AfterLedgerStageMarked() => Hit("stage-mark");
        public void BeforeReservationRelease() => Hit("reservation-release");
    }

    private sealed record SettlementScenario(
        OrderDemand Demand,
        OrderDemandBoard Board,
        WorldOrderSolveBook Book,
        WorkshopRoster Roster,
        WorldOrderExecutionAdmission Admission,
        WorldResourceReservationBook Reservations,
        ShipLiquidInventory Ship,
        ResourceLedger Ledger,
        WorldOrderExecutionSession Session);

    private static SettlementScenario PrepareSettlementScenario(
        bool withOwnerIdentities = false,
        IOrderSettlementFaultPort? settlementFaultPort = null)
    {
        var worldId = withOwnerIdentities ? new WorldInstanceId("settlement-world") : default;
        OwnerIdentity? masterOwner = withOwnerIdentities
            ? new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "master-owned") : null;
        OwnerIdentity? slaveOwner = withOwnerIdentities
            ? new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "slave-owned") : null;
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: withOwnerIdentities ? "master-owned" : null,
            ownerIdentity: masterOwner);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: withOwnerIdentities ? "slave-owned" : null,
            ownerIdentity: slaveOwner);
        Assert.True(master.AddJar(new Jar("圆罐", id: 170101, vocabulary: Words)));
        using var suppressMasterPourIn = master.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：结算故障边界保留从台液体"
        });

        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot, DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var book = new WorldOrderSolveBook(board);
        OwnerIdentity? shipOwner = withOwnerIdentities
            ? new OwnerIdentity(worldId, ResourceOwnerKind.Ship, "ship-owned") : null;
        var ship = new ShipLiquidInventory(
            withOwnerIdentities ? 1 : 4,
            new Dictionary<string, int> { ["酒"] = 1 }, shipOwner);
        ReevaluateWithShip(book, roster, ship, demand.Id);
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations, ship);
        var ledger = new ResourceLedger();
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, board, book, admission, roster, 1_000, ledger, () => 17,
            jarTransferPort: null,
            transferFaultPort: null,
            slaveRollbackFaultPort: null,
            settlementFaultPort: settlementFaultPort);
        Assert.True(started.Success, started.Reason);
        started.Session!.AdvanceLogicalTime(1_000);
        Assert.Equal(WorldOrderExecutionStage.SlaveSteps, started.Session.Stage);
        var scopes = admission.GetLiquidScopes(demand.Id)!.Value;
        Assert.Equal(1, scopes.Slave!.GetAmount("酒"));
        return new SettlementScenario(
            demand, board, book, roster, admission, reservations, ship, ledger, started.Session!);
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

    private sealed class FixedPlanSolver : IWorldOrderSpecificationSolver
    {
        private readonly WorldOrderSolvePlan _plan;
        public FixedPlanSolver(WorldOrderSolvePlan plan) => _plan = plan;
        public Type SpecificationType => typeof(JarOrderSpecification);
        public WorldOrderSolveResult Solve(OrderDemand demand, WorldSnapshot snapshot) =>
            new(demand.Id, snapshot.Version, WorldOrderSolveStatus.Feasible, plan: _plan);
    }

    private sealed class NoopInterruptionPort : IOrderInterruptionPort
    {
        public (bool Success, string? Error) InterruptForWorkshopReplacement() => (true, null);
    }

    private static OrderDemand Demand(string type, string shape, string? liquid, long cooldown = 0) =>
        new(new JarOrderSpecification(
            new[] { new Requirement { Shape = shape, Liquid = liquid } },
            type, Words), cooldown, new SequentialIdGenerator());

    [Fact]
    public void AOrderConsumesLogicalTimeThenExecutesAndDeliversMatchedJar()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.A, "圆罐", "酒");
        var setup = Prepare(master, null, demand);

        var runtime = CreateRuntime(setup.Board, setup.Roster);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            runtime.TryStartExecutionSession(demand.Id, setup.Book, setup.Admission, 0));
        Assert.False(runtime.IsFaulted, "调用参数错误不能把世界标记为故障");

        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);

        Assert.True(started.Success, started.Reason);
        var session = started.Session!;
        Assert.False(session.IsTerminal);
        runtime.AdvanceWorldTime(999);
        Assert.Null(jar.Liquid);
        Assert.False(session.IsTerminal);

        runtime.AdvanceWorldTime(1);

        Assert.True(session.Succeeded, session.Reason);
        Assert.Empty(master.Jars);
        Assert.Equal(0, master.LiquidStorage["酒"]);
        Assert.Null(setup.Board.Find(demand.Id));
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void ContinuousSuccessStartsCooldownAfterTheCompletingWorldStep()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.A, "圆罐", "酒", cooldown: 2_500);
        var setup = Prepare(master, null, demand,
            activeFulfillment: DemandAreaFulfillmentPolicy.Continuous);
        var runtime = CreateRuntime(setup.Board, setup.Roster);

        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;

        runtime.AdvanceWorldTime(999);
        Assert.False(session.IsTerminal);
        Assert.Equal(0, demand.CooldownRemainingLogicalMilli);

        runtime.AdvanceWorldTime(1);
        Assert.True(session.Succeeded, session.Reason);
        Assert.Same(demand, setup.Board.Find(demand.Id));
        Assert.Equal(2_500, demand.CooldownRemainingLogicalMilli);
        Assert.Equal(WorldOrderSolveState.NotReady, setup.Book.Find(demand.Id)!.State);

        runtime.AdvanceWorldTime(1_000);
        Assert.Equal(1_500, demand.CooldownRemainingLogicalMilli);
        Assert.Equal(WorldOrderSolveState.NotReady, setup.Book.Find(demand.Id)!.State);
    }

    [Fact]
    public void DirectMatchIsAppliedAsAZeroTimeStepBeforeAOrderIsDelivered()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var matchedJar = new Jar("圆罐", "酒", vocabulary: Words);
        master.AddJar(matchedJar);
        var demand = Demand(Constants.OrderType.A, "圆罐", "酒");
        var setup = Prepare(master, null, demand);
        var runtime = CreateRuntime(setup.Board, setup.Roster);

        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);

        Assert.True(started.Success, started.Reason);
        Assert.True(started.Session!.Succeeded, started.Session.Reason);
        Assert.Equal(1, started.Session.CompletedMasterSteps);
        Assert.Empty(master.Jars);
        Assert.False(matchedJar.IsMarked);
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void SessionCleanupDoesNotClearMarksOnUnrelatedMasterJars()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var targetJar = new Jar("圆罐", vocabulary: Words);
        var unrelatedJar = new Jar("方罐", vocabulary: Words);
        master.AddJar(targetJar);
        master.AddJar(unrelatedJar);
        master.AddLiquid("酒", 1);
        Assert.True(master.MarkJar(unrelatedJar).Success);

        var demand = Demand(Constants.OrderType.A, "圆罐", "酒");
        var setup = Prepare(master, null, demand);
        var runtime = CreateRuntime(setup.Board, setup.Roster);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);

        runtime.AdvanceWorldTime(1_000);

        Assert.True(started.Session!.Succeeded, started.Session.Reason);
        Assert.True(unrelatedJar.IsMarked);
        Assert.Contains(master.Jars, jar => ReferenceEquals(jar, unrelatedJar));
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void ResourceWorldRuntimeAdvancesRegisteredSessionUsingItsClockDelta()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.A, "圆罐", "酒");
        var setup = Prepare(master, null, demand);
        var runtime = CreateRuntime(setup.Board, setup.Roster);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);



        var step = runtime.AdvanceWorldTime(1_000);

        Assert.Equal(1_000, step.LogicalTimeDeltaMilli);
        var completedSession = started.Session!;
        Assert.True(completedSession.Succeeded, completedSession.Reason);
        Assert.Empty(runtime.ActiveExecutionSessions);
        Assert.Empty(master.Jars);
    }

    [Fact]
    public void UncertainReplacementFreezesRegisteredSessionsAndPreventsNewStarts()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var spare = new Workshop("备用台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.A, "圆罐", "酒", cooldown: 2_500);
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot,
            DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, spare }, master);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        book.Evaluate(demand.Id, WorldSnapshotFactory.Capture(roster), registry);
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations);
        var resources = new ResourceArea(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        var runtime = new ResourceWorldRuntime(resources, roster: roster, demandBoard: board);
        var started = runtime.TryStartExecutionSession(demand.Id, book, admission, 1_000);
        Assert.True(started.Success, started.Reason);

        var replacement = runtime.BeginReplacement(
            WorkshopRole.Master, spare, new ThrowingInterruptionPort(), phaseDurationMilli: 1_000);

        Assert.False(replacement.Success);
        Assert.True(runtime.ActiveReplacement!.InterruptionOutcomeUncertain);
        Assert.True(runtime.IsFaulted);
        Assert.Contains("结果可能不确定", runtime.FaultReason);

        var worldTimeBeforeRejectedAdvance = runtime.Clock.WorldTimeMilli;
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceWorldTime(1_000));
        Assert.Equal(worldTimeBeforeRejectedAdvance, runtime.Clock.WorldTimeMilli);

        Assert.Equal(0, started.Session!.CompletedMasterSteps);
        Assert.Null(jar.Liquid);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Equal(1, reservations.Count);
        var secondReplacement = runtime.BeginReplacement(
            WorkshopRole.Master, spare, new NoopInterruptionPort(), phaseDurationMilli: 1_000);
        Assert.False(secondReplacement.Success);
        Assert.Contains("已故障", secondReplacement.Error);
        var lateStart = runtime.TryStartExecutionSession(demand.Id, book, admission, 1_000);
        Assert.False(lateStart.Success);
        Assert.Contains("已故障", lateStart.Reason);
    }

    [Fact]
    public void RuntimeAdvancesMultipleSessionsWhenTheirReservedResourcesDoNotOverlap()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxJars: 8, maxLiquids: 8, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        master.AddJar(new Jar("方罐", vocabulary: Words));
        master.AddLiquid("酒", 1);
        master.AddLiquid("蜜", 1);
        var demandIds = new SequentialIdGenerator();
        var firstDemand = new OrderDemand(new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
            Constants.OrderType.A, Words), 0, demandIds);
        var setup = Prepare(master, null, firstDemand);

        var secondDemand = new OrderDemand(new JarOrderSpecification(
            new[] { new Requirement { Shape = "方罐", Liquid = "蜜" } },
            Constants.OrderType.A, Words), 0, demandIds);
        setup.Board.AddDemand(secondDemand, DemandAreaKind.Active);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        setup.Book.Evaluate(secondDemand.Id, WorldSnapshotFactory.Capture(setup.Roster), registry);

        var runtime = CreateRuntime(setup.Board, setup.Roster);
        var first = runtime.TryStartExecutionSession(
            firstDemand.Id, setup.Book, setup.Admission, 1_000);
        var second = runtime.TryStartExecutionSession(
            secondDemand.Id, setup.Book, setup.Admission, 1_000);

        Assert.True(first.Success, first.Reason);
        Assert.True(second.Success, second.Reason);
        Assert.Equal(2, runtime.ActiveExecutionSessions.Count);
        Assert.Equal(2, setup.Reservations.Count);

        runtime.AdvanceWorldTime(1_000);

        Assert.True(first.Session!.Succeeded, first.Session.Reason);
        Assert.True(second.Session!.Succeeded, second.Session.Reason);
        Assert.Empty(runtime.ActiveExecutionSessions);
        Assert.Empty(master.Jars);
        Assert.Null(setup.Board.Find(firstDemand.Id));
        Assert.Null(setup.Board.Find(secondDemand.Id));
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void RuntimeReplacementInterruptsItsRegisteredSessionsBeforeStartingMigration()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var spare = new Workshop("备用台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.A, "圆罐", "酒", cooldown: 2_500);
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot,
            DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, spare }, master);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        book.Evaluate(demand.Id, WorldSnapshotFactory.Capture(roster), registry);
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations);
        var runtime = CreateRuntime(board, roster);
        var started = runtime.TryStartExecutionSession(demand.Id, book, admission, 1_000);
        Assert.True(started.Success, started.Reason);



        var replacement = runtime.BeginReplacement(
            WorkshopRole.Master, spare, new NoopInterruptionPort(), phaseDurationMilli: 1_000);

        Assert.True(replacement.Success, replacement.Error);
        Assert.Equal(WorldOrderExecutionStage.Interrupted, started.Session!.Stage);
        Assert.Empty(runtime.ActiveExecutionSessions);
        Assert.Equal(2_500, demand.CooldownRemainingLogicalMilli);
        Assert.Equal(WorldOrderSolveState.NotReady, book.Find(demand.Id)!.State);
        Assert.Equal(0, reservations.Count);
        Assert.NotNull(runtime.ActiveReplacement);
    }

    [Fact]
    public void FailedStepKeepsEarlierWorldChangesAndReleasesReservation()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.A, "圆罐", "酒", cooldown: 2_500);
        var setup = Prepare(master, null, demand);
        var runtime = CreateRuntime(setup.Board, setup.Roster);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);

        // 模拟执行开始后外部规则/资源变化：预留是占用声明，不是对真实世界的锁。
        // 执行器必须在真正动手时重新检查领域规则。
        master.RemoveLiquid("酒", 1);
        runtime.AdvanceWorldTime(1_000);

        Assert.Equal(WorldOrderExecutionStage.Failed, started.Session!.Stage);
        Assert.Null(jar.Liquid);
        Assert.False(jar.IsMarked);
        Assert.Equal(0, setup.Reservations.Count);
        Assert.Equal(WorldOrderSolveState.Dirty, setup.Book.Find(demand.Id)!.State);
        Assert.Equal(0, demand.CooldownRemainingLogicalMilli);
        Assert.False(runtime.IsFaulted, "可确认结算的普通订单失败不能冻结整个世界");
    }

    [Fact]
    public void BOrderCompletesSlavePathDeliversJarAndReturnsTheEmptySlaveRole()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var deliveredJar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(deliveredJar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var ledger = new ResourceLedger();
        var runtime = CreateRuntime(setup.Board, setup.Roster, ledger, () => 59);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;

        for (var i = 0; i < 12 && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);

        Assert.True(session.Succeeded, session.Reason);
        var deliveredFact = Assert.Single(ledger.For(ResourceCauses.Transfer),
            entry => entry.Kind == ResourceKinds.Jar &&
                entry.ToEndpoint.Kind == ResourceEndpointKind.ExternalSink &&
                entry.ToEndpoint.ExternalIdentity == "order.delivery");
        Assert.Equal(deliveredJar.Id!.Value.ToString(CultureInfo.InvariantCulture),
            deliveredFact.ResourceIdentity);
        Assert.Equal("订单交付", deliveredFact.To);
        Assert.Empty(master.Jars);
        Assert.Empty(slave.Jars);
        Assert.False(slave.HasResources);
        Assert.Null(setup.Board.Find(demand.Id));
        Assert.Equal(0, setup.Reservations.Count);
        Assert.False(deliveredJar.IsMarked, "成功终态必须清理本订单参与 Jar 的 Mark");
    }

    [Fact]
    public void BTransferPreflightFailureMovesNoJarAndLeavesNoPartialSlaveInventory()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var runtime = CreateRuntime(setup.Board, setup.Roster);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);

        // 准入后工作台容量发生变化，转移阶段必须重新全量预检。
        slave.ResizeJarCapacity(0);
        var session = started.Session!;
        for (var i = 0; i < 8 && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);

        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.Empty(slave.Jars);
        Assert.Equal(0, slave.LiquidStorage["酒"]);
        Assert.Contains(master.Jars, item => item.Id == jar.Id);
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void BTransferAllowsMasterPolarityJarToCrossIntoOppositePolaritySlaveWithoutLiquidHandoff()
    {
        var master = new Workshop("Cathode主台", new List<string> { "圆罐" },
            ban: new List<string> { "蜜" }, maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "owned.workshop.master");
        var slave = new Workshop("Anode从台", new List<string> { "圆罐" },
            ban: new List<string> { "酒" }, maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "owned.workshop.slave");
        var jar = new Jar("圆罐", "酒", id: 9881, vocabulary: Words);
        Assert.Equal(JarPolarity.Cathode, jar.Polarity);
        Assert.Equal(WorkshopPolarityMode.Unrestricted, master.Capability.PolarityMode);
        Assert.Equal(WorkshopPolarityMode.Unrestricted, slave.Capability.PolarityMode);
        Assert.True(master.AddJar(jar));

        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot, DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var snapshot = WorldSnapshotFactory.Capture(roster);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        var solved = book.Evaluate(demand.Id, snapshot, registry);
        Assert.True(solved.Result!.IsFeasible, solved.Result.Reason);
        Assert.Empty(solved.Result.Plan!.MasterSteps);
        Assert.Single(solved.Result.Plan.SlaveSteps);
        Assert.Equal(new[] { 9881 }, solved.Result.Plan.TransferJarIds);

        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations);
        var runtime = CreateRuntime(board, roster);
        var started = runtime.TryStartExecutionSession(demand.Id, book, admission, 1_000);
        Assert.True(started.Success, started.Reason);

        // 当前 B 类候选策略优先由从台完成 MATCH；该操作必须发生在 Jar 所有权交接之后。
        // 启动时仍应保持 Jar 在主台，不能提前执行从台步骤。
        Assert.Equal(WorldOrderExecutionStage.Transfer, started.Session!.Stage);
        Assert.Equal(0, started.Session.CompletedMasterSteps);
        Assert.Equal(0, started.Session.CompletedSlaveSteps);
        Assert.Null(started.Session.TransferTransition);
        Assert.Contains(master.Jars, item => item.Id == 9881);
        Assert.Empty(slave.Jars);

        // 这一 tick 先提交 Jar 所有权交接，再由从台执行 MATCH 并完成交付。
        runtime.AdvanceWorldTime(1_000);

        Assert.True(started.Session.Succeeded, started.Session.Reason);
        Assert.Empty(master.Jars);
        Assert.Empty(slave.Jars);
        Assert.Equal("酒", jar.Liquid);
        Assert.False(jar.IsMarked, "订单成功终态清理 Mark，但不改变跨台 Jar 的液体身份");
        var transfer = Assert.IsType<RuleTransition>(started.Session.TransferTransition);
        Assert.Equal("owned.workshop.master->owned.workshop.slave", transfer.Target);
        Assert.Contains(transfer.ResourceEffects, effect =>
            effect.ResourceKind == RuleResourceKind.Jar &&
            effect.Kind == RuleResourceEffectKind.Transfer &&
            effect.ResourceId == "9881");
        Assert.DoesNotContain(transfer.Before.Values.Keys,
            key => key.Contains("Polarity", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, reservations.Count);
    }

    [Fact]
    public void BTransferRestoresDetachedJarWhenMutationPortFailsAfterSourceRemoval()
    {
        var master = new Workshop("Cathode主台", new List<string> { "圆罐" },
            ban: new List<string> { "蜜" }, maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("Anode从台", new List<string> { "圆罐" },
            ban: new List<string> { "酒" }, maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", "酒", id: 9882, vocabulary: Words);
        Assert.True(master.AddJar(jar));

        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var ledger = new ResourceLedger();
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster,
            1_000, ledger, () => 1, new ThrowAfterSourceJarRemovalPort());

        Assert.True(started.Success, started.Reason);
        var session = started.Session!;
        Assert.Equal(WorldOrderExecutionStage.Transfer, session.Stage);
        session.AdvanceLogicalTime(1_000);
        Assert.Contains("Injected failure", session.Reason);

        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.Same(jar, Assert.Single(master.Jars));
        Assert.Empty(slave.Jars);
        Assert.False(jar.IsMarked);
        Assert.Equal(0, setup.Reservations.Count);
        Assert.Empty(ledger.For(ResourceCauses.Transfer));
    }

    [Fact]
    public void BTransferRollsBackEarlierJarMovesWhenTheNthJarMoveFails()
    {
        var master = new Workshop("Cathode主台", new List<string> { "圆罐" },
            ban: new List<string> { "蜜" }, maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("Anode从台", new List<string> { "圆罐" },
            ban: new List<string> { "酒" }, maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var firstJar = new Jar("圆罐", "酒", id: 9883, vocabulary: Words);
        var secondJar = new Jar("圆罐", "酒", id: 9884, vocabulary: Words);
        Assert.True(master.AddJar(firstJar));
        Assert.True(master.AddJar(secondJar));
        var demand = new OrderDemand(new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" }, new Requirement { Shape = "圆罐", Liquid = "酒" } },
            Constants.OrderType.B, Words), 0, new SequentialIdGenerator());
        var setup = Prepare(master, slave, demand);
        var ledger = new ResourceLedger();
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster,
            1_000, ledger, () => 1, new ThrowOnSecondJarMovePort());

        Assert.True(started.Success, started.Reason);
        var session = started.Session!;
        Assert.Equal(WorldOrderExecutionStage.Transfer, session.Stage);
        Assert.Equal(0, session.CompletedMasterSteps);
        session.AdvanceLogicalTime(1_000);
        Assert.Contains("second Jar move", session.Reason);

        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.Contains(master.Jars, item => ReferenceEquals(item, firstJar));
        Assert.Contains(master.Jars, item => ReferenceEquals(item, secondJar));
        Assert.Empty(slave.Jars);
        Assert.False(firstJar.IsMarked);
        Assert.False(secondJar.IsMarked);
        Assert.Equal(0, setup.Reservations.Count);
        Assert.Empty(ledger.For(ResourceCauses.Transfer));
        Assert.Empty(ledger.For(ResourceCauses.Return)); // No Return may be published for a handoff whose Transfer batch never committed.
    }

    [Fact]
    public void BTransferRestoresWorkshopAndOrderStagingAccountsWhenLiquidPumpFailsBeforeLedger()
    {
        var worldId = new WorldInstanceId("b-transfer-liquid-compensation-world");
        var masterOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "b-master");
        var slaveOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "b-slave");
        var shipOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Ship, "b-ship");
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "b-master", ownerIdentity: masterOwner);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "b-slave", ownerIdentity: slaveOwner);
        var jar = new Jar("圆罐", vocabulary: Words);
        Assert.True(master.AddJar(jar));
        using var suppressMasterPourIn = master.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：要求从台接收订单液体"
        });

        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot, DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var book = new WorldOrderSolveBook(board);
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 }, shipOwner);
        ReevaluateWithShip(book, roster, ship, demand.Id);
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations, ship);
        var ledger = new ResourceLedger();
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, board, book, admission, roster, 1_000,
            ledger, () => 41, null,
            new ThrowAtOrderTransferBoundary("liquid-transfer"));

        Assert.True(started.Success, started.Reason);
        var scopesBefore = admission.GetLiquidScopes(demand.Id)!.Value;
        Assert.Equal(1, scopesBefore.Master.GetAmount("酒"));
        Assert.Equal(0, scopesBefore.Slave!.GetAmount("酒"));
        var session = started.Session!;
        session.AdvanceLogicalTime(1_000);

        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.Contains("liquid-transfer", session.Reason);
        Assert.Same(jar, Assert.Single(master.Jars));
        Assert.Empty(slave.Jars);
        Assert.Equal(0, scopesBefore.Master.GetAmount("酒"));
        Assert.Equal(0, scopesBefore.Slave!.GetAmount("酒"));
        Assert.Equal(1, ship.GetAmount("酒")); // Failure cleanup refunds the restored source account exactly once.
        Assert.Equal(0, reservations.Count);
        Assert.Empty(ledger.For(ResourceCauses.Transfer));
        Assert.Empty(ledger.For(ResourceCauses.Discard));
        var returned = Assert.Single(ledger.For(ResourceCauses.Return),
            entry => entry.Kind == ResourceKinds.Liquid);
        var returnSource = AccountIdentity.ParseV2(returned.FromEndpoint.AccountId!);
        Assert.Equal(ResourceOwnerIdentityGuard.ForOrderStaging(masterOwner, demand.Id),
            returnSource.Owner);
        Assert.Equal("workshop.order-liquid-staging", returnSource.AccountKind);
    }

    [Fact]
    public void BTransferRestoresLeaseStageWhenLedgerPublicationFailsAfterPhysicalCommit()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", "酒", id: 9885, vocabulary: Words);
        Assert.True(master.AddJar(jar));
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var ledger = new ResourceLedger();
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster,
            1_000, ledger, () => 43, null,
            new ThrowAtOrderTransferBoundary("ledger-publication"));

        Assert.True(started.Success, started.Reason);
        var lease = setup.Reservations.FindLease(demand.Id)!;
        var session = started.Session!;
        session.AdvanceLogicalTime(1_000);

        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.Contains("ledger-publication", session.Reason);
        Assert.Same(jar, Assert.Single(master.Jars));
        Assert.Empty(slave.Jars);
        Assert.Equal(OrderResourceLeaseStage.Settled, lease.Stage);
        Assert.Equal(0, setup.Reservations.Count);
        Assert.Empty(ledger.For(ResourceCauses.Transfer));
    }

    [Fact]
    public void RuntimeFreezesAndRetainsUncertainBTransferWhenWholeAccountCompensationFails()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", "酒", id: 9886, vocabulary: Words);
        Assert.True(master.AddJar(jar));
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var ledger = new ResourceLedger();
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster,
            1_000, ledger, () => 47, null,
            new ThrowAtOrderTransferBoundary("jar-moves", "compensation"));
        Assert.True(started.Success, started.Reason);

        var runtime = CreateRuntime(setup.Board, setup.Roster, ledger, () => 47);
        Assert.True(runtime.RegisterExecutionSession(started.Session!));
        var lease = setup.Reservations.FindLease(demand.Id)!;

        var error = Assert.ThrowsAny<AggregateException>(() => runtime.AdvanceWorldTime(1_000));

        Assert.Contains("全量补偿失败", error.Message);
        Assert.True(runtime.IsFaulted);
        Assert.Contains("Injected B-class compensation failure", runtime.FaultReason);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Same(lease, setup.Reservations.FindLease(demand.Id));
        Assert.Equal(OrderResourceLeaseStage.TransferPending, lease.Stage);
        Assert.Empty(ledger.For(ResourceCauses.Transfer));

        // The uncertain physical state is deliberately not auto-repaired or auto-retried.
        Assert.Empty(master.Jars);
        Assert.Same(jar, Assert.Single(slave.Jars));
        var worldTimeAtFault = runtime.Clock.WorldTimeMilli;
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceWorldTime(1_000));
        Assert.Equal(worldTimeAtFault, runtime.Clock.WorldTimeMilli);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Equal(1, setup.Reservations.Count);
    }

    [Fact]
    public void RestoredSlaveSessionRetainsCommittedHandoffForLedgerReturnOnFailure()
    {
        var worldId = new WorldInstanceId("world-slave-rollback-endpoints");
        var masterOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "rollback-master");
        var slaveOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "rollback-slave");
        var shipOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Ship, "rollback-ship");
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "rollback-master", ownerIdentity: masterOwner);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "rollback-slave", ownerIdentity: slaveOwner);
        var jar = new Jar("圆罐", vocabulary: Words);
        Assert.True(master.AddJar(jar));
        using var suppressMasterPourIn = master.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：要求由从台执行灌注"
        });

        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot, DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var book = new WorldOrderSolveBook(board);
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 }, shipOwner);
        ReevaluateWithShip(book, roster, ship, demand.Id);
        Assert.True(book.Find(demand.Id)!.Result!.IsFeasible);
        Assert.Empty(book.Find(demand.Id)!.Result!.Plan!.MasterSteps);
        Assert.Contains(book.Find(demand.Id)!.Result!.Plan!.SlaveSteps,
            step => step.Operation?.Type == "POUR_IN");
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations, ship);
        var ledger = new ResourceLedger();
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, board, book, admission, roster, 1_000, ledger, () => 1);
        Assert.True(started.Success, started.Reason);

        started.Session!.AdvanceLogicalTime(1_000); // Commit Master -> Slave handoff.
        Assert.Equal(WorldOrderExecutionStage.SlaveSteps, started.Session.Stage);
        var progress = started.Session.CaptureProgress();
        Assert.True(progress.SlaveTransferCommitted);
        Assert.Contains(ledger.For(ResourceCauses.Transfer), item => item.Kind == ResourceKinds.Jar);

        Assert.Throws<InvalidOperationException>(() => WorldOrderExecutionSession.RestoreProgress(
            progress, board, book, admission, roster, reservations, new ResourceLedger(), () => 1));
        var restored = WorldOrderExecutionSession.RestoreProgress(
            progress, board, book, admission, roster, reservations, ledger, () => 1);
        using var suppressSlavePourIn = slave.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：恢复后从台操作失败"
        });
        restored.AdvanceLogicalTime(1_000);

        Assert.Equal(WorldOrderExecutionStage.Failed, restored.Stage);
        Assert.Same(jar, Assert.Single(master.Jars));
        Assert.Empty(slave.Jars);
        Assert.Equal(1, ship.GetAmount("酒"));
        Assert.Equal(0, reservations.Count);
        var jarReturn = Assert.Single(ledger.For(ResourceCauses.Return), item =>
            item.Kind == ResourceKinds.Jar && item.From == slave.Name && item.To == master.Name);
        Assert.Equal(ResourceEndpointKind.Account, jarReturn.FromEndpoint.Kind);
        Assert.Equal(ResourceEndpointKind.Account, jarReturn.ToEndpoint.Kind);
        Assert.Equal("rollback-slave", AccountIdentity.ParseV2(jarReturn.FromEndpoint.AccountId!).Owner.LocalOwnerId);
        Assert.Equal("rollback-master", AccountIdentity.ParseV2(jarReturn.ToEndpoint.AccountId!).Owner.LocalOwnerId);
        var liquidReturn = Assert.Single(ledger.For(ResourceCauses.Return), item =>
            item.Kind == ResourceKinds.Liquid && item.To == "自机液库");
        Assert.Equal(ResourceEndpointKind.Account, liquidReturn.FromEndpoint.Kind);
        Assert.Equal(ResourceEndpointKind.Account, liquidReturn.ToEndpoint.Kind);
        Assert.Equal("rollback-ship", AccountIdentity.ParseV2(liquidReturn.ToEndpoint.AccountId!).Owner.LocalOwnerId);
    }


    [Fact]
    public void SettlementCompensationFailureFreezesRuntimeAndPreservesActiveOrderLease()
    {
        var setup = PrepareSettlementScenario(
            settlementFaultPort: new ThrowAtSettlementBoundary("master-drain", failCompensation: true));
        var scopes = setup.Admission.GetLiquidScopes(setup.Demand.Id)!.Value;
        var shipBefore = setup.Ship.Capture();
        Assert.True(scopes.Master.Staging.TryReceiveExact(
            setup.Demand.Id, "酒", 1, out var masterGrant), masterGrant.Reason);

        var runtime = CreateRuntime(setup.Board, setup.Roster, setup.Ledger, () => 17);
        Assert.True(runtime.RegisterExecutionSession(setup.Session));
        var lease = setup.Reservations.FindLease(setup.Demand.Id)!;
        using var suppressSlavePourIn = setup.Roster.Slave!.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：触发订单失败收尾并进入液体结算"
        });

        var error = Assert.Throws<OrderSettlementTransactionException>(
            () => runtime.AdvanceWorldTime(1_000));

        Assert.Contains("补偿回滚失败", error.Message);
        Assert.Contains("Injected settlement compensation failure", error.InnerException!.Message);
        Assert.True(runtime.IsFaulted);
        Assert.Contains("订单液体结算", runtime.FaultReason);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Same(lease, setup.Reservations.FindLease(setup.Demand.Id));
        Assert.Equal(1, setup.Reservations.Count);
        Assert.Equal(OrderResourceSettlementStage.NotStarted, lease.SettlementStage);
        Assert.Single(setup.Ledger.For(ResourceCauses.Return),
            entry => entry.Kind == ResourceKinds.Jar);
        Assert.DoesNotContain(setup.Ledger.For(ResourceCauses.Return),
            entry => entry.Kind == ResourceKinds.Liquid);
        Assert.Empty(setup.Ledger.For(ResourceCauses.Discard));
        Assert.Empty(scopes.Master.Balance);
        Assert.Equal(1, scopes.Slave!.GetAmount("酒"));
        Assert.Equal(shipBefore.Balances.GetValueOrDefault("酒"), setup.Ship.GetAmount("酒"));

        var frozenTime = runtime.Clock.WorldTimeMilli;
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceWorldTime(1_000));
        Assert.Equal(frozenTime, runtime.Clock.WorldTimeMilli);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Same(lease, setup.Reservations.FindLease(setup.Demand.Id));
    }

    [Fact]
    public void PostReceiptSettlementFailureFreezesRuntimeAndRetainsLedgerReceiptAndLease()
    {
        var setup = PrepareSettlementScenario(
            settlementFaultPort: new ThrowAtSettlementBoundary("ledger-batch"));
        var shipBefore = setup.Ship.GetAmount("酒");
        var scopes = setup.Admission.GetLiquidScopes(setup.Demand.Id)!.Value;
        var runtime = CreateRuntime(setup.Board, setup.Roster, setup.Ledger, () => 17);
        Assert.True(runtime.RegisterExecutionSession(setup.Session));
        var lease = setup.Reservations.FindLease(setup.Demand.Id)!;

        using var suppressSlavePourIn = setup.Roster.Slave!.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：触发结算收据提交后的故障窗口"
        });

        var error = Assert.Throws<OrderSettlementTransactionException>(
            () => runtime.AdvanceWorldTime(1_000));

        Assert.Contains("账本/租约释放阶段", error.Message);
        Assert.True(runtime.IsFaulted);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Same(lease, setup.Reservations.FindLease(setup.Demand.Id));
        Assert.Equal(1, setup.Reservations.Count);
        Assert.Equal(OrderResourceSettlementStage.ResourcesReturned, lease.SettlementStage);
        Assert.NotNull(lease.SettlementReceipt);
        Assert.True(setup.Ledger.HasRecordedOperation(lease.SettlementOperationId));
        Assert.Single(setup.Ledger.For(ResourceCauses.Return),
            entry => entry.Kind == ResourceKinds.Liquid && entry.Name == "酒");
        Assert.Empty(setup.Ledger.For(ResourceCauses.Discard));
        Assert.Empty(scopes.Master.Balance);
        Assert.Empty(scopes.Slave!.Balance);
        Assert.Equal(shipBefore + 1, setup.Ship.GetAmount("酒"));

        var frozenTime = runtime.Clock.WorldTimeMilli;
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceWorldTime(1_000));
        Assert.Equal(frozenTime, runtime.Clock.WorldTimeMilli);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Same(lease, setup.Reservations.FindLease(setup.Demand.Id));
    }

    [Theory]
    [InlineData("master-drain")]
    [InlineData("slave-drain")]
    [InlineData("ship-return-before-receipt")]
    public void SettlementFailureBeforeReceiptRestoresAllResourceAccounts(string boundary)
    {
        var setup = PrepareSettlementScenario();
        var scopes = setup.Admission.GetLiquidScopes(setup.Demand.Id)!.Value;
        var masterBefore = scopes.Master.Balance.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var slaveBefore = scopes.Slave!.Balance.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var shipBefore = setup.Ship.Capture();

        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
            new OrderResourceSettlementService(new ThrowAtSettlementBoundary(boundary))
                .SettleAndRelease(setup.Demand.Id, setup.Admission, setup.Ledger, 17));
        Assert.Contains("Injected settlement failure", error.InnerException!.Message);

        Assert.Equal(masterBefore.OrderBy(item => item.Key), scopes.Master.Balance.OrderBy(item => item.Key));
        Assert.Equal(slaveBefore.OrderBy(item => item.Key), scopes.Slave.Balance.OrderBy(item => item.Key));
        Assert.Equal(shipBefore.Balances.OrderBy(item => item.Key), setup.Ship.Balances.OrderBy(item => item.Key));
        Assert.Equal(OrderResourceSettlementStage.NotStarted,
            setup.Reservations.FindLease(setup.Demand.Id)!.SettlementStage);
        Assert.False(setup.Ledger.HasRecordedOperation(
            setup.Reservations.FindLease(setup.Demand.Id)!.SettlementOperationId));
        Assert.Equal(1, setup.Reservations.Count);

        var receipt = new OrderResourceSettlementService().SettleAndRelease(
            setup.Demand.Id, setup.Admission, setup.Ledger, 18);
        Assert.NotNull(receipt);
        Assert.Equal(1, setup.Ship.GetAmount("酒"));
        Assert.Empty(scopes.Master.Balance);
        Assert.Empty(scopes.Slave.Balance);
        Assert.Equal(0, setup.Reservations.Count);
        Assert.Single(setup.Ledger.For(ResourceCauses.Return));
    }


    [Fact]
    public void MultiSourceSettlementPreservesEachWorkshopOrderAccountAllocation()
    {
        var setup = PrepareSettlementScenario(withOwnerIdentities: true);
        var lease = setup.Reservations.FindLease(setup.Demand.Id)!;
        var scopes = setup.Admission.GetLiquidScopes(setup.Demand.Id)!.Value;
        Assert.True(scopes.Master.Staging.TryReceiveExact(
            setup.Demand.Id, "酒", 1, out var masterGrant), masterGrant.Reason);

        var settlementError = Assert.ThrowsAny<InvalidOperationException>(() =>
            new OrderResourceSettlementService(new ThrowAtSettlementBoundary("reservation-release"))
                .SettleAndRelease(setup.Demand.Id, setup.Admission, setup.Ledger, 19));
        Assert.Contains("Injected settlement failure", settlementError.InnerException!.Message);
        var receipt = lease.SettlementReceipt;

        Assert.NotNull(receipt);
        Assert.Equal(2, receipt!.Requested["酒"]);
        Assert.Equal(1, receipt.Accepted["酒"]);
        Assert.Equal(1, receipt.Discarded["酒"]);
        Assert.Collection(receipt.SourceAllocations,
            allocation => Assert.Contains("master-owned",
                AccountIdentity.ParseV2(allocation.SourceEndpoint.AccountId!).Owner.LocalOwnerId),
            allocation => Assert.Contains("slave-owned",
                AccountIdentity.ParseV2(allocation.SourceEndpoint.AccountId!).Owner.LocalOwnerId));
        var returned = Assert.Single(setup.Ledger.For(ResourceCauses.Return));
        var discarded = Assert.Single(setup.Ledger.For(ResourceCauses.Discard));
        Assert.StartsWith("account:v2:", returned.FromAccountId);
        Assert.StartsWith("account:v2:", returned.ToAccountId);
        Assert.Equal(ResourceEndpointKind.Account, returned.FromEndpoint.Kind);
        Assert.Equal(ResourceEndpointKind.Account, returned.ToEndpoint.Kind);
        Assert.Equal(returned.ToAccountId, returned.ToEndpoint.AccountId);
        Assert.StartsWith("account:v2:", discarded.FromAccountId);
        Assert.Equal(ResourceEndpointKind.Account, discarded.FromEndpoint.Kind);
        Assert.Equal(ResourceEndpointKind.ExternalSink, discarded.ToEndpoint.Kind);
        Assert.Equal(receipt.SourceAllocations[0].SourceEndpoint.AccountId, returned.FromAccountId);
        Assert.Equal(receipt.SourceAllocations[1].SourceEndpoint.AccountId, discarded.FromAccountId);
        Assert.Equal(1, receipt.SourceAllocations[0].Accepted["酒"]);
        Assert.Equal(1, receipt.SourceAllocations[1].Discarded["酒"]);
        Assert.All(receipt.SourceAllocations, allocation =>
        {
            Assert.Equal(ResourceEndpointKind.Account, allocation.SourceEndpoint.Kind);
            Assert.Equal(ResourceEndpointKind.Account, allocation.TargetEndpoint.Kind);
            Assert.Equal(ResourceEndpointKind.ExternalSink, allocation.DiscardEndpoint.Kind);
        });

        var leaseWire = OrderResourceLeaseWireDto.FromLease(lease);
        var leaseBookWire = new WorldResourceReservationBookWireDto(new[] { leaseWire });
        var encodedLeaseBook = WorldV2CheckpointJsonCodec.Encode(leaseBookWire, null);
        var restoredLeaseBook = WorldV2CheckpointJsonCodec
            .Decode<WorldResourceReservationBookWireDto>(encodedLeaseBook).Payload;
        restoredLeaseBook.Validate();
        var restoredReceipt = Assert.Single(restoredLeaseBook.Leases).ToReceipt();
        Assert.NotNull(restoredReceipt);
        Assert.Equal(
            receipt.SourceAllocations.Select(item => item.SourceEndpoint).OrderBy(item => item.AccountId),
            restoredReceipt.SourceAllocations.Select(item => item.SourceEndpoint).OrderBy(item => item.AccountId));
    }

    [Theory]
    [InlineData("ledger-batch")]
    [InlineData("stage-mark")]
    [InlineData("reservation-release")]
    public void SettlementPostReceiptFailureCanReplayWithoutDuplicateReturnOrLedger(string boundary)
    {
        var setup = PrepareSettlementScenario();
        var lease = setup.Reservations.FindLease(setup.Demand.Id)!;
        var operationId = lease.SettlementOperationId;

        var error = Assert.Throws<InvalidOperationException>(() =>
            new OrderResourceSettlementService(new ThrowAtSettlementBoundary(boundary))
                .SettleAndRelease(setup.Demand.Id, setup.Admission, setup.Ledger, 17));
        Assert.Contains("Injected settlement failure", error.Message);

        Assert.Equal(1, setup.Ship.GetAmount("酒"));
        Assert.True(setup.Ledger.HasRecordedOperation(operationId));
        Assert.Single(setup.Ledger.For(ResourceCauses.Return));
        Assert.Equal(1, setup.Reservations.Count);
        Assert.Equal(
            boundary == "ledger-batch"
                ? OrderResourceSettlementStage.ResourcesReturned
                : OrderResourceSettlementStage.LedgerRecorded,
            lease.SettlementStage);

        var lastSeq = setup.Ledger.LastSeq;
        var receipt = new OrderResourceSettlementService().SettleAndRelease(
            setup.Demand.Id, setup.Admission, setup.Ledger, 18);
        Assert.NotNull(receipt);
        Assert.Equal(lastSeq, setup.Ledger.LastSeq);
        Assert.Single(setup.Ledger.For(ResourceCauses.Return));
        Assert.Equal(1, setup.Ship.GetAmount("酒"));
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void RestoreRejectsLedgerRecordedSettlementWithoutMatchingLedgerReceipt()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        Assert.True(master.AddJar(jar));
        using var suppressMasterPourIn = master.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：从台执行"
        });

        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot, DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var book = new WorldOrderSolveBook(board);
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 });
        ReevaluateWithShip(book, roster, ship, demand.Id);
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations, ship);
        var ledger = new ResourceLedger();
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, board, book, admission, roster, 1_000, ledger, () => 17);
        Assert.True(started.Success, started.Reason);

        started.Session!.AdvanceLogicalTime(1_000);
        var progress = started.Session.CaptureProgress();
        Assert.True(progress.SlaveTransferCommitted);
        Assert.True(ledger.HasRecordedOperation(
            reservations.FindLease(demand.Id)!.WorldVersion + ":order:" + demand.Id + ":transfer"));

        var lease = reservations.FindLease(demand.Id)!;
        var scopes = admission.GetLiquidScopes(demand.Id)!.Value;
        var settlementRequested = new Dictionary<string, int>(
            scopes.Master.Balance, StringComparer.Ordinal);
        foreach (var item in scopes.Slave!.Balance)
            settlementRequested[item.Key] =
                checked(settlementRequested.GetValueOrDefault(item.Key) + item.Value);
        Assert.NotEmpty(settlementRequested);
        lease.RecordSettlement(new LiquidBatchReceipt(
            settlementRequested,
            new Dictionary<string, int>(settlementRequested, StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var balanceError = Assert.Throws<InvalidOperationException>(() =>
            WorldOrderExecutionSession.RestoreProgress(
                progress, board, book, admission, roster, reservations, ledger, () => 17));
        Assert.Contains("暂存账户仍包含未结算余额", balanceError.Message);

        scopes.Master.Staging.DrainOrder(demand.Id);
        scopes.Slave.Staging.DrainOrder(demand.Id);
        lease.MarkSettlementLedgerRecorded();

        var serviceError = Assert.Throws<InvalidOperationException>(() =>
            new OrderResourceSettlementService().SettleAndRelease(
                demand.Id, admission, ledger, settlementTick: null));
        Assert.Contains("Settlement operation receipt", serviceError.Message);

        Assert.Throws<InvalidOperationException>(() =>
            new OrderResourceSettlementService().SettleAndRelease(
                demand.Id, admission, ledger: null, settlementTick: null));
        Assert.Throws<InvalidOperationException>(() =>
            WorldOrderExecutionSession.RestoreProgress(
                progress, board, book, admission, roster, reservations, resourceLedger: null, resourceLedgerTick: () => 17));

        var ledgerError = Assert.Throws<InvalidOperationException>(() =>
            WorldOrderExecutionSession.RestoreProgress(
                progress, board, book, admission, roster, reservations, ledger, () => 17));
        Assert.Contains("Settlement operation receipt", ledgerError.Message);
    }

    [Fact]
    public void TransferPreflightDoesNotRollbackResourcesThatWereAlreadyOnTheSlave()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var plannedJar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(plannedJar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var runtime = CreateRuntime(setup.Board, setup.Roster);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);

        // 外部写入发生在准入之后、转移之前。会话必须拒绝转移，但不能把这只罐子当成自己的临时资源回流。
        var externalJar = new Jar("圆罐", vocabulary: Words);
        Assert.True(slave.AddJar(externalJar));
        var session = started.Session!;
        for (var i = 0; i < 8 && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);

        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.Same(externalJar, Assert.Single(slave.Jars));
        Assert.False(externalJar.IsMarked);
        Assert.Contains(master.Jars, item => item.Id == plannedJar.Id);
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void FailedSlaveRollbackFreezesRuntimeAndPreservesSessionAndLease()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var firstJar = new Jar("圆罐", vocabulary: Words);
        var secondJar = new Jar("圆罐", vocabulary: Words);
        Assert.True(master.AddJar(firstJar));
        Assert.True(master.AddJar(secondJar));
        master.AddLiquid("酒", 2);

        var demand = new OrderDemand(new JarOrderSpecification(
            new[]
            {
                new Requirement { Shape = "圆罐", Liquid = "酒" },
                new Requirement { Shape = "圆罐", Liquid = "酒" }
            },
            Constants.OrderType.B, Words), 0, new SequentialIdGenerator());
        var setup = Prepare(master, slave, demand);
        var ledger = new ResourceLedger();
        var runtime = CreateRuntime(setup.Board, setup.Roster, ledger, () => 53);
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster,
            1_000, ledger, () => 53,
            jarTransferPort: null,
            transferFaultPort: null,
            slaveRollbackFaultPort: new ThrowAtSlaveRollbackBoundary(failCompensation: true));
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;
        Assert.True(runtime.RegisterExecutionSession(session));
        var lease = setup.Reservations.FindLease(demand.Id)!;

        for (var i = 0; i < 8 && !slave.HasResources && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);
        Assert.True(slave.HasResources, "订单必须完成 B 类交接后才允许测试从台回落");
        runtime.AdvanceWorldTime(1_000);
        Assert.Equal(1, slave.Jars.Count(item => item.Liquid == "酒"));

        using var suppressSecondPourIn = slave.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：第二次灌注被抑制"
        });

        var error = Assert.ThrowsAny<SlaveRollbackTransactionException>(
            () => runtime.AdvanceWorldTime(1_000));

        Assert.Contains("全量补偿失败", error.Message);
        Assert.True(runtime.IsFaulted);
        Assert.Contains("SlaveRollback compensation failure", runtime.FaultReason);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Same(lease, setup.Reservations.FindLease(demand.Id));
        Assert.Equal(1, setup.Reservations.Count);
        Assert.Equal(OrderResourceLeaseStage.SlaveExecuting, lease.Stage);
        Assert.Equal(WorldOrderExecutionStage.SlaveSteps, session.Stage);
        Assert.Empty(slave.Jars);
        Assert.Equal(0, slave.LiquidStorage["酒"]);
        Assert.Contains(master.Jars, item => item.Liquid == "酒");
        Assert.Empty(ledger.For(ResourceCauses.Return));
        Assert.Empty(ledger.For(ResourceCauses.Discard));

        var frozenTime = runtime.Clock.WorldTimeMilli;
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceWorldTime(1_000));
        Assert.Equal(frozenTime, runtime.Clock.WorldTimeMilli);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Same(lease, setup.Reservations.FindLease(demand.Id));
    }

    [Fact]
    public void BOrderFailureAfterOneSlaveOperationKeepsConsumedLiquidConsumed()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var firstJar = new Jar("圆罐", vocabulary: Words);
        var secondJar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(firstJar);
        master.AddJar(secondJar);
        master.AddLiquid("酒", 2);
        var demand = new OrderDemand(new JarOrderSpecification(
            new[]
            {
                new Requirement { Shape = "圆罐", Liquid = "酒" },
                new Requirement { Shape = "圆罐", Liquid = "酒" }
            },
            Constants.OrderType.B, Words), 0, new SequentialIdGenerator());
        var setup = Prepare(master, slave, demand);
        var plan = setup.Book.Find(demand.Id)!.Result!.Plan!;
        Assert.Equal(2, plan.SlaveSteps.Count(step => step.Operation?.Type == "POUR_IN"));

        var runtime = CreateRuntime(setup.Board, setup.Roster);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;

        for (var i = 0; i < 8 && !slave.HasResources && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);

        Assert.True(slave.HasResources, "测试必须先越过 B 类全量转移边界");
        Assert.Equal(2, slave.LiquidStorage["酒"]);

        // 先成功执行一个 POUR_IN，再用操作级 Suppress 让第二个 POUR_IN 失败。
        // 不能在仍有游离酒库存时施加 ban：ban 同时禁止存储，不能留下非法余额。
        runtime.AdvanceWorldTime(1_000);
        Assert.Equal(1, slave.Jars.Count(item => item.Liquid == "酒"));
        Assert.Equal(1, slave.LiquidStorage["酒"]);

        using var suppressSecondPourIn = slave.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：第二次灌注被抑制"
        });
        runtime.AdvanceWorldTime(1_000);

        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.False(slave.HasResources, "失败收尾必须清空本订单临时从台资源");
        Assert.Contains(master.Jars, item => item.Id == firstJar.Id || item.Id == secondJar.Id);
        Assert.Contains(master.Jars, item => item.Liquid == "酒");
        Assert.Equal(1, master.LiquidStorage["酒"]);
        Assert.Equal(0, setup.Reservations.Count);
        Assert.False(firstJar.IsMarked, "失败终态必须清理已操作并回流的 Jar Mark");
        Assert.False(secondJar.IsMarked);
    }

    [Fact]
    public void SettlementTickFailureFaultsRuntimeAndPreventsFurtherWorldMutation()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var spare = new Workshop("备用台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var firstJar = new Jar("圆罐", vocabulary: Words);
        var secondJar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(firstJar);
        master.AddJar(secondJar);
        master.AddLiquid("酒", 2);

        var demand = new OrderDemand(new JarOrderSpecification(
            new[]
            {
                new Requirement { Shape = "圆罐", Liquid = "酒" },
                new Requirement { Shape = "圆罐", Liquid = "酒" }
            },
            Constants.OrderType.B, Words), 0, new SequentialIdGenerator());
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot,
            DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave, spare }, master, slave);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        book.Evaluate(demand.Id, WorldSnapshotFactory.Capture(roster), registry);
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations);
        var ledger = new ResourceLedger();
        var tickCalls = 0;
        int Tick()
        {
            tickCalls++;
            if (tickCalls == 2)
                throw new InvalidOperationException("模拟账本 tick 来源故障");
            return 17;
        }

        var runtime = CreateRuntime(board, roster, ledger, Tick);
        var started = runtime.TryStartExecutionSession(
            demand.Id, book, admission, 1_000);
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;

        for (var i = 0; i < 8 && !slave.HasResources && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);
        Assert.True(slave.HasResources, "必须先完成主从资源转移，触发第一次账本 tick");

        // 操作级抑制使订单进入失败结算；第二次 tick 在结算前抛出。
        // 用 Suppress 而不是临时 ban，避免把仍有库存的液体变成非法存储状态。
        using var suppressPourIn = slave.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "测试：灌注被抑制"
        });
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceWorldTime(1_000));

        Assert.True(runtime.IsFaulted);
        Assert.Contains("世界推进过程中发生异常", runtime.FaultReason);
        Assert.IsType<InvalidOperationException>(runtime.FaultException);
        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.True(slave.HasResources, "结算 tick 获取失败时，不应假装从台资源已完成回流");
        Assert.Equal(0, reservations.Count);

        var frozenWorldTime = runtime.Clock.WorldTimeMilli;
        var frozenLiquid = slave.LiquidStorage["酒"];
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceWorldTime(1_000));
        Assert.Equal(frozenWorldTime, runtime.Clock.WorldTimeMilli);
        Assert.Equal(frozenLiquid, slave.LiquidStorage["酒"]);

        var lateStart = runtime.TryStartExecutionSession(demand.Id, book, admission, 1_000);
        Assert.False(lateStart.Success);
        Assert.Contains("已故障", lateStart.Reason);

        var replacement = runtime.BeginReplacement(
            WorkshopRole.Master, spare, new NoopInterruptionPort(), phaseDurationMilli: 1_000);
        Assert.False(replacement.Success);
        Assert.Contains("已故障", replacement.Error);
    }

    [Fact]
    public void SuccessSettlementTickIsFetchedBeforeDeliveryJarsAreRemoved()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var ledger = new ResourceLedger();
        var tickCalls = 0;
        int Tick()
        {
            tickCalls++;
            if (tickCalls == 2)
                throw new InvalidOperationException("模拟成功结算前的 tick 来源故障");
            return 23;
        }

        var runtime = CreateRuntime(setup.Board, setup.Roster, ledger, Tick);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;

        Exception? observed = null;
        for (var i = 0; i < 16 && !session.IsTerminal && observed == null; i++)
        {
            try
            {
                runtime.AdvanceWorldTime(1_000);
            }
            catch (InvalidOperationException ex)
            {
                observed = ex;
            }
        }

        Assert.NotNull(observed);
        Assert.True(runtime.IsFaulted);
        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.NotNull(setup.Board.Find(demand.Id));
        Assert.Contains(master.Jars, candidate => candidate.Id == jar.Id);
        Assert.False(jar.IsMarked);
        Assert.Equal(0, setup.Reservations.Count);
        Assert.True(tickCalls >= 3,
            "失败收尾应在预取失败后重新执行可确认的从台回流结算");
    }

    [Fact]
    public void BOrderInterruptionRecordsTransferReturnAndDiscardInWorldResourceLedger()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "owned.rollback.master");
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "owned.rollback.slave");
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒", cooldown: 2_000);
        var setup = Prepare(master, slave, demand);
        var ledger = new ResourceLedger();
        var runtime = CreateRuntime(setup.Board, setup.Roster, ledger, () => 17);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;

        for (var i = 0; i < 8 && !slave.HasResources && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);

        Assert.True(slave.HasResources, "测试必须先越过 B 类全量转移边界");
        Assert.True(slave.LiquidStorage["酒"] > 0);

        // 资源已转入从台后，主台新增禁令；中断结算时游离酒应被丢弃，不能回流。
        master.ReplaceCapability(new WorkshopCapability(
            master.BaseMarks, master.AdditionalMarks, new[] { "酒" }));
        var interrupted = session.InterruptForWorkshopReplacement();

        Assert.True(interrupted.Success, interrupted.Error);
        Assert.Equal(WorldOrderExecutionStage.Interrupted, session.Stage);
        Assert.False(slave.HasResources);
        Assert.Contains(master.Jars, item => item.Id == jar.Id);
        Assert.Equal(0, master.LiquidStorage["酒"]);
        Assert.Equal(0, setup.Reservations.Count);

        var jarTransfer = Assert.Single(ledger.For(ResourceCauses.Transfer),
            entry => entry.Kind == ResourceKinds.Jar && entry.Name == "圆罐");
        Assert.Equal(jar.Id!.Value.ToString(CultureInfo.InvariantCulture), jarTransfer.ResourceIdentity);
        Assert.Equal(new RuleResourceAccount("workshop.jar-storage", "owned.rollback.master").AccountId,
            jarTransfer.FromAccountId);
        Assert.Equal(new RuleResourceAccount("workshop.jar-storage", "owned.rollback.slave").AccountId,
            jarTransfer.ToAccountId);

        var liquidTransfer = Assert.Single(ledger.For(ResourceCauses.Transfer),
            entry => entry.Kind == ResourceKinds.Liquid && entry.Name == "酒");
        Assert.Equal("酒", liquidTransfer.ResourceIdentity);
        Assert.Equal(new RuleResourceAccount("workshop.free-liquid-staging", "owned.rollback.master").AccountId,
            liquidTransfer.FromAccountId);
        Assert.Equal(new RuleResourceAccount("workshop.free-liquid-staging", "owned.rollback.slave").AccountId,
            liquidTransfer.ToAccountId);

        var jarReturn = Assert.Single(ledger.For(ResourceCauses.Return),
            entry => entry.Kind == ResourceKinds.Jar && entry.Name == "圆罐");
        Assert.Equal(jar.Id!.Value.ToString(CultureInfo.InvariantCulture), jarReturn.ResourceIdentity);
        Assert.Equal(new RuleResourceAccount("workshop.jar-storage", "owned.rollback.slave").AccountId,
            jarReturn.FromAccountId);
        Assert.Equal(new RuleResourceAccount("workshop.jar-storage", "owned.rollback.master").AccountId,
            jarReturn.ToAccountId);

        var liquidDiscard = Assert.Single(ledger.For(ResourceCauses.Discard),
            entry => entry.Kind == ResourceKinds.Liquid && entry.Name == "酒");
        Assert.Equal("酒", liquidDiscard.ResourceIdentity);
        Assert.Equal(new RuleResourceAccount("workshop.free-liquid-staging", "owned.rollback.slave").AccountId,
            liquidDiscard.FromAccountId);
        Assert.Null(liquidDiscard.ToAccountId);
        Assert.All(ledger.Entries, entry => Assert.Equal(17, entry.Tick));
        Assert.Contains(session.ResourceSettlementLog,
            entry => entry.Command == "discard_liquid");
    }

    [Fact]
    public void BOrderOwnsTheSingleSlaveAndInterruptionReturnsTemporaryResourcesAndStartsCooldown()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1);
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒", cooldown: 3_000);
        var setup = Prepare(master, slave, demand);

        var runtime = CreateRuntime(setup.Board, setup.Roster);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;

        // B 类可能先执行主台步骤；持续推进直到主从转移完成。
        for (var i = 0; i < 8 && !slave.HasResources && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);

        Assert.True(slave.HasResources, "B 类计划应先把清单资源转入从台");
        Assert.True(setup.Reservations.Find(demand.Id)!.UsesSlave);

        var interruption = session.InterruptForWorkshopReplacement();

        Assert.True(interruption.Success, interruption.Error);
        Assert.Equal(WorldOrderExecutionStage.Interrupted, session.Stage);
        Assert.False(slave.HasResources);
        Assert.Equal(3_000, demand.CooldownRemainingLogicalMilli);
        Assert.Equal(WorldOrderSolveState.NotReady, setup.Book.Find(demand.Id)!.State);
        Assert.Equal(0, setup.Reservations.Count);
        Assert.Contains(master.Jars, item => item.Id == jar.Id);
    }

    [Fact]
    public void BOrderUsesOrderScopesPumpsRequiredLiquidAndSettlesBackToShipInventory()
    {
        var worldId = new WorldInstanceId("world-b-order-scope-endpoints");
        var masterOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "scope-master");
        var slaveOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "scope-slave");
        var shipOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Ship, "scope-ship");
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "scope-master", ownerIdentity: masterOwner);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "scope-slave", ownerIdentity: slaveOwner);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1); // 求解快照的兼容投影
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var ship = new ShipLiquidInventory(4,
            new Dictionary<string, int> { ["酒"] = 2 }, shipOwner);
        ReevaluateWithShip(setup.Book, setup.Roster, ship, demand.Id);
        var admission = new WorldOrderExecutionAdmission(
            setup.Board, setup.Book, setup.Roster, setup.Reservations, ship);
        var ledger = new ResourceLedger();
        var runtime = CreateRuntime(setup.Board, setup.Roster, ledger);

        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, admission, 1_000);

        Assert.True(started.Success, started.Reason);
        var initialProgress = started.Session!.CaptureProgress();
        Assert.Equal(started.Session.Stage, initialProgress.Stage);
        Assert.Equal(demand.Id, initialProgress.Manifest.DemandId);
        Assert.True(initialProgress.Plan.IsPartitioned);
        Assert.False(initialProgress.SlaveTransferCommitted);
        var scopes = admission.GetLiquidScopes(demand.Id);
        Assert.True(scopes.HasValue);
        Assert.Equal(1, scopes.Value.Master.GetAmount("酒"));
        Assert.Equal(0, scopes.Value.Slave!.GetAmount("酒"));

        for (var i = 0; i < 5 && !slave.HasResources && !started.Session!.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);

        Assert.True(slave.HasResources, "B 类应先完成主台到从台的资源泵送");
        var transferredProgress = started.Session.CaptureProgress();
        Assert.Equal(WorldOrderExecutionStage.SlaveSteps, transferredProgress.Stage);
        Assert.True(transferredProgress.SlaveTransferCommitted);
        Assert.Equal(0, scopes.Value.Master.GetAmount("酒"));
        Assert.Equal(1, scopes.Value.Slave!.GetAmount("酒"));
        Assert.Equal(1, master.LiquidStorage["酒"]); // 兼容求解快照的旧池未参与新路径扣账
        Assert.Equal(0, slave.LiquidStorage["酒"]);

        var interrupted = started.Session!.InterruptForWorkshopReplacement();

        Assert.True(interrupted.Success, interrupted.Error);
        Assert.Equal(WorldOrderExecutionStage.Interrupted, started.Session.Stage);
        Assert.Equal(WorldOrderExecutionStage.Interrupted, started.Session.CaptureProgress().Stage);
        Assert.Equal(2, ship.GetAmount("酒"));
        Assert.Equal(0, scopes.Value.Master.Staging.UsedCapacity);
        Assert.Equal(0, scopes.Value.Slave!.Staging.UsedCapacity);
        Assert.Equal(0, scopes.Value.Master.Staging.ReservedCapacity);
        Assert.Equal(0, scopes.Value.Slave!.Staging.ReservedCapacity);
        Assert.Contains(master.Jars, item => item.Id == jar.Id);

        var funding = Assert.Single(ledger.For(ResourceCauses.Flow));
        Assert.Equal("自机液库", funding.From);
        Assert.Equal("主台", funding.To);
        Assert.Equal(1, funding.Amount);
        Assert.Equal(ResourceEndpointKind.Account, funding.FromEndpoint.Kind);
        Assert.Equal(ResourceEndpointKind.Account, funding.ToEndpoint.Kind);
        Assert.Equal("scope-ship", AccountIdentity.ParseV2(funding.FromEndpoint.AccountId!).Owner.LocalOwnerId);
        Assert.Equal("scope-master", AccountIdentity.ParseV2(funding.ToEndpoint.AccountId!).Owner.LocalOwnerId);
        var transfer = Assert.Single(ledger.For(ResourceCauses.Transfer),
            item => item.Kind == ResourceKinds.Liquid);
        Assert.Equal("主台", transfer.From);
        Assert.Equal("从台", transfer.To);
        Assert.Equal(1, transfer.Amount);
        var returned = Assert.Single(ledger.For(ResourceCauses.Return),
            item => item.Kind == ResourceKinds.Liquid);
        Assert.Equal("订单暂存", returned.From);
        Assert.Equal("自机液库", returned.To);
        Assert.Equal(1, returned.Amount);
        Assert.Equal(ResourceEndpointKind.Account, returned.ToEndpoint.Kind);
        Assert.Equal("scope-ship", AccountIdentity.ParseV2(returned.ToEndpoint.AccountId!).Owner.LocalOwnerId);
    }


    [Fact]
    public void AOrderExecutesAgainstShipFundedScopeAndKeepsLegacyWorkshopPoolUntouched()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 0);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1); // 当前求解快照的兼容投影
        var demand = Demand(Constants.OrderType.A, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var ship = new ShipLiquidInventory(4,
            new Dictionary<string, int> { ["酒"] = 2 });
        ReevaluateWithShip(setup.Book, setup.Roster, ship, demand.Id);
        var admission = new WorldOrderExecutionAdmission(
            setup.Board, setup.Book, setup.Roster, setup.Reservations, ship);
        var runtime = CreateRuntime(setup.Board, setup.Roster);

        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, admission, 1_000);

        Assert.True(started.Success, started.Reason);
        var scopes = admission.GetLiquidScopes(demand.Id);
        Assert.True(scopes.HasValue);
        Assert.Null(scopes.Value.Slave);
        Assert.Equal(1, scopes.Value.Master.GetAmount("酒"));
        Assert.Equal(1, ship.GetAmount("酒"));

        runtime.AdvanceWorldTime(1_000);

        Assert.True(started.Session!.Succeeded, started.Session.Reason);
        Assert.Empty(master.Jars);
        Assert.Equal(1, ship.GetAmount("酒"));
        Assert.Equal(1, master.LiquidStorage["酒"]);
        Assert.Equal(0, scopes.Value.Master.Staging.UsedCapacity);
        Assert.Equal(0, scopes.Value.Master.Staging.ReservedCapacity);
    }


    [Fact]
    public void BOrderPumpsOnlySlaveRequiredLiquidAndLeavesMasterSurplusForSettlement()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words,
            ownedInstanceId: "owned.workshop.master");
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 1, vocabulary: Words,
            ownedInstanceId: "owned.workshop.slave");
        var sourceJar = new Jar("圆罐", "蜜", vocabulary: Words);
        var toolJar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(sourceJar);
        master.AddJar(toolJar);
        master.AddLiquid("酒", 1); // 供从台 POUR_IN 的初始资源

        var demand = new OrderDemand(new JarOrderSpecification(
            new[]
            {
                new Requirement { Shape = "圆罐" },
                new Requirement { Shape = "圆罐", Liquid = "酒" }
            }, Constants.OrderType.B, Words), 0, new SequentialIdGenerator());
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot,
            DemandAreaFulfillmentPolicy.OneShot);
        board.AddDemand(demand, DemandAreaKind.Active);
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 });
        var snapshot = WorldSnapshotFactory.Capture(roster, ship);

        var masterStep = new SolverStep
        {
            Step = 0,
            Requirement = new Requirement { Shape = "圆罐" },
            Operation = new SolverOperation
            {
                Type = "POUR_OUT", JarIndex = 0, TargetLiquid = "蜜",
                Consumed = new Dictionary<string, int>()
            },
            Before = new List<JarState>
            {
                new() { Id = sourceJar.Id, Shape = "圆罐", Liquid = "蜜" },
                new() { Id = toolJar.Id, Shape = "圆罐" }
            },
            After = new List<JarState>
            {
                new() { Id = sourceJar.Id, Shape = "圆罐", IsMarked = true },
                new() { Id = toolJar.Id, Shape = "圆罐" }
            },
            AfterLiquids = new Dictionary<string, int> { ["酒"] = 1, ["蜜"] = 1 }
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
            Before = new List<JarState>
            {
                new() { Id = toolJar.Id, Shape = "圆罐", MasterOrigin = true }
            },
            After = new List<JarState>
            {
                new() { Id = toolJar.Id, Shape = "圆罐", Liquid = "酒", IsMarked = true, MasterOrigin = true }
            },
            AfterLiquids = new Dictionary<string, int>()
        };
        var plan = WorldOrderSolvePlan.Partitioned(new PartitionSearchOutcome
        {
            MasterPath = new List<SolverStep> { masterStep },
            SlavePath = new List<SolverStep> { slaveStep },
            TransferJarIds = new List<int> { sourceJar.Id!.Value, toolJar.Id!.Value },
            TransferLiquids = new Dictionary<string, int> { ["酒"] = 1 }
        });

        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new FixedPlanSolver(plan)
        });
        var solved = book.Evaluate(demand.Id, snapshot, registry);
        Assert.True(solved.Result!.IsFeasible, solved.Result.Reason);

        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(
            board, book, roster, reservations, ship);
        var runtime = CreateRuntime(board, roster);
        var started = runtime.TryStartExecutionSession(demand.Id, book, admission, 1_000);

        Assert.True(started.Success, started.Reason);
        var scopes = admission.GetLiquidScopes(demand.Id);
        Assert.True(scopes.HasValue);

        runtime.AdvanceWorldTime(1_000); // 主台倒出 1 蜜，主台余额为 酒1 + 蜜1
        Assert.Equal(1, scopes.Value.Master.GetAmount("酒"));
        Assert.Equal(1, scopes.Value.Master.GetAmount("蜜"));
        Assert.Contains(started.Session!.ResourceTransitions, transition =>
            transition.RuleId == "jar.workshop.pour-out" &&
            transition.ResourceEffects.Any(effect =>
                effect.Kind == RuleResourceEffectKind.Transfer &&
                effect.From?.AccountKind == "jar.internal-liquid" &&
                effect.To?.AccountKind == "workshop.order-output-boundary"));
        Assert.Contains(started.Session.ResourceTransitions, transition =>
            transition.RuleId == "workshop.staging.admission" &&
            transition.ResourceEffects.Any(effect =>
                effect.Kind == RuleResourceEffectKind.Transfer &&
                effect.From?.AccountKind == "workshop.order-output-boundary" &&
                effect.To?.AccountKind == "workshop.free-liquid-staging"));

        runtime.AdvanceWorldTime(1_000); // 只泵送从台需要的 1 酒
        // Master-operated sourceJar remains marked after handoff; the distinct
        // toolJar is still unmarked and available for its one Slave operation.
        Assert.True(Assert.Single(slave.Jars, item => item.Id == sourceJar.Id).IsMarked);
        Assert.False(Assert.Single(slave.Jars, item => item.Id == toolJar.Id).IsMarked);
        Assert.Empty(master.Jars);
        Assert.Equal(
            new[] { sourceJar.Id!.Value, toolJar.Id!.Value }.OrderBy(id => id),
            slave.Jars.Select(item => item.Id!.Value).OrderBy(id => id));
        Assert.Equal(0, scopes.Value.Master.GetAmount("酒"));
        Assert.Equal(1, scopes.Value.Master.GetAmount("蜜"));
        Assert.Equal(1, scopes.Value.Slave!.GetAmount("酒"));
        Assert.Equal(1, scopes.Value.Slave.Staging.Capacity);
        var handoff = Assert.IsType<RuleTransition>(started.Session.TransferTransition);
        Assert.Equal("owned.workshop.master->owned.workshop.slave", handoff.Target);
        Assert.Contains(handoff.ResourceEffects, effect =>
            effect.ResourceKind == RuleResourceKind.Liquid &&
            effect.Kind == RuleResourceEffectKind.Transfer &&
            effect.ResourceId == "酒");
        Assert.DoesNotContain(handoff.Before.Values.Keys,
            key => key.Contains("Polarity", StringComparison.OrdinalIgnoreCase));

        runtime.AdvanceWorldTime(1_000); // 从台消耗酒并完成交付

        Assert.True(started.Session!.Succeeded, started.Session.Reason);
        Assert.Equal(1, ship.GetAmount("蜜"));
        Assert.Equal(0, ship.GetAmount("酒"));
        Assert.Equal(0, scopes.Value.Master.Staging.UsedCapacity);
        Assert.Equal(0, scopes.Value.Slave.Staging.UsedCapacity);
        Assert.Empty(master.Jars);
        Assert.Empty(slave.Jars);
        Assert.False(sourceJar.IsMarked, "成功交付前必须清理跨台保留的 Mark");
        Assert.False(toolJar.IsMarked);
    }

}

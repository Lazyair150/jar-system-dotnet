using System;
using System.Collections.Generic;
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
        var runtime = CreateRuntime(setup.Board, setup.Roster);
        var started = runtime.TryStartExecutionSession(
            demand.Id, setup.Book, setup.Admission, 1_000);
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;

        for (var i = 0; i < 12 && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);

        Assert.True(session.Succeeded, session.Reason);
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
    public void BTransferAllowsJarWhosePolarityDiffersFromSlaveFocus()
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

        // Admission happened under an unrestricted Slave. Before transfer, focus Slave on Anode.
        // The Master-produced Cathode Jar still transfers: role-to-role Jar ownership movement
        // is independent of either Workshop's chemical-operation polarity.
        slave.ReplaceCapability(new WorkshopCapability(
            new[] { "圆罐" }, ban: new[] { "酒" }));

        var session = started.Session!;
        for (var i = 0; i < 10 && !session.IsTerminal; i++)
            runtime.AdvanceWorldTime(1_000);

        Assert.True(session.Succeeded, session.Reason);
        Assert.DoesNotContain(master.Jars, item => item.Id == jar.Id);
        Assert.DoesNotContain(slave.Jars, item => item.Id == jar.Id);
        Assert.Equal("酒", jar.Liquid);
        Assert.False(jar.IsMarked);
        Assert.Equal(0, setup.Reservations.Count);
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
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
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

        Assert.Contains(ledger.For(ResourceCauses.Transfer),
            entry => entry.Kind == ResourceKinds.Jar && entry.Name == "圆罐" &&
                entry.From == master.Name && entry.To == slave.Name);
        Assert.Contains(ledger.For(ResourceCauses.Transfer),
            entry => entry.Kind == ResourceKinds.Liquid && entry.Name == "酒" &&
                entry.From == master.Name && entry.To == slave.Name);
        Assert.Contains(ledger.For(ResourceCauses.Return),
            entry => entry.Kind == ResourceKinds.Jar && entry.Name == "圆罐" &&
                entry.From == slave.Name && entry.To == master.Name);
        Assert.Contains(ledger.For(ResourceCauses.Discard),
            entry => entry.Kind == ResourceKinds.Liquid && entry.Name == "酒" &&
                entry.From == slave.Name && entry.To == null);
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
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var jar = new Jar("圆罐", vocabulary: Words);
        master.AddJar(jar);
        master.AddLiquid("酒", 1); // 求解快照的兼容投影
        var demand = Demand(Constants.OrderType.B, "圆罐", "酒");
        var setup = Prepare(master, slave, demand);
        var ship = new ShipLiquidInventory(4,
            new Dictionary<string, int> { ["酒"] = 2 });
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
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 1, vocabulary: Words);
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

        runtime.AdvanceWorldTime(1_000); // 只泵送从台需要的 1 酒
        // Master-operated sourceJar remains marked after handoff; the distinct
        // toolJar is still unmarked and available for its one Slave operation.
        Assert.True(Assert.Single(slave.Jars, item => item.Id == sourceJar.Id).IsMarked);
        Assert.False(Assert.Single(slave.Jars, item => item.Id == toolJar.Id).IsMarked);
        Assert.Equal(0, scopes.Value.Master.GetAmount("酒"));
        Assert.Equal(1, scopes.Value.Master.GetAmount("蜜"));
        Assert.Equal(1, scopes.Value.Slave!.GetAmount("酒"));
        Assert.Equal(1, scopes.Value.Slave.Staging.Capacity);

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

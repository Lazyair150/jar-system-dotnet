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
        Workshop master, Workshop? slave, OrderDemand demand)
    {
        var board = new OrderDemandBoard(
            DemandAreaFulfillmentPolicy.OneShot,
            DemandAreaFulfillmentPolicy.OneShot);
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

    private static ResourceWorldRuntime CreateRuntime(OrderDemandBoard board, WorkshopRoster roster)
    {
        var resources = new ResourceArea(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        return new ResourceWorldRuntime(resources, roster: roster, demandBoard: board);
    }

    private sealed class ThrowingInterruptionPort : IOrderInterruptionPort
    {
        public (bool Success, string? Error) InterruptForWorkshopReplacement() =>
            throw new InvalidOperationException("模拟中断结果不确定");
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
        Assert.Empty(setup.Reservations.Find(demand.Id)?.LockedJarIds ?? System.Array.Empty<int>());
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
        runtime.AdvanceWorldTime(1_000);

        Assert.Equal(0, started.Session!.CompletedMasterSteps);
        Assert.Null(jar.Liquid);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Equal(1, reservations.Count);
        var secondReplacement = runtime.BeginReplacement(
            WorkshopRole.Master, spare, new NoopInterruptionPort(), phaseDurationMilli: 1_000);
        Assert.False(secondReplacement.Success);
        Assert.Contains("不确定", secondReplacement.Error);
        var lateStart = runtime.TryStartExecutionSession(demand.Id, book, admission, 1_000);
        Assert.False(lateStart.Success);
        Assert.Contains("结果不确定", lateStart.Reason);
    }

    [Fact]
    public void RuntimeAllowsOnlyOneActiveOrderSessionWithoutTakingASecondReservation()
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

        var resources = new ResourceArea(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        var runtime = new ResourceWorldRuntime(
            resources, roster: setup.Roster, demandBoard: setup.Board);

        var first = runtime.TryStartExecutionSession(
            firstDemand.Id, setup.Book, setup.Admission, 1_000);
        var second = runtime.TryStartExecutionSession(
            secondDemand.Id, setup.Book, setup.Admission, 1_000);

        Assert.True(first.Success, first.Reason);
        Assert.False(second.Success);
        Assert.Contains("活动订单执行会话", second.Reason);
        Assert.Single(runtime.ActiveExecutionSessions);
        Assert.Equal(1, setup.Reservations.Count);
        Assert.Null(setup.Reservations.Find(secondDemand.Id));

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
        var demand = Demand(Constants.OrderType.A, "圆罐", "酒");
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
    }

    [Fact]
    public void BOrderCompletesSlavePathDeliversJarAndReturnsTheEmptySlaveRole()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
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
}

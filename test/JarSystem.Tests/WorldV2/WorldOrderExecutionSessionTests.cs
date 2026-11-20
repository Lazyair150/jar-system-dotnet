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

        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster, 1_000);

        Assert.True(started.Success, started.Reason);
        var session = started.Session!;
        Assert.False(session.IsTerminal);
        session.AdvanceLogicalTime(999);
        Assert.Null(jar.Liquid);
        Assert.False(session.IsTerminal);

        session.AdvanceLogicalTime(1);

        Assert.True(session.Succeeded, session.Reason);
        Assert.Empty(master.Jars);
        Assert.Equal(0, master.LiquidStorage["酒"]);
        Assert.Null(setup.Board.Find(demand.Id));
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
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster, 1_000);
        Assert.True(started.Success, started.Reason);

        var resources = new ResourceArea(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        var runtime = new ResourceWorldRuntime(
            resources, roster: setup.Roster, demandBoard: setup.Board);
        Assert.True(runtime.RegisterExecutionSession(started.Session!));

        var step = runtime.AdvanceWorldTime(1_000);

        Assert.Equal(1_000, step.LogicalTimeDeltaMilli);
        Assert.True(started.Session!.Succeeded, started.Session.Reason);
        Assert.Empty(runtime.ActiveExecutionSessions);
        Assert.Empty(master.Jars);
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
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, board, book, admission, roster, 1_000);
        Assert.True(started.Success, started.Reason);

        var resources = new ResourceArea(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        var runtime = new ResourceWorldRuntime(
            resources, roster: roster, demandBoard: board);
        Assert.True(runtime.RegisterExecutionSession(started.Session!));

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
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster, 1_000);
        Assert.True(started.Success, started.Reason);

        // 模拟执行开始后外部规则/资源变化：预留是占用声明，不是对真实世界的锁。
        // 执行器必须在真正动手时重新检查领域规则。
        master.RemoveLiquid("酒", 1);
        started.Session!.AdvanceLogicalTime(1_000);

        Assert.Equal(WorldOrderExecutionStage.Failed, started.Session.Stage);
        Assert.Null(jar.Liquid);
        Assert.False(jar.IsMarked);
        Assert.Equal(0, setup.Reservations.Count);
        Assert.Equal(WorldOrderSolveState.Dirty, setup.Book.Find(demand.Id)!.State);
        Assert.True(demand.ReadyForSolve);
        Assert.Equal(0, demand.CooldownRemainingLogicalMilli);
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
        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster, 1_000);
        Assert.True(started.Success, started.Reason);

        // 准入后工作台容量发生变化，转移阶段必须重新全量预检。
        slave.ResizeJarCapacity(0);
        var session = started.Session!;
        for (var i = 0; i < 8 && !session.IsTerminal; i++)
            session.AdvanceLogicalTime(1_000);

        Assert.Equal(WorldOrderExecutionStage.Failed, session.Stage);
        Assert.Empty(slave.Jars);
        Assert.Equal(0, slave.LiquidStorage["酒"]);
        Assert.Contains(master.Jars, item => item.Id == jar.Id);
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

        var started = WorldOrderExecutionSession.TryStart(
            demand.Id, setup.Board, setup.Book, setup.Admission, setup.Roster, 1_000);
        Assert.True(started.Success, started.Reason);
        var session = started.Session!;

        // B 类可能先执行主台步骤；持续推进直到主从转移完成。
        for (var i = 0; i < 8 && !slave.HasResources && !session.IsTerminal; i++)
            session.AdvanceLogicalTime(1_000);

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

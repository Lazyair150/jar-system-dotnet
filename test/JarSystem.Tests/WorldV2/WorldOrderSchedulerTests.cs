using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using JarSystem.Core.Solver;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderSchedulerTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static OrderDemand Demand(string shape, DemandAreaKind area, SequentialIdGenerator ids) =>
        new(new JarOrderSpecification(
            new[] { new Requirement { Shape = shape } },
            Constants.OrderType.A, Words), 0, ids);

    private static (OrderDemandBoard Board, WorldOrderSolveBook Book,
        WorkshopRoster Roster, WorldResourceReservationBook Reservations,
        WorldOrderExecutionAdmission Admission, ResourceWorldRuntime Runtime,
        WorldOrderScheduler Scheduler) Prepare(
        Workshop master,
        DemandAreaFulfillmentPolicy passive = DemandAreaFulfillmentPolicy.OneShot,
        DemandAreaFulfillmentPolicy active = DemandAreaFulfillmentPolicy.OneShot)
    {
        var board = new OrderDemandBoard(passive, active);
        var roster = new WorkshopRoster(new[] { master }, master);
        var book = new WorldOrderSolveBook(board);
        var registry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        var reservations = new WorldResourceReservationBook();
        var admission = new WorldOrderExecutionAdmission(board, book, roster, reservations);
        var resources = new ResourceArea(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        var runtime = new ResourceWorldRuntime(resources, roster: roster, demandBoard: board);
        var scheduler = new WorldOrderScheduler(
            runtime, board, book, registry, admission, roster, logicalTimePerUnit: 1_000);
        return (board, book, roster, reservations, admission, runtime, scheduler);
    }

    [Fact]
    public void PassiveOrdersUseUserDefinedListOrderWhenCompetingForTheSameJar()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var setup = Prepare(master);
        var ids = new SequentialIdGenerator();
        var first = Demand("圆罐", DemandAreaKind.Passive, ids);
        var second = Demand("圆罐", DemandAreaKind.Passive, ids);
        setup.Board.AddDemand(first, DemandAreaKind.Passive);
        setup.Board.AddDemand(second, DemandAreaKind.Passive);

        var result = setup.Scheduler.RunSchedulingPass();

        Assert.Contains(first.Id, result.StartedDemandIds);
        Assert.DoesNotContain(second.Id, result.StartedDemandIds);
        Assert.Null(setup.Board.Find(first.Id));
        Assert.NotNull(setup.Board.Find(second.Id));
        Assert.Equal(WorldOrderSolveState.Current, setup.Book.Find(second.Id)!.State);
        Assert.False(setup.Book.Find(second.Id)!.Result!.IsFeasible);
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void InfeasibleEarlierPassiveOrderDoesNotBlockLaterFeasibleOrder()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var setup = Prepare(master);
        var ids = new SequentialIdGenerator();
        var impossible = Demand("方罐", DemandAreaKind.Passive, ids);
        var feasible = Demand("圆罐", DemandAreaKind.Passive, ids);
        setup.Board.AddDemand(impossible, DemandAreaKind.Passive);
        setup.Board.AddDemand(feasible, DemandAreaKind.Passive);

        var result = setup.Scheduler.RunSchedulingPass();

        Assert.DoesNotContain(impossible.Id, result.StartedDemandIds);
        Assert.Contains(feasible.Id, result.StartedDemandIds);
        Assert.NotNull(setup.Board.Find(impossible.Id));
        Assert.Null(setup.Board.Find(feasible.Id));
    }

    [Fact]
    public void ActiveOrdersAreSolvedButOnlyStartAfterExplicitUserRequest()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var setup = Prepare(master);
        var demand = Demand("圆罐", DemandAreaKind.Active, new SequentialIdGenerator());
        setup.Board.AddDemand(demand, DemandAreaKind.Active);

        var pass = setup.Scheduler.RunSchedulingPass();

        Assert.Empty(pass.StartedDemandIds);
        Assert.Equal(WorldOrderSolveState.Current, setup.Book.Find(demand.Id)!.State);
        Assert.True(setup.Book.Find(demand.Id)!.Result!.IsFeasible);
        Assert.Empty(setup.Runtime.ActiveExecutionSessions);
        Assert.NotNull(setup.Board.Find(demand.Id));

        var started = setup.Scheduler.TryStartActiveDemand(demand.Id);

        Assert.True(started.Success, started.Reason);
        Assert.True(started.Session!.Succeeded);
        Assert.Null(setup.Board.Find(demand.Id));
        Assert.Equal(0, setup.Reservations.Count);
    }

    [Fact]
    public void SchedulerRejectsActiveStartForPassiveDemand()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var setup = Prepare(master);
        var demand = Demand("圆罐", DemandAreaKind.Passive, new SequentialIdGenerator());
        setup.Board.AddDemand(demand, DemandAreaKind.Passive);

        var result = setup.Scheduler.TryStartActiveDemand(demand.Id);

        Assert.False(result.Success);
        Assert.Contains("不属于主动", result.Reason);
        Assert.NotNull(setup.Board.Find(demand.Id));
        Assert.Empty(setup.Runtime.ActiveExecutionSessions);
    }
}

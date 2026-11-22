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

public sealed class WorldOrderSolveBookTests
{
    private static readonly Vocabulary Words = new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static OrderDemand Demand(IIdGenerator? ids = null, long cooldown = 0) =>
        new(new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐" } }, Constants.OrderType.A, Words),
            cooldown, ids ?? new SequentialIdGenerator());

    private static WorldSnapshot Snapshot(params Jar[] jars)
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        foreach (var jar in jars) master.AddJar(jar);
        return WorldSnapshotFactory.Capture(
            new WorkshopRoster(new List<Workshop> { master }, master, null));
    }

    private static WorldOrderSolverRegistry Registry() =>
        new(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });

    private static OrderDemandBoard Board(
        DemandAreaFulfillmentPolicy passive = DemandAreaFulfillmentPolicy.OneShot,
        DemandAreaFulfillmentPolicy active = DemandAreaFulfillmentPolicy.OneShot) =>
        new(passive, active);

    [Fact]
    public void NewDemandIsDirtyAndSuccessfulEvaluationIsCachedForSameWorldVersion()
    {
        var board = Board();
        var demand = Demand();
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var snapshot = Snapshot(new Jar("圆罐"));

        Assert.Equal(WorldOrderSolveState.Dirty, book.Find(demand.Id)!.State);

        var first = book.Evaluate(demand.Id, snapshot, Registry());
        var second = book.Evaluate(demand.Id, snapshot, Registry());

        Assert.Equal(WorldOrderSolveState.Current, first.State);
        Assert.Equal(WorldOrderSolveStatus.Feasible, first.Result!.Status);
        Assert.Same(first, second);
        Assert.Same(first.Result, book.GetCurrentResult(demand.Id, snapshot));
    }

    [Fact]
    public void DifferentWorldVersionInvalidatesCachedResultBeforeItCanBeRead()
    {
        var board = Board();
        var demand = Demand();
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var firstSnapshot = Snapshot(new Jar("圆罐"));
        var first = book.Evaluate(demand.Id, firstSnapshot, Registry());

        var changedSnapshot = Snapshot(new Jar("圆罐"), new Jar("方罐"));

        Assert.NotEqual(firstSnapshot.Version, changedSnapshot.Version);
        Assert.Null(book.GetCurrentResult(demand.Id, changedSnapshot));
        Assert.Equal(WorldOrderSolveState.Dirty, book.Find(demand.Id)!.State);

        var second = book.Evaluate(demand.Id, changedSnapshot, Registry());
        Assert.Equal(changedSnapshot.Version, second.Result!.WorldVersion);
        Assert.NotSame(first.Result, second.Result);

        // ResourceVersion 变化会重做资源绑定/当前求解，但规则候选 Profile 仍复用。
        Assert.Equal(1, book.RuleCandidates.CacheMisses);
        Assert.True(book.RuleCandidates.CacheHits >= 1);
    }

    [Fact]
    public void RemovedDemandBecomesDetachedAndItsOldResultCannotBeRead()
    {
        var board = Board();
        var demand = Demand();
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var snapshot = Snapshot(new Jar("圆罐"));
        book.Evaluate(demand.Id, snapshot, Registry());

        Assert.True(board.RemoveDemand(demand.Id));

        Assert.Equal(WorldOrderSolveState.Detached, book.Find(demand.Id)!.State);
        Assert.Null(book.Find(demand.Id)!.Result);
        Assert.Null(book.GetCurrentResult(demand.Id, snapshot));
    }

    [Fact]
    public void MovingBetweenAreasInvalidatesButPassiveReorderingDoesNotChangeSolveResult()
    {
        var board = Board();
        var ids = new SequentialIdGenerator();
        var first = Demand(ids);
        var second = Demand(ids);
        board.AddDemand(first, DemandAreaKind.Passive);
        board.AddDemand(second, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var snapshot = Snapshot(new Jar("圆罐"));
        var evaluated = book.Evaluate(first.Id, snapshot, Registry());

        Assert.True(board.MovePassiveDemand(second.Id, 0));
        Assert.Equal(WorldOrderSolveState.Current, book.Find(first.Id)!.State);
        Assert.Same(evaluated.Result, book.GetCurrentResult(first.Id, snapshot));

        Assert.True(board.MoveToArea(first.Id, DemandAreaKind.Active));
        Assert.Equal(WorldOrderSolveState.Dirty, book.Find(first.Id)!.State);
        Assert.Null(book.Find(first.Id)!.Result);
    }

    [Fact]
    public void CooldownStartMakesTheSolveRecordNotReadyImmediately()
    {
        var board = Board();
        var demand = Demand(cooldown: 2_000);
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var snapshot = Snapshot(new Jar("圆罐"));
        book.Evaluate(demand.Id, snapshot, Registry());

        Assert.True(board.StartCooldown(demand.Id));

        Assert.Equal(WorldOrderSolveState.NotReady, book.Find(demand.Id)!.State);
        Assert.Null(book.Find(demand.Id)!.Result);
    }

    [Fact]
    public void CooldownExpiryMarksDemandDirtyAndEligibleForNewSolve()
    {
        var board = Board();
        var demand = Demand(cooldown: 2_000);
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var snapshot = Snapshot(new Jar("圆罐"));

        Assert.True(demand.EnterCooldown());
        var waiting = book.Evaluate(demand.Id, snapshot, Registry());
        Assert.Equal(WorldOrderSolveState.NotReady, waiting.State);

        var resources = new ResourceArea(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        var runtime = new ResourceWorldRuntime(resources, new WorldClock(), demandBoard: board);
        runtime.AdvanceWorldTime(2_000, master: null);

        Assert.True(demand.ReadyForSolve);
        Assert.Equal(WorldOrderSolveState.Dirty, book.Find(demand.Id)!.State);
        Assert.Equal(WorldOrderSolveState.Current,
            book.Evaluate(demand.Id, snapshot, Registry()).State);
    }

    [Fact]
    public void ContinuousFulfillmentStartsCooldownAndExpiryAutomaticallyMarksDemandDirty()
    {
        var board = Board(
            DemandAreaFulfillmentPolicy.Continuous,
            DemandAreaFulfillmentPolicy.Continuous);
        var demand = Demand(cooldown: 2_000);
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var snapshot = Snapshot(new Jar("圆罐"));
        book.Evaluate(demand.Id, snapshot, Registry());

        Assert.Equal(DemandFulfillmentResult.RetainedInArea,
            board.RecordSuccessfulFulfillment(demand.Id));
        Assert.Equal(2_000, demand.CooldownRemainingLogicalMilli);
        Assert.Equal(WorldOrderSolveState.NotReady, book.Find(demand.Id)!.State);
        Assert.Null(book.Find(demand.Id)!.Result);

        Assert.Equal(WorldOrderSolveState.NotReady,
            book.Evaluate(demand.Id, snapshot, Registry()).State);
        Assert.True(book.MarkDirty(demand.Id, "外部输入变化"));
        Assert.Equal(WorldOrderSolveState.NotReady, book.Find(demand.Id)!.State);
        book.MarkAllDirty("全局输入变化");
        Assert.Equal(WorldOrderSolveState.NotReady, book.Find(demand.Id)!.State);

        var changedSnapshot = Snapshot(new Jar("圆罐"), new Jar("方罐"));
        Assert.NotEqual(snapshot.Version, changedSnapshot.Version);
        Assert.Equal(WorldOrderSolveState.NotReady,
            book.Evaluate(demand.Id, changedSnapshot, Registry()).State);
        Assert.True(board.MoveToArea(demand.Id, DemandAreaKind.Active));
        Assert.Equal(WorldOrderSolveState.NotReady, book.Find(demand.Id)!.State);

        var resources = new ResourceArea(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        var runtime = new ResourceWorldRuntime(resources, new WorldClock(), demandBoard: board);
        runtime.AdvanceWorldTime(2_000, master: null);

        Assert.True(demand.ReadyForSolve);
        Assert.Equal(WorldOrderSolveState.Dirty, book.Find(demand.Id)!.State);
        Assert.Equal(WorldOrderSolveState.Current,
            book.Evaluate(demand.Id, snapshot, Registry()).State);
    }

    [Fact]
    public void ZeroCooldownContinuousFulfillmentIsImmediatelyDirty()
    {
        var board = Board(DemandAreaFulfillmentPolicy.Continuous);
        var demand = Demand(cooldown: 0);
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);

        Assert.Equal(DemandFulfillmentResult.RetainedInArea,
            board.RecordSuccessfulFulfillment(demand.Id));

        Assert.Equal(0, demand.CooldownRemainingLogicalMilli);
        Assert.Equal(WorldOrderSolveState.Dirty, book.Find(demand.Id)!.State);
    }

    [Fact]
    public void OneShotFulfillmentDetachesDemandFromSolveBook()
    {
        var board = Board(DemandAreaFulfillmentPolicy.OneShot);
        var demand = Demand(cooldown: 2_000);
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var snapshot = Snapshot(new Jar("圆罐"));
        book.Evaluate(demand.Id, snapshot, Registry());

        Assert.Equal(DemandFulfillmentResult.RemovedFromArea,
            board.RecordSuccessfulFulfillment(demand.Id));
        Assert.Equal(WorldOrderSolveState.Detached, book.Find(demand.Id)!.State);
        Assert.Equal(0, demand.CooldownRemainingLogicalMilli);
    }

    [Fact]
    public void ExplicitGlobalInvalidationMarksAttachedDemandsDirty()
    {
        var board = Board();
        var ids = new SequentialIdGenerator();
        var first = Demand(ids);
        var second = Demand(ids);
        board.AddDemand(first, DemandAreaKind.Passive);
        board.AddDemand(second, DemandAreaKind.Active);
        using var book = new WorldOrderSolveBook(board);
        var snapshot = Snapshot(new Jar("圆罐"));
        book.Evaluate(first.Id, snapshot, Registry());
        book.Evaluate(second.Id, snapshot, Registry());

        book.MarkAllDirty("求解规则配置变化");

        Assert.Equal(WorldOrderSolveState.Dirty, book.Find(first.Id)!.State);
        Assert.Equal(WorldOrderSolveState.Dirty, book.Find(second.Id)!.State);
    }
}

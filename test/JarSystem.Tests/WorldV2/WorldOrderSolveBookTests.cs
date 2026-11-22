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
    public void DifferentWorldVersionRebindsPlanWhenReferencedResourcesRemainValid()
    {
        var board = Board();
        var demand = Demand();
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var firstSnapshot = Snapshot(new Jar("圆罐"));
        var first = book.Evaluate(demand.Id, firstSnapshot, Registry());
        var firstSymbolic = book.GetSymbolicSolutions(demand.Id, firstSnapshot);
        Assert.NotNull(firstSymbolic);
        Assert.Equal(WorldOrderSymbolicSolutionStatus.EnumerationComplete, firstSymbolic!.Status);
        Assert.NotEmpty(firstSymbolic.Solutions);
        Assert.All(firstSymbolic.Solutions, solution =>
            Assert.All(solution.Steps, step => Assert.Equal("Master", step.WorkshopRole)));

        var changedSnapshot = Snapshot(new Jar("圆罐"), new Jar("方罐"));

        Assert.NotEqual(firstSnapshot.Version, changedSnapshot.Version);
        var rebound = book.GetCurrentResult(demand.Id, changedSnapshot);
        Assert.NotNull(rebound);
        Assert.Equal(changedSnapshot.Version, rebound!.WorldVersion);
        Assert.NotSame(first.Result, rebound);
        Assert.Same(first.Result!.Plan, rebound.Plan);
        Assert.Equal(WorldOrderSolveState.Current, book.Find(demand.Id)!.State);

        var second = book.Evaluate(demand.Id, changedSnapshot, Registry());
        Assert.Same(rebound, second.Result);

        // ResourceVersion 变化不重建规则候选 Profile，也不重跑已经验证仍然有效的计划。
        Assert.Equal(1, book.RuleCandidates.CacheMisses);
        Assert.True(book.RuleCandidates.CacheHits >= 1);
        Assert.Same(firstSymbolic, book.GetSymbolicSolutions(demand.Id, changedSnapshot));
        Assert.Equal(1, book.RuleCandidates.SolutionCacheMisses);
        Assert.True(book.RuleCandidates.SolutionCacheHits >= 1);
    }

    [Fact]
    public void SymbolicCandidatesInvalidateOnWorkshopProfileChangeButNotOnResourceChange()
    {
        var board = Board();
        var demand = Demand();
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var roster = new WorkshopRoster(new[] { master }, master);

        var firstSnapshot = WorldSnapshotFactory.Capture(roster);
        var firstRecord = book.Evaluate(demand.Id, firstSnapshot, Registry());
        var firstRecordPlan = firstRecord.Result!.Plan;
        var first = book.GetSymbolicSolutions(demand.Id, firstSnapshot);
        Assert.NotNull(first);

        master.AddLiquid("酒", 1);
        var resourceChanged = WorldSnapshotFactory.Capture(roster);
        Assert.NotEqual(firstSnapshot.ResourceVersion, resourceChanged.ResourceVersion);
        Assert.Same(first, book.GetSymbolicSolutions(demand.Id, resourceChanged));

        var resourceRebound = book.Evaluate(demand.Id, resourceChanged, Registry());
        Assert.Equal(WorldOrderSolveState.Current, resourceRebound.State);
        Assert.Equal(resourceChanged.Version, resourceRebound.Result!.WorldVersion);
        Assert.Same(firstRecordPlan, resourceRebound.Result.Plan);

        master.ReplaceCapability(new WorkshopCapability(
            new[] { "方罐" }, System.Array.Empty<string>(), System.Array.Empty<string>()));
        var profileChanged = WorldSnapshotFactory.Capture(roster);
        Assert.NotEqual(resourceChanged.WorkshopProfileVersion, profileChanged.WorkshopProfileVersion);
        Assert.Null(book.GetSymbolicSolutions(demand.Id, profileChanged));

        book.Evaluate(demand.Id, profileChanged, Registry());
        var rebuilt = book.GetSymbolicSolutions(demand.Id, profileChanged);
        Assert.NotNull(rebuilt);
        Assert.NotSame(first, rebuilt);

        master.Rules.Stack.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Reason = "运行时规则禁止匹配圆罐"
        });
        var ruleChanged = WorldSnapshotFactory.Capture(roster);
        Assert.NotEqual(profileChanged.RuleSetVersion, ruleChanged.RuleSetVersion);
        Assert.Null(book.GetSymbolicSolutions(demand.Id, ruleChanged));

        book.Evaluate(demand.Id, ruleChanged, Registry());
        var ruleRebuilt = book.GetSymbolicSolutions(demand.Id, ruleChanged);
        Assert.NotNull(ruleRebuilt);
        Assert.NotSame(rebuilt, ruleRebuilt);
        Assert.DoesNotContain(
            ruleRebuilt!.Solutions.SelectMany(solution => solution.Steps),
            step => step.Candidate.Operation == "MATCH");
    }

    [Fact]
    public void ResourceRebindFallsBackToSolverWhenAPlanJarIsReplaced()
    {
        var board = Board();
        var demand = Demand();
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var original = new Jar("圆罐");
        master.AddJar(original);
        var roster = new WorkshopRoster(new[] { master }, master);

        var firstSnapshot = WorldSnapshotFactory.Capture(roster);
        var first = book.Evaluate(demand.Id, firstSnapshot, Registry());
        Assert.Equal(WorldOrderSolveStatus.Feasible, first.Result!.Status);

        Assert.Same(original, master.RemoveJar(original));
        var replacement = new Jar("圆罐");
        Assert.True(master.AddJar(replacement));
        var changedSnapshot = WorldSnapshotFactory.Capture(roster);

        var rebound = book.Evaluate(demand.Id, changedSnapshot, Registry());

        Assert.Equal(WorldOrderSolveStatus.Feasible, rebound.Result!.Status);
        Assert.Equal(changedSnapshot.Version, rebound.Result.WorldVersion);
        Assert.NotSame(first.Result.Plan, rebound.Result.Plan);
        Assert.Contains(rebound.Result.Plan!.MasterSteps,
            step => step.Operation?.Type == "MATCH" &&
                    step.Before![step.Operation.JarIndex!.Value].Id == replacement.Id);
    }

    [Fact]
    public void ResourceRebindRejectsChangedFactsOnSameJarIdentity()
    {
        var board = Board();
        var demand = Demand();
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var jar = new Jar("圆罐");
        master.AddJar(jar);
        var roster = new WorkshopRoster(new[] { master }, master);

        var firstSnapshot = WorldSnapshotFactory.Capture(roster);
        var first = book.Evaluate(demand.Id, firstSnapshot, Registry());
        Assert.Equal(WorldOrderSolveStatus.Feasible, first.Result!.Status);
        Assert.Equal("MATCH", first.Result.Plan!.MasterSteps.Single().Operation!.Type);

        Assert.True(master.PourIn(jar, "酒").Success);
        Assert.True(master.ClearMark(jar).Success);
        var changedSnapshot = WorldSnapshotFactory.Capture(roster);
        var second = book.Evaluate(demand.Id, changedSnapshot, Registry());

        Assert.Equal(WorldOrderSolveStatus.Feasible, second.Result!.Status);
        Assert.NotSame(first.Result.Plan, second.Result.Plan);
        Assert.Contains(second.Result.Plan!.MasterSteps,
            step => step.Operation?.Type == "POUR_OUT" &&
                    step.Before[step.Operation.JarIndex!.Value].Id == jar.Id);
    }

    [Fact]
    public void ResourceRebindFallsBackToSolverWhenRequiredLiquidSupplyDisappears()
    {
        var board = Board();
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());
        board.AddDemand(demand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var roster = new WorkshopRoster(new[] { master }, master);

        var supplied = WorldSnapshotFactory.Capture(
            roster, new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 }));
        var first = book.Evaluate(demand.Id, supplied, Registry());
        Assert.Equal(WorldOrderSolveStatus.Feasible, first.Result!.Status);

        var depleted = WorldSnapshotFactory.Capture(
            roster, new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 0 }));
        var second = book.Evaluate(demand.Id, depleted, Registry());

        Assert.Equal(WorldOrderSolveStatus.Infeasible, second.Result!.Status);
        Assert.NotSame(first.Result.Plan, second.Result.Plan);
        Assert.Equal(depleted.Version, second.Result.WorldVersion);
    }

    [Fact]
    public void BOrderSymbolicCandidatesIncludeBothRolesWhileAOrderBypassesSlave()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台", new List<string> { "圆罐" });
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var board = Board();
        var bDemand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐" } },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());
        board.AddDemand(bDemand, DemandAreaKind.Passive);
        using var book = new WorldOrderSolveBook(board);
        var snapshot = WorldSnapshotFactory.Capture(roster);

        book.Evaluate(bDemand.Id, snapshot, Registry());
        var bCandidates = book.GetSymbolicSolutions(bDemand.Id, snapshot);
        Assert.NotNull(bCandidates);
        Assert.Contains(bCandidates!.Solutions.SelectMany(x => x.Steps),
            step => step.WorkshopRole == "Master");
        Assert.Contains(bCandidates.Solutions.SelectMany(x => x.Steps),
            step => step.WorkshopRole == "Slave");

        var aDemand = Demand();
        var aBoard = Board();
        aBoard.AddDemand(aDemand, DemandAreaKind.Passive);
        using var aBook = new WorldOrderSolveBook(aBoard);
        aBook.Evaluate(aDemand.Id, snapshot, Registry());
        var aCandidates = aBook.GetSymbolicSolutions(aDemand.Id, snapshot);
        Assert.NotNull(aCandidates);
        Assert.All(aCandidates!.Solutions.SelectMany(x => x.Steps),
            step => Assert.Equal("Master", step.WorkshopRole));
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

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

public sealed class WorldV2ContextTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static WorldOrderSolverRegistry CreateSolverRegistry() =>
        new(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });

    private static ResourceArea CreateResources() =>
        new(
            new ProducerModule(Words),
            new LiquidProduction(Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);

    private static Workshop CreateMaster()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        return master;
    }

    private static OrderDemand CreateDemand(long cooldownDurationLogicalMilli = 0) =>
        new(new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐" } },
            Constants.OrderType.A, Words),
            cooldownDurationLogicalMilli,
            ids: new SequentialIdGenerator());

    [Fact]
    public void CreateBuildsOneConsistentWorldGraphAndSchedulerCanUseIt()
    {
        var master = CreateMaster();
        using var world = WorldV2Context.Create(
            CreateResources(),
            new[] { master },
            master,
            slave: null,
            solverRegistry: CreateSolverRegistry(),
            logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous);

        Assert.Same(master, world.Roster.Master);
        Assert.Same(world.Roster, world.Runtime.Roster);
        Assert.Same(world.DemandBoard, world.Runtime.DemandBoard);
        Assert.Same(world.Resources, world.Runtime.Resources);
        Assert.Equal(DemandAreaFulfillmentPolicy.OneShot, world.DemandBoard.Passive.FulfillmentPolicy);
        Assert.Equal(DemandAreaFulfillmentPolicy.Continuous, world.DemandBoard.Active.FulfillmentPolicy);

        var demand = CreateDemand();
        world.DemandBoard.AddDemand(demand, DemandAreaKind.Passive);

        var result = world.Scheduler.RunSchedulingPass();

        Assert.Contains(demand.Id, result.StartedDemandIds);
        Assert.Null(world.DemandBoard.Find(demand.Id));
        Assert.Equal(0, world.Reservations.Count);
    }

    [Fact]
    public void SeparateCreateCallsNeverShareMutableWorldComponents()
    {
        var firstMaster = CreateMaster();
        var secondMaster = CreateMaster();
        var first = WorldV2Context.Create(
            CreateResources(), new[] { firstMaster }, firstMaster, null,
            CreateSolverRegistry(), logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous);
        var second = WorldV2Context.Create(
            CreateResources(), new[] { secondMaster }, secondMaster, null,
            CreateSolverRegistry(), logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot);

        try
        {
            Assert.NotSame(first.Roster, second.Roster);
            Assert.NotSame(first.DemandBoard, second.DemandBoard);
            Assert.NotSame(first.SolveBook, second.SolveBook);
            Assert.NotSame(first.Reservations, second.Reservations);
            Assert.NotSame(first.Runtime, second.Runtime);
            Assert.NotSame(first.Scheduler, second.Scheduler);
            Assert.Equal(DemandAreaFulfillmentPolicy.OneShot, first.DemandBoard.Passive.FulfillmentPolicy);
            Assert.Equal(DemandAreaFulfillmentPolicy.Continuous, first.DemandBoard.Active.FulfillmentPolicy);
            Assert.Equal(DemandAreaFulfillmentPolicy.Continuous, second.DemandBoard.Passive.FulfillmentPolicy);
            Assert.Equal(DemandAreaFulfillmentPolicy.OneShot, second.DemandBoard.Active.FulfillmentPolicy);

            first.Dispose();

            // Continuous 成功后是否进入等待取决于订单自身 CD 时长。
            // 这里使用非零 CD，才能验证保留订单且暂时不可再次求解。
            var demand = CreateDemand(cooldownDurationLogicalMilli: 1_000);
            second.DemandBoard.AddDemand(demand, DemandAreaKind.Passive);
            var result = second.Scheduler.RunSchedulingPass();

            Assert.Contains(demand.Id, result.StartedDemandIds);
            // 第二个世界的被动区明确配置为 Continuous：成功后应保留订单并启动 CD，
            // 而不是像第一个世界的 OneShot 区域一样卸载订单。
            Assert.Same(demand, second.DemandBoard.Find(demand.Id));
            Assert.False(demand.ReadyForSolve);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void CreateRejectsInvalidLogicalTimeScale()
    {
        var master = CreateMaster();

        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            WorldV2Context.Create(
                CreateResources(), new[] { master }, master, null,
                CreateSolverRegistry(), logicalTimePerUnit: 0,
                passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
                activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot));
    }
}

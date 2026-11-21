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
    public void ExecutionResourceCheckpointCapturesDetachedLiquidScopesReservationsLedgerClockAndSessionProgress()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 });
        using var world = WorldV2Context.Create(
            CreateResources(), new[] { master }, master, null,
            CreateSolverRegistry(), logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: ship);

        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());
        world.DemandBoard.AddDemand(demand, DemandAreaKind.Active);
        var started = world.Scheduler.TryStartActiveDemand(demand.Id);
        Assert.True(started.Success, started.Reason);

        var first = world.CaptureExecutionResourceCheckpoint();
        Assert.Equal(0, first.ShipLiquidInventory.Balances.Values.Sum());
        Assert.Single(first.MasterWorkshop.Jars);
        Assert.Equal(master.Jars[0].Id, first.MasterWorkshop.Jars[0].Id);
        Assert.Null(first.SlaveWorkshop);
        Assert.Equal(1, first.Admission.MasterStaging!.Accounts[demand.Id]["酒"]);
        Assert.Empty(first.Admission.SlaveStaging?.Accounts ?? new Dictionary<int, IReadOnlyDictionary<string, int>>());
        Assert.Single(first.Admission.ActiveOrders);
        Assert.False(first.Admission.ActiveOrders[0].UsesSlave);
        Assert.True(first.Admission.ActiveOrders[0].MasterCapacityReserved);
        Assert.False(first.Admission.ActiveOrders[0].SlaveCapacityReserved);
        Assert.Single(first.Reservations.Reservations);
        Assert.Single(first.Sessions);
        Assert.Equal(WorldOrderExecutionStage.MasterSteps, first.Sessions[0].Stage);
        Assert.Equal(0, first.Sessions[0].UnitElapsedLogicalMilli);
        Assert.Contains(first.ResourceLedger.Entries, item =>
            item.Cause == ResourceCauses.Stock && item.From == "关卡初始库存");
        Assert.Contains(first.ResourceLedger.Entries, item =>
            item.Cause == ResourceCauses.Flow && item.From == "自机液库" && item.To == "主台");

        world.Runtime.AdvanceWorldTime(500);
        var second = world.CaptureExecutionResourceCheckpoint();

        Assert.Equal(0, first.Sessions[0].UnitElapsedLogicalMilli);
        Assert.Equal(500, second.Sessions[0].UnitElapsedLogicalMilli);
        Assert.Equal(0, first.Clock.WorldTimeMilli);
        Assert.Equal(500, second.Clock.WorldTimeMilli);
        Assert.Equal(1, first.Admission.MasterStaging!.Accounts[demand.Id]["酒"]);
    }

    [Fact]
    public void ContextRestoresActiveOrderWithoutRepeatingFundingOrExecutionSteps()
    {
        var firstMaster = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        firstMaster.AddJar(new Jar("圆罐", id: 41, vocabulary: Words));
        var firstSlave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        var firstShip = new ShipLiquidInventory(4,
            new Dictionary<string, int> { ["酒"] = 2 });
        using var firstWorld = WorldV2Context.Create(
            CreateResources(), new[] { firstMaster, firstSlave }, firstMaster, firstSlave,
            CreateSolverRegistry(), logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: firstShip);
        var firstDemand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());
        firstWorld.DemandBoard.AddDemand(firstDemand, DemandAreaKind.Active);
        var started = firstWorld.Scheduler.TryStartActiveDemand(firstDemand.Id);
        Assert.True(started.Success, started.Reason);
        firstWorld.Runtime.AdvanceWorldTime(500);

        var checkpoint = firstWorld.CaptureExecutionResourceCheckpoint();
        Assert.Equal(1, checkpoint.ShipLiquidInventory.Balances["酒"]);
        Assert.Null(checkpoint.Admission.SlaveStaging);
        Assert.False(Assert.Single(checkpoint.Admission.ActiveOrders).UsesSlave);
        Assert.Equal(500, Assert.Single(checkpoint.Sessions).UnitElapsedLogicalMilli);
        Assert.Single(checkpoint.ResourceLedger.Entries, item => item.Cause == ResourceCauses.Flow);

        var secondMaster = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        secondMaster.AddJar(new Jar("圆罐", id: 41, vocabulary: Words));
        var secondSlave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        var secondShip = new ShipLiquidInventory(4);
        using var secondWorld = WorldV2Context.Create(
            CreateResources(), new[] { secondMaster, secondSlave }, secondMaster, secondSlave,
            CreateSolverRegistry(), logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: secondShip);
        var restoredDemand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());
        Assert.Equal(firstDemand.Id, restoredDemand.Id);
        secondWorld.DemandBoard.AddDemand(restoredDemand, DemandAreaKind.Active);

        var invalidClockCheckpoint = new WorldV2ExecutionResourceCheckpoint(
            checkpoint.ResourceArea, checkpoint.DemandBoard, checkpoint.ConfigurationFingerprint, checkpoint.ShipLiquidInventory,
            checkpoint.MasterWorkshop, checkpoint.SlaveWorkshop,
            checkpoint.Admission, checkpoint.Reservations, checkpoint.ResourceLedger,
            new WorldClockSnapshot(-1, checkpoint.Clock.LogicalTimeMilli,
                checkpoint.Clock.SpeedRatioMilli, checkpoint.Clock.IsPaused,
                checkpoint.Clock.ConversionRemainder),
            checkpoint.RuntimeLedgerTick, checkpoint.Sessions);
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            secondWorld.RestoreExecutionResourceCheckpoint(invalidClockCheckpoint));
        Assert.Equal(0, secondShip.GetAmount("酒"));
        Assert.Single(secondMaster.Jars);
        Assert.Equal(0, secondWorld.Reservations.Count);
        Assert.Empty(secondWorld.Runtime.ActiveExecutionSessions);

        secondWorld.RestoreExecutionResourceCheckpoint(checkpoint);

        Assert.Equal(1, secondShip.GetAmount("酒"));
        Assert.Equal(1, secondWorld.Admission.GetLiquidScopes(restoredDemand.Id)!.Value.Master.GetAmount("酒"));
        var restoredSession = Assert.Single(secondWorld.Runtime.ActiveExecutionSessions);
        Assert.Equal(500, restoredSession.UnitElapsedLogicalMilli);
        Assert.Equal(checkpoint.ResourceLedger.LastSeq, secondWorld.ResourceLedger.LastSeq);

        secondWorld.Runtime.AdvanceWorldTime(500);

        Assert.Empty(secondWorld.Runtime.ActiveExecutionSessions);
        Assert.Empty(secondWorld.Roster.Master!.Jars);
        Assert.Equal(1, secondShip.GetAmount("酒"));
        Assert.Single(secondWorld.ResourceLedger.For(ResourceCauses.Flow));
    }

    [Fact]
    public void BOrderCheckpointAfterMasterToSlavePumpRestoresWithoutRepumpingOrRequiringReleasedMasterQuota()
    {
        var firstMaster = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        firstMaster.AddJar(new Jar("圆罐", id: 51, vocabulary: Words));
        firstMaster.AddLiquid("酒", 1); // 兼容求解输入；新订单供给只从自机液库扣除
        var firstSlave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        var firstShip = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 2 });
        using var firstWorld = WorldV2Context.Create(
            CreateResources(), new[] { firstMaster, firstSlave }, firstMaster, firstSlave,
            CreateSolverRegistry(), logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: firstShip);
        var firstDemand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());
        firstWorld.DemandBoard.AddDemand(firstDemand, DemandAreaKind.Active);
        var start = firstWorld.Scheduler.TryStartActiveDemand(firstDemand.Id);
        Assert.True(start.Success, start.Reason);

        var firstSession = Assert.Single(firstWorld.Runtime.ActiveExecutionSessions);
        for (var i = 0; i < 8 && firstSession.Stage != WorldOrderExecutionStage.SlaveSteps; i++)
            firstWorld.Runtime.AdvanceWorldTime(1_000);
        Assert.Equal(WorldOrderExecutionStage.SlaveSteps, firstSession.Stage);
        Assert.True(firstSlave.HasResources);

        var checkpoint = firstWorld.CaptureExecutionResourceCheckpoint();
        var activeScope = Assert.Single(checkpoint.Admission.ActiveOrders);
        Assert.True(activeScope.UsesSlave);
        Assert.False(activeScope.MasterCapacityReserved);
        Assert.True(activeScope.SlaveCapacityReserved);
        Assert.Empty(checkpoint.Admission.MasterStaging!.ReservedCapacityByOrder);
        Assert.Contains(firstDemand.Id, checkpoint.Admission.SlaveStaging!.ReservedCapacityByOrder.Keys);
        Assert.Equal(1, checkpoint.Admission.SlaveStaging.Accounts[firstDemand.Id]["酒"]);
        Assert.Equal(1, checkpoint.ShipLiquidInventory.Balances["酒"]);
        Assert.Single(checkpoint.ResourceLedger.Entries, item => item.Cause == ResourceCauses.Flow);
        Assert.Single(checkpoint.ResourceLedger.Entries, item =>
            item.Cause == ResourceCauses.Transfer && item.Kind == ResourceKinds.Liquid);

        var secondMaster = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        secondMaster.AddJar(new Jar("圆罐", id: 51, vocabulary: Words));
        secondMaster.AddLiquid("酒", 1);
        var secondSlave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        var secondShip = new ShipLiquidInventory(4);
        using var secondWorld = WorldV2Context.Create(
            CreateResources(), new[] { secondMaster, secondSlave }, secondMaster, secondSlave,
            CreateSolverRegistry(), logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: secondShip);
        var restoredDemand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());
        Assert.Equal(firstDemand.Id, restoredDemand.Id);
        secondWorld.DemandBoard.AddDemand(restoredDemand, DemandAreaKind.Active);

        secondWorld.RestoreExecutionResourceCheckpoint(checkpoint);

        Assert.Equal(1, secondShip.GetAmount("酒"));
        Assert.Equal(1, secondWorld.Admission.GetLiquidScopes(restoredDemand.Id)!.Value.Slave!.GetAmount("酒"));
        var restoredSession = Assert.Single(secondWorld.Runtime.ActiveExecutionSessions);
        Assert.Equal(WorldOrderExecutionStage.SlaveSteps, restoredSession.Stage);
        Assert.True(restoredSession.CaptureProgress().SlaveTransferCommitted);

        for (var i = 0; i < 8 && secondWorld.Runtime.ActiveExecutionSessions.Count > 0; i++)
            secondWorld.Runtime.AdvanceWorldTime(1_000);

        Assert.Empty(secondWorld.Runtime.ActiveExecutionSessions);
        Assert.Empty(secondSlave.Jars);
        Assert.Equal(1, secondShip.GetAmount("酒"));
        Assert.Single(secondWorld.ResourceLedger.For(ResourceCauses.Flow));
        Assert.Single(secondWorld.ResourceLedger.For(ResourceCauses.Transfer),
            item => item.Kind == ResourceKinds.Liquid);
        Assert.DoesNotContain(secondWorld.ResourceLedger.For(ResourceCauses.Return),
            item => item.Kind == ResourceKinds.Liquid);
    }

    [Fact]
    public void ExecutionCheckpointPreservesProductionRemaindersWarehouseAndFlowQuota()
    {
        static ResourceArea CreateProductionArea(ShipLiquidInventory ship)
        {
            var producers = new ProducerModule(Words);
            producers.RegisterProducer("罐源", new Dictionary<string, double> { ["圆罐"] = 0.5 });
            var liquidProduction = new LiquidProduction(
                Words,
                defaults: new Dictionary<string, double> { ["酒"] = 0.5, ["蜜"] = 0 });
            return new ResourceArea(
                producers, liquidProduction,
                jarFlowRate: 0.5, liquidFlowRate: 0,
                shipLiquidInventory: ship);
        }

        var firstMaster = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        var firstShip = new ShipLiquidInventory(10);
        var firstResources = CreateProductionArea(firstShip);
        firstResources.Inventory.StoreJars(new[] { new Jar("圆罐", vocabulary: Words) });
        using var firstWorld = WorldV2Context.Create(
            firstResources, new[] { firstMaster }, firstMaster, null,
            CreateSolverRegistry(), logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: firstShip);

        firstWorld.Runtime.AdvanceWorldTime(500);
        var checkpoint = firstWorld.CaptureExecutionResourceCheckpoint();
        Assert.Single(checkpoint.ResourceArea.Warehouse.Jars);
        Assert.Equal(0.25, checkpoint.ResourceArea.Producers.GeneratorAccumulatedMicro["圆罐"] / 1_000_000d);
        Assert.Equal(0.25, checkpoint.ResourceArea.LiquidProduction.AccumulatedMicro["酒"] / 1_000_000d);
        Assert.Equal(250_000, checkpoint.ResourceArea.JarFlowAccumulator);

        var secondMaster = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words, liquidStagingCapacity: 4);
        var secondShip = new ShipLiquidInventory(10);
        var secondResources = CreateProductionArea(secondShip);
        // 恢复不仅要补余数，也要恢复运行中被修改过的速率与流入配置。
        secondResources.Producers.UpdateRate("罐源", "圆罐", 1.0);
        secondResources.LiquidProduction.SetRate("酒", 1.0);
        secondResources.SetFlowRates(jarFlowRate: 1.0, liquidFlowRate: 0);
        using var secondWorld = WorldV2Context.Create(
            secondResources, new[] { secondMaster }, secondMaster, null,
            CreateSolverRegistry(), logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: secondShip);

        secondWorld.RestoreExecutionResourceCheckpoint(checkpoint);
        Assert.Single(secondResources.Inventory.Jars);

        secondWorld.Runtime.AdvanceWorldTime(1_500);

        Assert.Single(secondMaster.Jars);
        Assert.Single(secondResources.Inventory.Jars);
        Assert.Equal(1, secondShip.GetAmount("酒"));
        Assert.Equal(2_000, secondWorld.Runtime.Clock.WorldTimeMilli);
        Assert.Equal(0, secondResources.Producers.Generators["圆罐"].Accumulated);
        Assert.Equal(0, secondResources.LiquidProduction.Accumulated["酒"]);
    }

    [Fact]
    public void WorldClockSnapshotPreservesFixedPointRemainderAcrossRestore()
    {
        var original = new WorldClock();
        original.SetSpeedRatio(0.333);
        Assert.Equal(0, original.Advance(1));
        var snapshot = original.Capture();
        var restored = WorldClock.Restore(snapshot);

        Assert.Equal(original.Advance(2), restored.Advance(2));
        Assert.Equal(original.WorldTimeMilli, restored.WorldTimeMilli);
        Assert.Equal(original.LogicalTimeMilli, restored.LogicalTimeMilli);
        Assert.Equal(original.Capture().ConversionRemainder,
            restored.Capture().ConversionRemainder);
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

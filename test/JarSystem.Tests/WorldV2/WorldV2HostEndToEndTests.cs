using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using Xunit;

namespace JarSystem.Tests.WorldV2;

/// <summary>
/// 从 Host 创建世界开始，验证生产、流入、订单求解、准入、计时执行和结算能够形成一条真实链路。
/// 该测试不经过旧 JarSystem、Web 宿主或 Godot 旧模块。
/// </summary>
public sealed class WorldV2HostEndToEndTests
{
    private static WorldV2Options Options() => new()
    {
        Shapes = new List<string> { "圆罐", "方罐" },
        Liquids = new List<string> { "酒", "蜜" },
        Producers = new List<WorldV2ProducerOptions>
        {
            new()
            {
                Name = "陶工",
                Rates = new Dictionary<string, double> { ["圆罐"] = 1.0 }
            }
        },
        LiquidProductionRates = new Dictionary<string, double>
        {
            ["酒"] = 0,
            ["蜜"] = 0
        },
        InitialLiquidStock = new Dictionary<string, int> { ["酒"] = 1 },
        JarFlowRatePerLogicalTime = 1,
        LiquidFlowRatePerLogicalTime = 1,
        Workshops = new List<WorldV2WorkshopOptions>
        {
            new()
            {
                Name = "主台",
                BaseMarks = new List<string> { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10
            }
        },
        MasterWorkshopName = "主台",
        ExecutionLogicalTimePerUnit = 1000,
        PassiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot,
        ActiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot
    };

    [Fact]
    public void HostRunsACompleteWorldV2ProductionToTimedOrderSettlementFlow()
    {
        using var host = new WorldV2HostService();
        var options = Options();
        options.Producers[0].ProducerInstanceId = "producer-e2e";
        options.Workshops[0].OwnedInstanceId = "workshop-e2e";
        var worldId = new WorldInstanceId("world-ledger-e2e");
        var ownerMap = new WorldV2OwnerIdentityMap(
            worldId, "ship-e2e", "board-e2e", "production-e2e", "warehouse-e2e",
            new[] { "producer-e2e" }, new[] { "workshop-e2e" });
        host.Replace(options, worldId, ownerMap);

        var producedJars = 0;
        var flowedJars = 0;
        var flowedWine = 0;
        var shipWineAfterProduction = -1;
        var startedDemand = false;
        var activeSessionsBeforeCompletion = -1;
        var activeSessionsAfterCompletion = -1;
        var remainingDemands = -1;
        var remainingReservations = -1;
        var runtimeFaulted = true;
        var logicalTime = -1L;
        ResourceEvent[] resourceEvents = System.Array.Empty<ResourceEvent>();

        host.ExecuteExclusive(context =>
        {
            // 1. Host 驱动 Scheduler；Runtime 推进逻辑时间、生产和资源流入。
            var productionStep = context.Scheduler.AdvanceWorldAndSchedule(1_000);
            producedJars = productionStep.WorldStep.Resources.ProducedJarCount;
            flowedJars = productionStep.WorldStep.Resources.FlowedJarCount;
            flowedWine = productionStep.WorldStep.Resources.FlowedLiquids.GetValueOrDefault("酒");
            shipWineAfterProduction = context.ShipLiquidInventory!.GetAmount("酒");

            // 2. 外部输入只表达订单规格与区域，不直接调用 Solver 或执行器。
            var vocabulary = new Vocabulary(
                new[] { "圆罐", "方罐" },
                new[] { "酒", "蜜" });
            var demand = new OrderDemand(
                new JarOrderSpecification(
                    new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                    Constants.OrderType.A,
                    vocabulary),
                cooldownDurationLogicalMilli: 0,
                ids: new SequentialIdGenerator());
            context.DemandBoard.AddDemand(demand, DemandAreaKind.Passive);

            // 3. Scheduler 共享快照、求解、执行准入并登记计时执行会话。
            var pass = context.Scheduler.RunSchedulingPass();
            startedDemand = pass.StartedDemandIds.Contains(demand.Id);
            activeSessionsBeforeCompletion = context.Runtime.ActiveExecutionSessions.Count;

            // 4. 再推进一个执行时间单位，执行 POUR_IN，完成订单并释放资源预留。
            context.Scheduler.AdvanceWorldAndSchedule(1_000);
            activeSessionsAfterCompletion = context.Runtime.ActiveExecutionSessions.Count;
            remainingDemands = context.DemandBoard.Count;
            remainingReservations = context.Reservations.Count;
            runtimeFaulted = context.Runtime.IsFaulted;
            logicalTime = context.Runtime.Clock.LogicalTimeMilli;
            resourceEvents = context.ResourceLedger.Entries.ToArray();
        });

        Assert.Equal(1, producedJars);
        Assert.Equal(1, flowedJars);
        Assert.Equal(0, flowedWine);
        Assert.Equal(1, shipWineAfterProduction);
        Assert.True(startedDemand);
        Assert.Equal(1, activeSessionsBeforeCompletion);
        Assert.Equal(0, activeSessionsAfterCompletion);
        Assert.Equal(0, remainingDemands);
        Assert.Equal(0, remainingReservations);
        Assert.False(runtimeFaulted);
        Assert.Equal(2_000, logicalTime);
        Assert.Contains(resourceEvents, item =>
            item.Cause == ResourceCauses.Stock && item.Kind == ResourceKinds.Liquid &&
            item.Name == "酒" && item.From == "关卡初始库存" && item.To == "自机液库" &&
            item.Amount == 1);
        Assert.Contains(resourceEvents, item =>
            item.Cause == ResourceCauses.Flow && item.Kind == ResourceKinds.Liquid &&
            item.Name == "酒" && item.From == "自机液库" && item.To == "主台" &&
            item.Amount == 1);
        Assert.DoesNotContain(resourceEvents, item => item.Cause == ResourceCauses.Transfer);

        var stockEvent = Assert.Single(resourceEvents, item => item.Cause == ResourceCauses.Stock);
        Assert.Equal(ResourceEndpointKind.ExternalSource, stockEvent.FromEndpoint.Kind);
        Assert.Equal("level.initial-stock", stockEvent.FromEndpoint.ExternalIdentity);
        Assert.Equal(ResourceEndpointKind.Account, stockEvent.ToEndpoint.Kind);
        var stockTarget = AccountIdentity.ParseV2(stockEvent.ToEndpoint.AccountId!);
        Assert.Equal(ResourceOwnerKind.Ship, stockTarget.Owner.OwnerKind);
        Assert.Equal("ship-e2e", stockTarget.Owner.LocalOwnerId);

        var productionJars = resourceEvents
            .Where(item => item.Cause == ResourceCauses.Production && item.Kind == ResourceKinds.Jar)
            .ToArray();
        Assert.NotEmpty(productionJars);
        Assert.All(productionJars, productionJar =>
        {
            Assert.Equal(ResourceOwnerKind.ProductionArea,
                AccountIdentity.ParseV2(productionJar.FromEndpoint.AccountId!).Owner.OwnerKind);
            Assert.Equal(ResourceOwnerKind.Warehouse,
                AccountIdentity.ParseV2(productionJar.ToEndpoint.AccountId!).Owner.OwnerKind);
        });

        var flowedJar = Assert.Single(resourceEvents, item =>
            item.Cause == ResourceCauses.Flow && item.Kind == ResourceKinds.Jar);
        Assert.Equal("warehouse-e2e", AccountIdentity.ParseV2(flowedJar.FromEndpoint.AccountId!).Owner.LocalOwnerId);
        Assert.Equal("workshop-e2e", AccountIdentity.ParseV2(flowedJar.ToEndpoint.AccountId!).Owner.LocalOwnerId);

        // Host 操作结束后，仍可通过下一次独占操作读取同一世界的脱离式状态。
        var worldStillAvailable = false;
        host.ExecuteExclusive(context =>
            worldStillAvailable = context.Roster.Master != null && !context.Runtime.IsFaulted);
        Assert.True(worldStillAvailable);
    }
}

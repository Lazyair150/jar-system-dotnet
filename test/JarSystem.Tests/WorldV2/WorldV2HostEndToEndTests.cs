using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using Xunit;

namespace JarSystem.Tests.WorldV2;

/// <summary>
/// 从 Host 创建世界开始，验证生产、流入、订单求解、准入、执行和结算能够形成一条真实链路。
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
        InitialLiquidStock = new Dictionary<string, int>(),
        JarFlowRatePerLogicalTime = 1,
        LiquidFlowRatePerLogicalTime = 0,
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
    public void HostRunsACompleteWorldV2ProductionToOrderSettlementFlow()
    {
        using var host = new WorldV2HostService();
        host.Replace(Options());

        var producedJars = 0;
        var flowedJars = 0;
        var startedDemand = false;
        var remainingDemands = -1;
        var remainingReservations = -1;
        var runtimeFaulted = true;
        var logicalTime = -1L;

        host.ExecuteExclusive(context =>
        {
            // 1. 宿主推进世界时间；Runtime 推进生产和资源流入。
            var step = context.Scheduler.AdvanceWorldAndSchedule(1_000);
            producedJars = step.WorldStep.Resources.ProducedJarCount;
            flowedJars = step.WorldStep.Resources.FlowedJarCount;

            // 2. 外部输入只表达订单规格与所在区域，不直接调用 Solver。
            var vocabulary = new Vocabulary(
                new[] { "圆罐", "方罐" },
                new[] { "酒", "蜜" });
            var demand = new OrderDemand(
                new JarOrderSpecification(
                    new[] { new Requirement { Shape = "圆罐" } },
                    Constants.OrderType.A,
                    vocabulary),
                cooldownDurationLogicalMilli: 0,
                ids: new SequentialIdGenerator());
            context.DemandBoard.AddDemand(demand, DemandAreaKind.Passive);

            // 3. Scheduler 负责共享快照、求解、执行准入和启动会话。
            var pass = context.Scheduler.RunSchedulingPass();
            startedDemand = pass.StartedDemandIds.Contains(demand.Id);

            // 4. 一次性订单完成后从需求板移除，资源预留释放。
            remainingDemands = context.DemandBoard.Count;
            remainingReservations = context.Reservations.Count;
            runtimeFaulted = context.Runtime.IsFaulted;
            logicalTime = context.Runtime.Clock.LogicalTimeMilli;
        });

        Assert.Equal(1, producedJars);
        Assert.Equal(1, flowedJars);
        Assert.True(startedDemand);
        Assert.Equal(0, remainingDemands);
        Assert.Equal(0, remainingReservations);
        Assert.False(runtimeFaulted);
        Assert.Equal(1_000, logicalTime);

        // Host 操作结束后，仍可通过下一次独占操作读取同一世界的脱离式状态。
        var worldStillAvailable = false;
        host.ExecuteExclusive(context =>
            worldStillAvailable = context.Roster.Master != null && !context.Runtime.IsFaulted);
        Assert.True(worldStillAvailable);
    }
}

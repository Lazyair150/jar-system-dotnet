using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Orders;
using JarSystem.Core.Services;
using Xunit;
using JarSystem.Core.Domain;
using JarSystem.Core.Abstractions;

namespace JarSystem.Tests;

/// <summary>
/// 第一个真实反应器的行为测试：**世界资源一变，待定订单的可行性缓存就不再成立**。
///
/// 它守的是一条能造成真实损害的路径：
/// <c>CheckFeasibility</c> 把计划存进 <see cref="Order.FeasibilityResult"/>，
/// <c>ExecuteOrder</c> 会照着那份计划执行（`OrderService.cs:167-168`）。
/// 而那份计划不记录"算于哪一版工作台" —— 换台之后照着它执行，动的是不再成立的动作。
/// </summary>
public class FeasibilityInvalidationTests
{
    [Fact]
    public void ResourceChange_InvalidatesCachedFeasibility_OnNextTick()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);

        var result = system.CheckFeasibility(0);
        Assert.NotNull(result);
        Assert.NotNull(system.Orders[0].FeasibilityResult);

        // 能力变更：新增一个工作台会动到世界的可选资源。
        system.CreateWorkshop("附加台", new List<string> { "圆罐" }, null, null);

        // **延迟是设计的一部分**：信号分发时世界还在"改到一半"，所以此刻不动缓存。
        Assert.NotNull(system.Orders[0].FeasibilityResult);

        // 下一个 tick 的第 ① 步统一执行排队的反应。
        system.Tick();

        Assert.Null(system.Orders[0].FeasibilityResult);
    }

    [Fact]
    public void SelectionOnly_DoesNotInvalidate()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.SetSlaveIndex(1);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);
        system.CheckFeasibility(0);
        Assert.NotNull(system.Orders[0].FeasibilityResult);

        // 只改"选哪个台"不动任何台的形状/禁令/罐位 —— 白名单里明确不触发。
        system.SetMasterIndex(1);
        system.SetSlaveIndex(0);
        system.Tick();

        Assert.NotNull(system.Orders[0].FeasibilityResult);
    }

    [Fact]
    public void RepeatedTicks_DoNotThrashTheCache()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);
        system.CheckFeasibility(0);

        // 没有任何资源变更时，单纯推进 tick 不应把缓存清掉：
        // 否则"每 tick 都重算"会把判定成本摊到每一刻，而判定最坏是秒级的。
        system.Tick(3);

        Assert.NotNull(system.Orders[0].FeasibilityResult);
    }

    private static JarSystem.Core.JarSystem NewSystem() => new(
        randomSeed: 17,
        jarIds: new SequentialIdGenerator(),
        orderIds: new SequentialIdGenerator());
}

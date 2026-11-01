using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Orders;
using JarSystem.Core.Services;
using Xunit;
using JarSystem.Core.Domain;
using JarSystem.Core.Abstractions;

namespace JarSystem.Tests;

/// <summary>
/// 可行性结论的**复用**：世界内容没变就不重算。
///
/// 这条缓存最初用 `JarSystem.WorldRevision` 当键，那是个错误 ——
/// `Tick` 每次都会记一条 `WorldOps.Tick`（`JarSystem.cs:683`），所以修订号每 tick 必增，
/// 而默认 2 秒一 tick、自动检测 5 秒一次 ⇒ 缓存永远不命中。
/// 现在用**内容指纹**（主从台选择 + 标记/禁令 + 罐子集合 + 液池）。
/// </summary>
public class FeasibilityReuseTests
{
    [Fact]
    public void UnchangedWorld_ReusesTheSameResultObject()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);

        var first = system.CheckAllFeasibility();
        var second = system.CheckAllFeasibility();

        Assert.Single(first);
        Assert.NotNull(first[0].Result);
        // 同一个对象 ⇒ 确实没有重算（重算会生成新的 FeasibilityResultData）。
        Assert.Same(first[0].Result, second[0].Result);
    }

    [Fact]
    public void ChangedCapability_Recomputes()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);

        var first = system.CheckAllFeasibility();

        // 新增一台工作台会改变"从台可选项"，也就改变了判定的输入。
        system.CreateWorkshop("附加台", new List<string> { "圆罐" }, null, null);

        var second = system.CheckAllFeasibility();

        Assert.NotNull(first[0].Result);
        Assert.NotSame(first[0].Result, second[0].Result);
    }

    [Fact]
    public void ChangedSelection_Recomputes()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);

        var first = system.CheckAllFeasibility();
        system.SetMasterIndex(1);
        var second = system.CheckAllFeasibility();

        Assert.NotSame(first[0].Result, second[0].Result);
    }

    private static JarSystem.Core.JarSystem NewSystem() => new(
        randomSeed: 31,
        jarIds: new SequentialIdGenerator(),
        orderIds: new SequentialIdGenerator());
}

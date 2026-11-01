using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Orders;
using JarSystem.Core.Services;
using Xunit;
using JarSystem.Core.Domain;
using JarSystem.Core.Abstractions;

namespace JarSystem.Tests;

/// <summary>
/// **工作台迁徙过程**：换台不再是一次瞬间的世界跳变，而是玩家主动发起、一 tick 一段的迁徙。
///
/// 这组测试守三件事：
/// <list type="number">
/// <item>迁徙**是真过程**：中途主台指针不变，跑完才变；</item>
/// <item>它**不是订单**：不产生 `orderEvent`，走的是 `workshop_migration` 这条过程线；</item>
/// <item>没有资源可搬时**不起过程**（不让人白等两个 tick）。</item>
/// </list>
/// </summary>
public class SwitchProcessTests
{
    [Fact]
    public void Migration_TakesTicks_AndOnlyCommitsAtTheEnd()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.Tick(30);                     // 让产线把罐子/液体送进主台（否则没有可搬的东西）

        var (started, error, processId) = system.BeginSwitch(1);
        Assert.True(started, error);
        Assert.NotNull(processId);

        // **发起瞬间不能切换**：这是一个过程，不是一次跳变。
        Assert.Equal(0, system.CurrentMaster);

        system.Tick(30);

        // 过程跑完后指针才过去。
        Assert.Equal(1, system.CurrentMaster);
    }

    [Fact]
    public void Migration_IsNotAnOrder()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.Tick(30);

        var before = system.EventHistory.Count;
        var (started, error, _) = system.BeginSwitch(1);
        Assert.True(started, error);
        system.Tick(30);

        // 事件历史里只有 workshop_switch，没有任何 orderEvent。
        var added = new List<string>();
        for (int i = before; i < system.EventHistory.Count; i++)
            added.Add(system.EventHistory[i].GetValueOrDefault("type")?.ToString() ?? "");

        Assert.DoesNotContain("orderEvent", added);
        Assert.Contains("workshop_switch", added);
    }

    [Fact]
    public void Migration_WithoutResources_SwitchesImmediately()
    {
        var system = NewSystem();
        // 新建一台（空台）并切过去 —— 它身上没有任何罐子或液体。
        system.CreateWorkshop("空台", new List<string> { "圆罐" }, null, null);
        var empty = system.Workshops.Count - 1;
        system.SetMasterIndex(empty);

        var (started, error, processId) = system.BeginSwitch(0);
        Assert.True(started, error);

        // 没有可搬运的东西 ⇒ 不起过程，直接改指针（不让人白等两个 tick）。
        Assert.Null(processId);
        Assert.Equal(0, system.CurrentMaster);
    }

    [Fact]
    public void Migration_ToCurrentMaster_IsRejected()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        var (started, error, _) = system.BeginSwitch(0);
        Assert.False(started);
        Assert.NotNull(error);
    }

    [Fact]
    public void Migration_IsRejected_WhileAnOrderProcessRuns()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.Tick(30);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);

        var started = system.StartOrder(0);
        Assert.True(started.Started, started.Error);

        // 迁徙改的是工作台配置；订单执行以工作台为前提 ⇒ 两者互斥。
        var (ok, error, processId) = system.BeginSwitch(1);
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Null(processId);
    }

    [Fact]
    public void OrderProcess_IsRejected_WhileMigrationRuns()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.Tick(30);

        var (started, error, _) = system.BeginSwitch(1);
        Assert.True(started, error);

        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);
        var start = system.StartOrder(0);

        // 互斥的另一半：配置正在变，不能基于它起新的订单计划。
        Assert.False(start.Started);
        Assert.NotNull(start.Error);
    }

    [Fact]
    public void CancellingMigration_RollsBackAndKeepsMaster()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.Tick(30);

        var before = Shapes(system, 0);
        var (started, error, processId) = system.BeginSwitch(1);
        Assert.True(started, error);
        Assert.NotNull(processId);

        system.Tick(2);                                  // 让第一段（重组）跑起来
        var cancelled = system.CancelProcess(processId!.Value, "测试取消");
        Assert.True(cancelled.Cancelled, cancelled.Error);

        // 取消 ≠ 提交：主台指针不动，且旧台的罐子必须回到迁徙前的形状。
        // 取消路径是 `Cancel → MarkFailed → OnFinished → Abort`，若不回滚，
        // 世界会停在一个"改了一半"的工作台上。
        Assert.Equal(0, system.CurrentMaster);
        Assert.Equal(before, Shapes(system, 0));
    }

    private static List<string> Shapes(JarSystem.Core.JarSystem system, int workshopIndex)
        => system.Workshops[workshopIndex].Jars.Select(j => j.Shape + ":" + (j.Liquid ?? "-")).OrderBy(x => x).ToList();

    private static JarSystem.Core.JarSystem NewSystem() => new(
        randomSeed: 41,
        jarIds: new SequentialIdGenerator(),
        orderIds: new SequentialIdGenerator());
}

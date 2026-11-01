using System;
using System.Collections.Generic;
using System.Text.Json;
using JarSystem.Core;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Orders;
using JarSystem.Core.Services;
using JarSystem.Core.Sessions;
using JarSystem.Core.World;
using Xunit;
using JarSystem.Core.Domain;

namespace JarSystem.Tests;

/// <summary>
/// 世界信息总线的契约测试。
///
/// 这批测试守的是**总线自己的三条承诺**，而不是某个玩法的行为：
/// <list type="number">
/// <item>信号按注册顺序同步送达，且带 tick + revision（否则订阅方无法在回放里复现决策）；</item>
/// <item>反应器请求的"后续世界变化"**不当场执行**，而是排进下一个 tick 的批
///       —— 这是级联可复现的全部依据；</item>
/// <item>**没有订阅者时总线不改变世界任何一步**（回放字节不变的机器化检验）。</item>
/// </list>
/// </summary>
public class WorldBusTests
{
    private sealed class RecordingObserver : IWorldObserver
    {
        public List<WorldSignal> Seen { get; } = new();
        public void OnSignal(in WorldSignal signal) => Seen.Add(signal);
    }

    private sealed class OrderRecordingObserver : IWorldObserver
    {
        private readonly List<string> _log;
        private readonly string _tag;

        public OrderRecordingObserver(List<string> log, string tag)
        {
            _log = log;
            _tag = tag;
        }

        public void OnSignal(in WorldSignal signal) => _log.Add(_tag + ":" + signal.Op);
    }

    /// <summary>一个只会请求"一条后续变化"的反应器。</summary>
    private sealed class EnqueueOnceReactor : IWorldReactor
    {
        public int Calls { get; private set; }

        public void React(in WorldSignal signal, IWorldEffectSink sink)
        {
            Calls++;
            sink.Enqueue("demo", "演示用效果", () => { });
        }
    }

    [Fact]
    public void Publish_ReachesObserversInRegistrationOrder()
    {
        var bus = new WorldBus();
        var log = new List<string>();
        bus.Subscribe(new OrderRecordingObserver(log, "first"));
        bus.Subscribe(new OrderRecordingObserver(log, "second"));

        bus.Publish(new WorldSignal(WorldSignalKind.WorldChanged, "probe", null, null, tick: 3, revision: 7));

        // 顺序即语义：订阅顺序决定通知顺序，不能并发化。
        Assert.Equal(new[] { "first:probe", "second:probe" }, log);
        Assert.Equal(1, bus.Published);
    }

    [Fact]
    public void Signal_CarriesTickAndRevision()
    {
        var bus = new WorldBus();
        var observer = new RecordingObserver();
        bus.Subscribe(observer);

        bus.Publish(new WorldSignal(WorldSignalKind.WorldChanged, "probe", "玄武台", 2, tick: 11, revision: 42));

        var signal = Assert.Single(observer.Seen);
        Assert.Equal(11, signal.Tick);
        Assert.Equal(42, signal.Revision);
        Assert.Equal("玄武台", signal.Subject);
        Assert.Equal(2, signal.SubjectId);
    }

    [Fact]
    public void ReactorEffects_AreDeferredAndTakenExactlyOnce()
    {
        var bus = new WorldBus();
        var reactor = new EnqueueOnceReactor();
        bus.AttachReactor(reactor);

        bus.Publish(new WorldSignal(WorldSignalKind.WorldChanged, "probe", null, null, 0, 1));

        // 关键断言：请求了效果，但**还没有执行** —— 它只是在队里等着。
        Assert.Equal(1, reactor.Calls);
        Assert.Equal(1, bus.PendingCount);

        var firstBatch = bus.DrainForTick(1);
        Assert.Single(firstBatch);

        // 取走即清空：同一条效果不会被第二个 tick 再执行一次。
        Assert.Empty(bus.DrainForTick(2));
        Assert.Equal(0, bus.PendingCount);
    }

    [Fact]
    public void EffectQueue_IsBoundedAndReportsRejection()
    {
        var bus = new WorldBus();

        for (var i = 0; i < WorldBus.MaxEffectsPerTick; i++)
            Assert.True(bus.Enqueue("fill", "占位", () => { }));

        // 超限后拒收并计数，而不是抛异常或无限增长 —— 级联必须死在明处。
        Assert.False(bus.Enqueue("overflow", "超出上限", () => { }));
        Assert.Equal(1, bus.DroppedEffects);
        Assert.Equal(WorldBus.MaxEffectsPerTick, bus.PendingCount);
    }

    [Fact]
    public void ResetPending_DropsEffectsButKeepsSubscribers()
    {
        var bus = new WorldBus();
        var observer = new RecordingObserver();
        bus.Subscribe(observer);
        bus.Enqueue("stale", "旧世界遗留", () => { });

        bus.ResetPending();

        Assert.Equal(0, bus.PendingCount);
        Assert.True(bus.HasObservers);

        bus.Publish(new WorldSignal(WorldSignalKind.WorldChanged, "after-reset", null, null, 0, 1));
        Assert.Single(observer.Seen);
    }

    [Fact]
    public void JarSystem_PublishesWorldChangedForEveryIntent()
    {
        var system = NewSystem();
        var observer = new RecordingObserver();
        system.Bus.Subscribe(observer);

        system.CreateWorkshop("测试台", new List<string> { "圆罐" }, null, null);

        Assert.Contains(observer.Seen, s => s.Kind == WorldSignalKind.WorldChanged && s.Op == WorldOps.CreateWorkshop);
        Assert.All(observer.Seen, s => Assert.True(s.Revision > 0));
    }

    [Fact]
    public void JarSystem_PublishesProcessChangedForOrderLifecycle()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);

        var observer = new RecordingObserver();
        system.Bus.Subscribe(observer);

        system.StartOrder(0);
        system.Tick();

        var processSignals = observer.Seen.FindAll(s => s.Kind == WorldSignalKind.ProcessChanged);
        Assert.NotEmpty(processSignals);
        Assert.Contains(processSignals, s => s.Op == "started");
    }

    /// <summary>
    /// **最重要的一条**：接入总线不得改变世界的走向。
    ///
    /// 两个种子与 ID 源完全相同、命令序列完全相同的系统，一个挂着若干订阅者（含反应器），
    /// 另一个什么都不挂 —— 它们的状态快照、修订号、逻辑 tick 必须**逐字节相同**。
    /// 这条测试就是"回放资产不会因为总线存在而变化"的机器化版本。
    /// </summary>
    [Fact]
    public void Subscribers_DoNotChangeWorldOutcome()
    {
        var plain = RunScript(withSubscribers: false);
        var wired = RunScript(withSubscribers: true);

        Assert.Equal(plain, wired);
    }

    private static string RunScript(bool withSubscribers)
    {
        var system = NewSystem();

        if (withSubscribers)
        {
            system.Bus.Subscribe(new RecordingObserver());
            system.Bus.AttachReactor(new EnqueueOnceReactor());
        }

        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);
        system.StartOrder(0);
        system.Tick(3);
        system.CheckFeasibility(0);

        // ⚠ timestamp 是**墙钟耗时**（FeasibilityResultData.Timestamp，单位毫秒），
        // 它天然逐次不同，与总线无关。比对前把它抹平 —— 否则这条测试测的是性能抖动，不是确定性。
        var snapshot = System.Text.RegularExpressions.Regex.Replace(
            JsonSerializer.Serialize(system.GetState()),
            "\"timestamp\":\"[^\"]*\"",
            "\"timestamp\":\"<volatile>\"");

        return snapshot
            + "|rev=" + system.WorldRevision
            + "|tick=" + system.Processes.Clock.Tick;
    }

    private static JarSystem.Core.JarSystem NewSystem() => new(
        randomSeed: 17,
        jarIds: new SequentialIdGenerator(),
        orderIds: new SequentialIdGenerator());
}

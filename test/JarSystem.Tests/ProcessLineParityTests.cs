using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Orders;
using JarSystem.Core.Process;
using JarSystem.Core.Services;
using JarSystem.Core.World;
using Xunit;
using JarSystem.Core.Domain;
using JarSystem.Core.Abstractions;

namespace JarSystem.Tests;

/// <summary>
/// **"三支线打通"的可验证形态**：过程线的每一次状态变化，必须**同时**到达宿主的观察者槽位
/// 和世界总线，且 reason 与 tick 完全一致。
///
/// 为什么用"并行送达"而不是"把 <c>Observer</c> 删掉改走总线"：
/// <see cref="IProcessObserver.OnChanged"/> 拿到的是**活的** <see cref="ProcessInstance"/>，
/// Web 壳（`src/JarSystem.Web/ProcessObserver.cs`）要从它身上读 10 个字段才能组消息
/// （Id / Kind / Subject / DeclaredSteps / DeadlineTick / AppliedSteps / BlockedReason /
/// GraceTicks / Outcome / FailureReason）。而 `WorldSignal` 是值类型、只带
/// (Kind, Op, Subject, SubjectId, Tick, Revision)。
/// 如果让 Web 壳改走总线，它就得按 SubjectId 回去查实例 —— 而
/// `ProcessRun.Close` 的顺序是「先 notify Finished（`ProcessRun.cs:163`）再 onClosed → 进最近表
/// （`ProcessSystem.cs:266`）」，能不能查到取决于恰好站在哪一刻。**那是把一条信息完整的通路
/// 换成一条靠时序碰运气的通路。** 所以这里守的是"不丢线"，不是"只留一条线"。
/// </summary>
public class ProcessLineParityTests
{
    [Fact]
    public void EveryProcessChange_ReachesBothObserverAndBus()
    {
        var system = new JarSystem.Core.JarSystem(
            randomSeed: 23,
            jarIds: new SequentialIdGenerator(),
            orderIds: new SequentialIdGenerator());

        var observerSeen = new List<(string Reason, int Tick)>();
        system.Processes.Observer = new Recorder(observerSeen);

        var busSeen = new List<(string Reason, int Tick)>();
        system.Bus.Subscribe(new BusRecorder(busSeen));

        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);

        var started = system.StartOrder(0);
        Assert.True(started.Started, started.Error);

        system.Tick(20);

        Assert.NotEmpty(observerSeen);

        // 比对口径：**同一条事件集合**（reason + tick 的多重集）。
        // 刻意不比对顺序 —— 两个槽位是同一次回调里的相邻两行，顺序必然一致，
        // 但把"顺序"写进断言会让这条测试在将来某次合理的重排上假报警；
        // 它守的是"线路不丢事件"，不是"事件按什么顺序排队"。
        Assert.Equal(
            observerSeen.OrderBy(x => x.Reason, StringComparer.Ordinal).ToList(),
            busSeen.OrderBy(x => x.Reason, StringComparer.Ordinal).ToList());
    }

    private sealed class Recorder : IProcessObserver
    {
        private readonly List<(string, int)> _sink;
        public Recorder(List<(string, int)> sink) => _sink = sink;

        public void OnChanged(ProcessInstance instance, string reason, int tick) => _sink.Add((reason, tick));
    }

    private sealed class BusRecorder : IWorldObserver
    {
        private readonly List<(string, int)> _sink;
        public BusRecorder(List<(string, int)> sink) => _sink = sink;

        public void OnSignal(in WorldSignal signal)
        {
            if (signal.Kind == WorldSignalKind.ProcessChanged) _sink.Add((signal.Op, signal.Tick));
        }
    }
}

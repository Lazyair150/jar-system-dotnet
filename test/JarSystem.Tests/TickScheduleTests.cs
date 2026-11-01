using System;
using System.Collections.Generic;
using JarSystem.Core;
using JarSystem.Core.Process;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// tick 调度器的契约测试。
///
/// 这批测试守的是**"把一个 tick 里发生什么做成一张可枚举的表"这件事的代价**：
/// 引擎可以随便加模块、随便调顺序，因为它不要求逐字节回放；我们要求，所以三条纪律必须可验证：
/// <list type="number">
/// <item>执行次序只由「阶段 + Order」决定，与注册先后无关；</item>
/// <item>同阶段 Order 冲突**当场报错**，不静默；</item>
/// <item>表在时间开始流动的那一刻封盘，之后注册一律拒绝。</item>
/// </list>
/// 外加两条结构性证据：核心的五个步骤真的都在表里；流入只跑在调用边界、不跑每 tick。
/// </summary>
public class TickScheduleTests
{
    private sealed class Probe : ITickHandler
    {
        private readonly List<string> _log;
        private readonly string _label;

        public Probe(TickPhase phase, int order, string name, List<string> log, string label)
        {
            Phase = phase;
            Order = order;
            Name = name;
            _log = log;
            _label = label;
        }

        public TickPhase Phase { get; }
        public int Order { get; }
        public string Name { get; }

        public void OnTick(in TickContext context) => _log.Add(_label + "@" + context.Tick);
    }

    [Fact]
    public void Scheduler_RunsByPhaseThenOrder_NotByRegistrationOrder()
    {
        var log = new List<string>();
        var sched = new WorldScheduler();

        // 故意倒着注册：先流入、再产出、再过程、再修饰器、最后事件
        sched.Register(new Probe(TickPhase.Flow, 0, "flow", log, "flow"));
        sched.Register(new Probe(TickPhase.Production, 0, "prod", log, "prod"));
        sched.Register(new Probe(TickPhase.Processes, 5, "proc-b", log, "procB"));
        sched.Register(new Probe(TickPhase.Processes, 1, "proc-a", log, "procA"));
        sched.Register(new Probe(TickPhase.Modifiers, 0, "mods", log, "mods"));
        sched.Register(new Probe(TickPhase.Events, 0, "events", log, "events"));

        sched.RunTick(7);

        // 每 tick 的阶段只跑 ①~④；Flow 不在其中
        Assert.Equal(new[] { "events@7", "mods@7", "procA@7", "procB@7", "prod@7" }, log);

        log.Clear();
        sched.RunCallBoundary(7);
        Assert.Equal(new[] { "flow@7" }, log);
    }

    [Fact]
    public void Scheduler_RejectsDuplicateOrderInSamePhase()
    {
        var log = new List<string>();
        var sched = new WorldScheduler();
        sched.Register(new Probe(TickPhase.Processes, 0, "first", log, "a"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => sched.Register(new Probe(TickPhase.Processes, 0, "second", log, "b")));

        // 报错必须指名道姓：不然"为什么我的模块不生效"会变成一场猜谜
        Assert.Contains("first", ex.Message, StringComparison.Ordinal);
        Assert.Contains("second", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scheduler_SameOrderInDifferentPhasesIsAllowed()
    {
        var log = new List<string>();
        var sched = new WorldScheduler();

        sched.Register(new Probe(TickPhase.Events, 0, "e", log, "e"));
        sched.Register(new Probe(TickPhase.Production, 0, "p", log, "p"));   // 不冲突：阶段不同

        sched.RunTick(1);
        Assert.Equal(new[] { "e@1", "p@1" }, log);
    }

    [Fact]
    public void Scheduler_SealsAfterFirstTick()
    {
        var log = new List<string>();
        var sched = new WorldScheduler();
        sched.Register(new Probe(TickPhase.Events, 0, "e", log, "e"));

        Assert.False(sched.IsSealed);
        sched.RunTick(1);
        Assert.True(sched.IsSealed);

        // 时间已经流动 ⇒ 再注册会让同一份命令日志在不同会话里重放出不同世界
        Assert.Throws<InvalidOperationException>(
            () => sched.Register(new Probe(TickPhase.Events, 1, "late", log, "late")));
    }

    [Fact]
    public void Scheduler_ExposesTheFiveCoreSteps()
    {
        var sys = new JarSystem.Core.JarSystem();
        var handlers = sys.Processes.Pipeline.Scheduler.Handlers;

        // 心跳从"散落在 TickPipeline + JarSystem 的调用"变成"可枚举的表"：
        // 事件 / 修饰器 / 过程 / 产出 / 流入
        Assert.Equal(5, handlers.Count);
        Assert.Contains(handlers, h => h.Phase == TickPhase.Events && h.Name == "events");
        Assert.Contains(handlers, h => h.Phase == TickPhase.Modifiers && h.Name == "modifiers");
        Assert.Contains(handlers, h => h.Phase == TickPhase.Processes && h.Name == "processes");
        Assert.Contains(handlers, h => h.Phase == TickPhase.Production && h.Name == "worldProduction");
        Assert.Contains(handlers, h => h.Phase == TickPhase.Flow && h.Name == "worldFlow");
    }

    [Fact]
    public void Scheduler_CoreScheduleIsSealedAfterFirstTick()
    {
        var sys = new JarSystem.Core.JarSystem();
        sys.Tick(1);

        Assert.True(sys.Processes.Pipeline.Scheduler.IsSealed);
        Assert.Throws<InvalidOperationException>(() =>
            sys.Processes.Pipeline.Scheduler.Register(
                new DelegateTickHandler(TickPhase.Production, 99, "tooLate", (in TickContext _) => { })));
    }

    [Fact]
    public void Scheduler_FlowRunsOncePerCallNotPerTick()
    {
        // 这条钉住的是既有语义（ProductionService 类注释原文）：
        // 「每个 tick 一次 TickStep、每次宿主 Tick 调用一次 FlushFlow」。
        // 流入是"调用边界"阶段，不是"每 tick"阶段 —— 混进每 tick 会改变 Tick(10) 与 Tick(1)×10 的关系。
        var sys = new JarSystem.Core.JarSystem();
        var flow = 0;
        var prod = 0;

        var sched = sys.Processes.Pipeline.Scheduler;
        // 用新阶段位注册观测器（Order 1 排在既有 Order 0 之后）
        sched.Register(new DelegateTickHandler(TickPhase.Production, 1, "probeProd",
            (in TickContext _) => prod++));
        sched.Register(new DelegateTickHandler(TickPhase.Flow, 1, "probeFlow",
            (in TickContext _) => flow++));

        sys.Tick(4);

        Assert.Equal(4, prod);
        Assert.Equal(1, flow);
    }
}

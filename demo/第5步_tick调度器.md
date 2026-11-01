# tick 调度器：把"一个 tick 里发生什么"做成一张表

## 0 一句话

引擎自带时间流逝，模块只要实现 `process()` 就会被调用。
这套系统能学这一半——**实现 `ITickHandler` 并注册**；另一半（核心自己拥有时间推进）永远不做，因为那会杀掉回放。

## 1 改造前：执行者硬编码在两个地方

```csharp
// Process/ProcessSystem.cs:40
Pipeline = new TickPipeline(_clock, _modifiers, DrainEvents, AdvanceActive);
```

```csharp
// JarSystem.Tick（改造前）
for (int i = 0; i < count; i++)
{
    Processes.Pipeline.AdvanceOneTick();   // 时钟 + ①事件 ②修饰器 ③过程
    _productionService.TickStep();          // ④产出
}
_productionService.FlushFlow();             // ⑤流入（每次调用一次）
```

五个执行者，两个调用点。**新增一个时间驱动模块，必须改这两处。**

## 2 能做的与不能做的

| | 引擎 | 我们 |
|---|---|---|
| 时间的所有权 | 引擎 `while(true)` | **宿主**（`JarSystem.Tick(count)`） |
| 参与者的注册 | 遍历所有模块 | **可以做**（本文） |
| 顺序 | 通常不定 | **必须钉死** |
| 逐字节回放 | 不要求 | **要求** |

前两列解释了为什么"学一半"不是妥协：引擎能随便加模块、随便调顺序，**因为它不要求逐字节回放**。我们要，所以顺序必须进代码。

`dt` 也不能有。引擎的 `process(float dt)` 里 `dt` 是真实秒数；我们的 `process()` 参数恒为"一步"。
`Production/ProducerModule.cs` 的 `_accumulatedMilli` 是定点整数累加，起因是实测到 `0.20000000000000018` 导致 `Math.Floor` 少产一个罐——引入 `dt` 等于把那次修复作废。

## 3 结构

```
WorldScheduler
 ├─ TickPhase.Events     0  ← events            （ProcessSystem.DrainEvents）
 ├─ TickPhase.Modifiers  1  ← modifiers         （ModifierStack.Expire）
 ├─ TickPhase.Processes  2  ← processes         （ProcessSystem.AdvanceActive）
 ├─ TickPhase.Production 3  ← worldProduction   （ProductionService.TickStep）
 └─ TickPhase.Flow       4  ← worldFlow         （ProductionService.FlushFlow）
```

两个入口，因为两类时机不同：

```csharp
Pipeline.AdvanceOneTick();   // 时钟 +1 → RunTick：跑 Events/Modifiers/Processes/Production
Pipeline.RunCallBoundary();  // 只跑 Flow
```

`Flow` 单独成口不是我发明的，是既有语义：
`Services/ProductionService.cs:13-14` 的类注释原文——「每个 tick 一次 TickStep、每次宿主 Tick 调用一次 FlushFlow」。
以前它散落在 `JarSystem.Tick` 的一行调用里，现在它有了名字。**这个区别以前是隐式的，现在是显式的。**

## 4 三条注册纪律（违反任一条，回放就会分叉）

1. **注册必须在时间开始流动之前**。第一次 `RunTick` 之后表封盘（`IsSealed`），再注册抛 `InvalidOperationException`。
   理由：同一份命令日志在不同会话里要重放出同一个世界，处理器集合就必须相同。
2. **同阶段内 `Order` 必须唯一**。冲突在注册时报错，且**指名道姓**两个处理器。
   不静默按注册顺序——调用序/哈希序在这里就是不确定性本身。
3. **处理器不得改世界**。只能读世界 + 写自己的状态。要改世界必须经 `WorldBus` 排进下一个 tick 的 `Events` 阶段。

`TickContext` 只有一个字段 `Tick`。没有 `dt`，也不持有 `JarSystem`——**这两个"没有"都是刻意的**。

## 5 接入方式

```csharp
public sealed class 我的模块 : ITickHandler
{
    public TickPhase Phase => TickPhase.Production;
    public int Order => 10;          // 同阶段内排第几
    public string Name => "myModule";
    public void OnTick(in TickContext ctx) { /* 世界走了一步 */ }
}

// 第一个 tick 之前
sys.Processes.Pipeline.Scheduler.Register(new 我的模块());
```

或者用委托，不改类：

```csharp
sys.Processes.Pipeline.Scheduler.Register(new DelegateTickHandler(
    TickPhase.Production, 10, "myModule", (in TickContext ctx) => { ... }));
```

## 6 验收：唯一的判据是字节

改造是**纯抽取**，不是重写。证明方式不是我说等价，是门禁那一行：

```
REPLAY-OK (commands=33, stateBytes=3125, logBytes=8797, journalBytes=725)   ← 改造前后逐字相同
```

字节不变 ⇒ tick 内的调用序列与今天等价；字节变了 ⇒ 我动了行为，不能声称等价。

## 7 测试（`test/JarSystem.Tests/TickScheduleTests.cs`，7 条）

| 测试 | 守什么 |
|---|---|
| `Scheduler_RunsByPhaseThenOrder_NotByRegistrationOrder` | 次序只由阶段+Order 决定，倒着注册也按序执行 |
| `Scheduler_RejectsDuplicateOrderInSamePhase` | 冲突当场报错，且报错点名两个处理器 |
| `Scheduler_SameOrderInDifferentPhasesIsAllowed` | 阶段不同则 Order 可重，不误杀 |
| `Scheduler_SealsAfterFirstTick` | 封盘后拒绝注册 |
| `Scheduler_ExposesTheFiveCoreSteps` | 核心五步真的都在表里（心跳可枚举） |
| `Scheduler_CoreScheduleIsSealedAfterFirstTick` | 真系统的表在第一个 tick 后确实封了 |
| `Scheduler_FlowRunsOncePerCallNotPerTick` | `Tick(4)` ⇒ 产出 4 次、流入 1 次 |

## 8 门禁

```
JarSystem.Tests  Total: 59, Failed: 0        （52 → 59）
=== 0 bugs / 2000 random + 0 bugs / 3000 capacity + 32 deterministic ===
PROTOCOL-OK (commands=21, messages=16)
SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)
MODDIFF-OK (scenarios=400, checks=1600, passthrough=1, liquid=1, inexpressible=1, wholeShape=1)
REPLAY-OK (commands=33, stateBytes=3125, logBytes=8797, journalBytes=725)
QUALITY-OK
VERDICT=OK
```

## 9 刻意没做的

1. **调度器不认识"时间到了没有"**。它不持有时钟、不起线程、不看墙钟。"什么时候跑一次"由 `TickPipeline` 决定。
2. **没有动态注册/注销**。游戏过程中增删处理器 = 让回放分叉。需要"某模块这段时间不参与"的话，正确做法是模块自己读 `IsTimePaused` 或世界状态后 early-return，不是改表。
3. **没有做"阶段优先级可配置"**。阶段顺序是确定性的一部分，不是调参项。
4. **`FlushFlow` 仍然在调用边界而不是每 tick**。移进去会改变 `Tick(10)` 与 `Tick(1)×10` 的等价关系——那是重基线，不是修 bug。这个语义含糊记在 `第4步_统一时间系统.md` §9 与本文件 §3，仍未决。

# Phase 0：接口与契约规格（冻结稿）

> 生成时间：2026-10-16　生成者：Codex
> 性质：**可评审的规格**。本文档不包含任何代码改动；Phase 1 才动代码。
> 依据：`设计分析_过程系统内核.md`（设计与实施规格）；本项目既有约定（`[JsonPropertyName]` 钉死键名、集合只读包装、`DomainValidationException` 区分规则拒绝与编程错误、契约三件产物自动生成）。
> 本文档中的 **✅ 冻结** 表示已按既有约定定稿；**⏳ 待裁决** 表示需要你点头（见 §11）。

---

## §1 术语 → 代码标识符对照（冻结）

| 术语 | 代码标识符 | 说明 |
|---|---|---|
| 过程 | `Process` | 抽象概念与命名空间前缀 |
| 过程定义 | `ProcessDefinition` | 不可变模板（含 `ProcessPlan`） |
| 过程计划 | `ProcessPlan` | 步骤序列，**不可变**；来源：求解器路径 / 贪心计划 / 未来生成器 |
| 过程步骤 | `ProcessStep` | 最小可执行单元（语义与既有 `SolverStepDto` 对齐，但**不是同一个类型**） |
| 过程实例 | `ProcessInstance` | 运行时状态；变更只经意图方法 |
| 过程状态 | `ProcessState` | 见 §2 取值表 |
| 过程结尾 | `ProcessOutcome` | `succeeded` / `failed` / `aborted`；null = 未终结 |
| 逻辑时钟 | `LogicalClock` | 世界推进的唯一权威 |
| 步间隔 | `StepInterval` | 一步占用几个 tick（0 = 瞬发） |
| 修饰器 | `IRuleModifier` / `ModifierStack` | 区间内叠加/抑制规则 |
| 规则上下文 | `RuleContext` | 基础法则 + 活跃修饰器；**唯一的规则读取口** |
| 守护 / 前置条件 | `PreconditionChecker` / `GuardResult` | 执行前校验 |
| 过程日志 | `ProcessJournal` / `JournalEntry` | append-only |
| 里程碑 | `Milestone` | Journal 的特殊条目 |
| 过程门面 | `ProcessSystem` | 宿主唯一入口 |

---

## §2 状态机（冻结）

### 2.1 取值（字符串即契约取值，风格对齐既有 `single_phase` / `same_bans` / `master`）

```
ProcessState   = created | ready | running | blocked | succeeded | failed | aborted
ProcessOutcome = succeeded | failed | aborted          （null = 未终结）
ProcessKind    = order_execution | workshop_migration  （扩展须在本表登记）
PlanSource     = solver | greedy | mixed | manual      （**仅进 Journal，不下发**）
```

### 2.2 迁移表

| 当前态 | 事件 | 目标态 | 副作用 |
|---|---|---|---|
| `created` | `PlanAccepted`（计划非空） | `ready` | Journal: `plan_accepted` |
| `created` | `PlanAccepted`（计划为空） | — | **抛** `InvalidOperationException`（无计划不得开始） |
| `ready` | `Started` | `running` | `startedAtTick = clock.Tick`；Journal: `started` |
| `running` | `StepCommitted`（还有剩余步） | `running` | `currentStep++`；Journal: `step_applied` |
| `running` | `StepCommitted`（无剩余步） | `succeeded` | Journal: `succeeded`；投影 `outcome=succeeded` |
| `running` | `StepBlocked(reason)` | `blocked` | `blockedAtTick`；Journal: `blocked`（含原因） |
| `running` | `StepFatal(reason)` | `failed` | Journal: `failed`；**已完成步不回滚** |
| `running` / `blocked` | `DeadlineReached` | `failed` | Journal: `failed`（`reason=deadline`） |
| `blocked` | `Resumed`（守护重新成立） | `running` | Journal: `resumed` |
| `blocked` | `GraceExpired` | `failed` | Journal: `failed`（`reason=grace_expired`） |
| 非终态 | `Cancelled` | `aborted` | Journal: `aborted`；**已完成步不回滚** |
| **终态** | 任何推进事件 | — | **抛** `InvalidOperationException`（对齐"订单已是终态不能重复执行"的既有规则） |
| **终态** | 重复 `Cancelled` | — | **不抛**，返回 `(false, "过程已终结")`（宿主重试必须幂等） |

### 2.3 与既有语义的一致性约束（冻结）

1. `feasible == false && undetermined == false` 的订单**不得**创建执行过程——与既有承重不变量「判定不可行 ⇒ 执行必失败」一致。
2. `undetermined == true` 的订单**允许**创建过程（贪心兜底），但 `PlanSource = greedy` 必须写进 Journal（只进日志，不下发玩家）。
3. 失败不回滚已完成步（`规则.txt` 的设计定案）；跨台转移仍按既有 `SlaveRollback` 语义回滚。

---

## §3 对外契约扩展（只加不改）

### 3.1 `state` 根新增键（冻结：1 个）

| 键 | 类型 | 说明 |
|---|---|---|
| `worldRevision` | int | 世界修订号，每次世界变更 +1；用于"过程观察到的是哪一版世界"的诊断 |

### 3.2 `state.processes[]` 元素键集合（冻结：13 键）

| # | 键 | 类型 | 可空 | 说明 |
|---|---|---|---|---|
| 1 | `id` | int | 否 | 过程 id（进程内单调递增） |
| 2 | `kind` | string | 否 | 取值见 §2.1 |
| 3 | `subject` | string | 否 | **世界语言**描述（例：`"委托 #12"` / `"玄武台 迁徙"`），前端直接显示 |
| 4 | `subjectId` | int | 是 | 结构化标识（订单 id / 工作台下标） |
| 5 | `state` | string | 否 | 取值见 §2.1 |
| 6 | `outcome` | string | 是 | 未终结为 null |
| 7 | `currentStep` | int | 否 | 已完成步数（`0..totalSteps`） |
| 8 | `totalSteps` | int | 否 | 计划总步数 |
| 9 | `startedAtTick` | int | 是 | 未开始为 null |
| 10 | `deadlineTick` | int | 是 | 无时限为 null |
| 11 | `remainingTicks` | int | 是 | `deadlineTick - now`；**由服务端算好下发**（客户端不知道 tick 速率，自己算必错） |
| 12 | `blockedReason` | string | 是 | **世界语言**的阻断原因（例：`"忌律所限"`） |
| 13 | `lastJournalSeq` | int | 否 | 该过程最后一条 Journal 序号（便于客户端发现"我漏了消息"） |

**不下发的内部字段**（刻意）：`priority`（同 tick 推进顺序）、`planSource`、`graceTicks` 的原始值、计划明细。
理由：§2 的暗箱原则——玩家与客户端只需要"世界语言 + 进度 + 时限 + 原因"。

### 3.3 新增消息（服务端 → 客户端，冻结：5 条）

> **状态：已被 S1 取代（2026-11）。** 这 5 条 `process*` 消息是宿主自己翻译的词汇表，
> 现已由核心帧流的一条 `frame` 消息取代（`Presentation/PresentationFeed` +
> `src/JarSystem.Web/PresentationObserver.cs`），`--check-protocol` 的期望集合同步为
> `commands=21, messages=12`。本节保留作历史记录：它记录了"过程对外可见"这件事最初的形状。

沿用既有 `{ "type": ..., "data": {...} }` 一元结构；键名全部 `[JsonPropertyName]` 钉死。

| 消息 | 载荷键 |
|---|---|
| `processStarted` | `id` : int, `kind` : string, `subject` : string, `totalSteps` : int, `deadlineTick` : int? |
| `processProgressed` | `id` : int, `currentStep` : int, `totalSteps` : int, `tick` : int |
| `processBlocked` | `id` : int, `currentStep` : int, `reason` : string, `graceTicks` : int? |
| `processResumed` | `id` : int, `currentStep` : int, `tick` : int |
| `processFinished` | `id` : int, `outcome` : string, `currentStep` : int, `reason` : string? |

> ⚠️ 新增消息名必须同步登记进 `test/JarSystem.Test` 的 `--check-protocol` 期望集合，否则 `verify.bat` 第 7 步会红（这是**设计如此**：协议守卫的职责就是防止"改了源码忘了契约"）。

### 3.4 新增命令（客户端 → 服务端，冻结名称；Phase 4 实现）

| 命令 | 载荷 | 说明 |
|---|---|---|
| `cancelProcess` | `processId` : int | 取消进行中的过程（`aborted`，已完成步不回滚） |
| `pauseProcess` / `resumeProcess` | `processId` : int | ⏳ 待裁决（§11.4）：是否给玩家"暂停过程"的权力 |

### 3.5 过程日志存档格式（冻结：`journalVersion = 1`）

与既有 `WorldCommandJson` 并列，新增 `ProcessJournalJson`：

```json
{ "journalVersion": 1, "entries": [ { "seq": 1, "tick": 0, "processId": 1, "kind": "started",
  "stepIndex": null, "operation": null, "modifier": null, "reason": null,
  "worldRevision": 12, "summary": "开始：委托 #12（7 步）" } ] }
```

`JournalEntry` 键集合（冻结：10 键）：`seq` / `tick` / `processId` / `kind` / `stepIndex` / `operation` / `modifier` / `reason` / `worldRevision` / `summary`。

`kind` 取值（冻结）：`plan_accepted` / `started` / `step_begin` / `step_applied` / `step_skipped` / `blocked` / `resumed` / `failed` / `succeeded` / `aborted` / `modifier_on` / `modifier_off` / `grace_expired`。

规则：**append-only**；只记"改变世界的步"与里程碑；未知键读档即报错（沿用 `JsonUnmappedMemberHandling.Disallow` 的既定策略）。

---

## §4 接口签名草案（C#）

> 风格沿用本仓库约定：中文 XML 注释、说明"为什么"、集合只读、`init` 不可变、规则拒绝用 `DomainValidationException`。

```csharp
namespace JarSystem.Core.Process;            // 新增命名空间（依赖方向见 §6）

// ---------- Model（纯数据 + 不变量，零依赖） ----------

public sealed class ProcessDefinition
{
    public string Kind { get; init; } = "";              // §2.1 取值
    public ProcessPlan Plan { get; init; } = ProcessPlan.Empty;
    public int? DeadlineTicks { get; init; }              // null = 无时限（相对值，启动时换算成绝对 tick）
    public int GraceTicks { get; init; } = 0;             // 被阻断后的宽限窗口
    public int Priority { get; init; } = 0;               // 同 tick 内的推进顺序
}

public sealed class ProcessPlan
{
    public static readonly ProcessPlan Empty = new();
    public IReadOnlyList<ProcessStep> Steps { get; init; } = [];   // 只读包装
    public string Source { get; init; } = "solver";                // PlanSource：仅进 Journal
}

public sealed class ProcessStep
{
    public string Operation { get; init; } = "";    // MATCH / POUR_IN / POUR_OUT / RESHAPE / TRANSFER
    public int? JarId { get; init; }                // 稳定身份优先（与既有 SolverStepApplier 的契约一致）
    public int? JarIndex { get; init; }             // 回退定位
    public string? TargetShape { get; init; }
    public string? TargetLiquid { get; init; }
    public string? CostLiquid { get; init; }
    public bool FullReshape { get; init; }
    public string Guard { get; init; } = "";        // 前置条件的人类可读名（世界语言）
}

public sealed class ProcessInstance                      // 运行时状态：变更只经意图方法
{
    public int Id { get; }
    public string Kind { get; }
    public string Subject { get; }                      // 世界语言
    public int? SubjectId { get; }
    public ProcessPlan Plan { get; }
    public ProcessState State { get; private set; }
    public ProcessOutcome? Outcome { get; private set; }
    public int CurrentStep { get; private set; }
    public int TotalSteps => Plan.Steps.Count;
    public int? StartedAtTick { get; private set; }
    public int? DeadlineTick { get; private set; }
    public string? BlockedReason { get; private set; }
    public int LastJournalSeq { get; private set; }

    // 意图方法（唯一写入口；非法迁移抛 InvalidOperationException，见 §2.2）
    internal void MarkReady();
    internal void Start(int tick);
    internal void CommitStep(int tick, JournalEntry entry);
    internal void Block(int tick, string reason);
    internal void Resume(int tick);
    internal void Conclude(ProcessOutcome outcome, int tick, string? reason);
}

// ---------- Rules（叠加层；默认空栈 == 今天的行为） ----------

public interface IRuleModifier
{
    string Kind { get; }                 // suppress / require / grant / retime / intensity
    string Source { get; }               // 事件 / 关卡 / 角色 / 器物
    int Priority { get; }
    (int From, int? To)? Window { get; } // tick 区间；To 为 null 表示"直到撤销"
    bool AppliesTo(string workshopName, string? shape, string? liquid, string operation);
}

public sealed class RuleContext
{
    public static RuleContext From(WorkshopCapability capability);  // 默认构造：无修饰器 ⇒ 逐字节等价
    public bool IsBanned(string liquid);
    public bool CanTransform(string from, string to);
    public IReadOnlyList<string> GetPossibleTransforms(string shape);
    public int IntervalFor(ProcessInstance p, ProcessStep step);     // 步间隔（retime 修饰器在这里生效）
}

public sealed class ModifierScope : IDisposable                   // 与既有 WithTemporaryAdditionalMarks 同手法
{
    public ModifierScope(ModifierStack stack, IRuleModifier modifier);
    public void Dispose();                                        // 区间结束或异常都必须回收
}

// ---------- Scheduler（纯逻辑；决定"谁在哪个 tick 做什么"） ----------

public sealed class LogicalClock
{
    public int Tick { get; private set; }
    public void Advance();
}

public sealed class TickPipeline                                   // §5 的四步固定顺序
{
    public TickResult RunOneTick(TickInput input);                  // ①事件 → ②修饰器 → ③过程 → ④产出
}

// ---------- Execution（Check → Apply → Commit） ----------

public enum GuardStatus { Ok, Blocked, Fatal }
public readonly record struct GuardResult(GuardStatus Status, string? Reason);

public sealed class StepRunner
{
    public GuardResult Check(ProcessInstance p, ProcessStep step, RuleContext rules, WorldView world);
    public bool Apply(ProcessInstance p, ProcessStep step, IOperationAdapter ops, out string? reason);
}

public interface IOperationAdapter         // 把 ProcessStep 映射到**既有原子操作**（Command/Workshop 意图方法）
{
    bool TryApply(ProcessStep step, out string? reason);
}

// ---------- Journal ----------

public sealed class ProcessJournal
{
    public JournalEntry Append(int tick, int processId, string kind, int? stepIndex = null,
        string? operation = null, string? modifier = null, string? reason = null, string? summary = null);
    public IReadOnlyList<JournalEntry> Entries { get; }             // 只读包装
}

// ---------- Ports（对外；宿主只依赖这一层） ----------

public interface IProcessHost
{
    (bool Success, string? Error, int? ProcessId) Start(ProcessDefinition definition, string subject, int? subjectId);
    (bool Success, string? Error) Cancel(int processId);
    IReadOnlyList<ProcessInstance> ActiveProcesses { get; }
}

public interface IWorldEventSource          // 事件只改世界（§5.1 边界）
{
    IReadOnlyList<WorldEvent> DrainForTick(int tick);
}

public interface IProcessObserver           // 表现层订阅（宿主把 tick 映射成动画）
{
    void OnStarted(ProcessInstance p);
    void OnProgressed(ProcessInstance p, int tick);
    void OnBlocked(ProcessInstance p, string reason, int? graceTicks);
    void OnResumed(ProcessInstance p, int tick);
    void OnFinished(ProcessInstance p, ProcessOutcome outcome, string? reason);
}

public sealed class ProcessSystem : IProcessHost             // 宿主唯一入口（门面）
{
    public ProcessSystem(LogicalClock clock, ProcessJournal journal, IProcessObserver? observer = null);
}
```

---

## §5 边界与并发（冻结）

### 5.1 三条边界（不可越）

1. **过程只读世界、只调既有原子操作**；禁止直接改容器/字段。
2. **事件只改世界**（经既有意图方法），**不得直接改过程状态**；过程通过只读快照（既有 `CreateSnapshot()` 手法）自行迁移。
3. **既有内核零 `Process` 引用**；规则查询经 `RuleContext`（默认空栈 ⇒ 今天的行为）。

### 5.2 每 tick 的固定处理顺序（确定性来源）

```
① 事件注入     IWorldEventSource.DrainForTick(tick) → 改世界 / 加修饰器
② 修饰器更新   区间生效/失效、冲突解析（冲突显式报错）
③ 过程推进     按 Priority 升序，每个过程至多一步（Check → Apply → Commit）
④ 世界产出     既有 ProducerModule.Tick + FlowToMaster（行为不变）
```

全部在同一串行闸门（既有 `JarSystemService.Gate`）内完成 ⇒ **可中断 + 确定性同时成立**。

---

## §6 模块划分与文件清单（冻结）

### 6.1 新增包（按阶段推进；本节"阶段归属"已在 P1 完成后校正）

**校正说明（2026-10-16）**：本节原先写"Phase 1 只建前三个（Model / Rules / Execution）"，
与实际实施不符，且与 §8 自相矛盾（§8 写的是 Model + Execution + Journal）。实际落地：

* **P1 已建**：`Model/`、`Journal/`、`ProcessRun` + `ProcessSystem`；
* `Rules/`（修饰器栈）归 **P3**；`Scheduler/`（逻辑时钟与 tick 管线）归 **P2**；
  `Execution/` 在 P1 只有"记账"这一半职责（已并入 `ProcessRun`），其余（`StepRunner` / 守护 / 前置条件）归 **P3**。

```
src/JarSystem.Core/Process/
  Model/          ProcessDefinition.cs  ProcessPlan.cs  ProcessStep.cs
                  ProcessInstance.cs    ProcessState.cs ProcessOutcome.cs
                  ProcessKind.cs        JournalEntry.cs Milestone.cs
  Rules/          RuleContext.cs        IRuleModifier.cs  ModifierStack.cs
                  ModifierScope.cs      ConflictPolicy.cs
  Execution/      StepRunner.cs         PreconditionChecker.cs  GuardResult.cs
                  IOperationAdapter.cs  CommandOperationAdapter.cs
  Journal/        ProcessJournal.cs     JournalCodec.cs
  Scheduler/      LogicalClock.cs       StepAllocator.cs  TickPipeline.cs
  Ports/          IProcessHost.cs       IWorldEventSource.cs  IProcessObserver.cs
                  IProcessStore.cs
  ProcessSystem.cs（门面）
```

### 6.2 既有文件的改动点（Phase 1 只碰这几处，全部有等价性论据）

| 文件 | 改动 | 等价性论据 |
|---|---|---|
| `src/JarSystem.Core/JarSystem.cs` | 新增 `Processes`（只读视图）与 `WorldRevision`；`Tick()` 内部改为调用 `TickPipeline`（Phase 1 的管线等价于今天的顺序） | 今天 `Tick` 就是"产出 → 流入"；管线第 ①②③ 步在无事件、无修饰器、无过程时是空操作 |
| `src/JarSystem.Core/Services/ProductionService.cs` | 同上（管线第 ④ 步搬进去） | 调用顺序与参数一字不变 |
| `src/JarSystem.Core/Domain/Workshop.cs` | 规则查询重载为经 `RuleContext`（旧签名保留，内部新建默认上下文） | 默认空栈 ⇒ `IsBanned/CanTransform/GetPossibleTransforms` 返回值逐项相同 |
| `src/JarSystem.Core/Services/StateDtos.cs` | 新增 `ProcessStateDto` + `StateDto.WorldRevision/Processes` | 只加键；既有键的声明顺序与类型不变 ⇒ 快照 diff 只增行 |
| `src/JarSystem.Core/Services/StateSerializer.cs` | 投影 `processes[]` 与 `worldRevision` | 同上 |
| `src/JarSystem.Core/Orders/OrderService.cs` | `ExecuteOrder` 改为"创建并推进一个瞬发过程"（Phase 1） | 瞬发模式下同 tick 内完成 ⇒ 对外 JSON、事件历史、日志逐字节一致 |
| `src/JarSystem.Web/CommandDispatcher.cs` | Phase 1 不改；Phase 4 增 `cancelProcess` 等命令 | — |
| `test/JarSystem.Test/Program.cs` | 新增 `--dump-journal`（新产物）与 S23–S26；`--check-protocol` 期望集合随 Phase 4 更新 | 既有模式与其输出断言不变 |

### 6.3 依赖方向（自检清单，Codex 实现时逐条核对）

1. `Process/*` 可以 `using` 既有内核，反之**不可以**。
2. `Process/Model` 只 `using` BCL（零业务依赖）。
3. `Process/Rules` 只依赖 `Model` + 既有的 `Config`/`Domain`（只读）。
4. `Process` 内部不出现 `System.Timers`/`Task.Delay`/`DateTimeOffset.Now`（时间只能来自 `LogicalClock`）。

---

## §7 不变量与测试清单（冻结）

### 7.1 十条不变量（Phase 1 起逐步锁定；括号内为生效阶段）

| # | 不变量 | 锁定方式 |
|---|---|---|
| 1 | 确定性：同（世界+种子+事件序列）⇒ 过程日志与最终状态逐字节一致 | 扩展 `--check-replay`（Phase 2） |
| 2 | 单步原子性：一步要么完整生效要么完全不生效 | 替身注入异常（Phase 1） |
| 3 | 前置条件守恒：不成立绝不执行 | 构造不成立的世界（Phase 3） |
| 4 | 代价守恒：失败不回滚已完成步 | S23（Phase 3） |
| 5 | 资源可解释：每步增减（含丢弃）可在 Journal 对上账 | 随机会话账目断言（Phase 2） |
| 6 | 时间单调 + 无墙钟依赖 | 伪时钟注入 + 不同墙钟节奏双跑（Phase 2） |
| 7 | 修饰器区间封闭：结束必回收（含异常） | 作用域 + 抛异常用例（Phase 3） |
| 8 | 同 tick 处理顺序固定：事件 → 修饰器 → 过程 → 产出 | 同 tick 混合事件与推进的用例（Phase 3） |
| 9 | 契约兼容：既有键/命令名/消息名一字不变 | 九步门第 4/5/6/7 步自动覆盖（Phase 1 起每阶段跑） |
| 10 | 可中断等价性：暂停/恢复结果 == 不暂停 | 双路径比对（Phase 3） |

### 7.2 新增确定性用例（冻结断言）

| 用例 | 场景 | 必须成立的断言 |
|---|---|---|
| **S23** | 7 步过程，第 3 步后世界资源缺失（事件只改世界） | 过程 `blocked` → 宽限耗尽 → `failed`；`currentStep == 3`；前 3 步的资源变化**保留**；Journal 含 `blocked` + `grace_expired` + `failed`；事件历史与既有语义一致 |
| **S24** | `Suppress` 修饰器禁用第 4 步所需操作，区间随后撤销 | 第 4 步 `blocked`；撤销后 `resumed` 并完成；最终世界状态与"从未被阻断"的对照运行**完全一致** |
| **S25** | `Retime` 把第 4 步间隔 1 → 3 | 完成 tick 后移；最终世界状态与无修饰器对照**完全一致**（只有时间不同） |
| **S26** | 过程日志回放 | 重放进全新系统后 Journal 与原 Journal 逐字节一致（与既有第 9 步同源） |

### 7.3 九步门的扩展计划

| 步 | 现状 | Phase 1 | Phase 2 | Phase 3 |
|---|---|---|---|---|
| 2 `JarSystem.Test` | 2000+3000+22 | 不变 + 文档口径守卫纳入新数字 | 不变 | +S23–S26（确定性用例计数上升，**须同步 9 个载体**） |
| 4 契约快照 | 223 行 | +`worldRevision`/`processes` 路径（新基线，增长须有独立记录） | 不变 | 不变 |
| 5 schema | 10 类型 loose=1 | +`ProcessStateDto`（11 类型；loose 目标仍为 1） | 不变 | 不变 |
| 7 协议守卫 | 18 命令/10 消息 | 不变 | 不变 | 不变 |
| 9 会话回放 | 命令日志 | 不变 | 纳入过程日志 | 不变 |
| 新增 | — | `--dump-journal` 产物 + 快照 | 过程时间线快照 | 同 tick 顺序用例 |

> ⚠️ 提醒（本项目已有教训）：**任何"确定性用例数"变化都必须同步 9 个载体**（`verify.bat`、`verify.yml`、`_审查上下文.md`、`05_扩展指南.md`、`11`、`12`、`13`、`UML/04_Component`、`JarSystem.Tests/README.md`），否则 `CheckDocs` 会红。这条已在 `test/JarSystem.Test` 里做成机器检查。

---

## §8 Phase 1 实施清单（等批准；**行为逐字节不变**）

1. 新建 `Process/Model` + `Process/Execution` + `Process/Journal`（**不含** Scheduler 的时间化、不含 Rules 的修饰器栈）。
2. `OrderService.ExecuteOrder` 改为：构造 `ProcessDefinition`（`Plan` 来自现有 `SolverPath`/`MasterPath`，`PlanSource` 记录来源）→ `ProcessSystem.Start` → 在同一 tick 内推进全部步骤 → 结束。**瞬发模式默认步间隔 0**，因此对外结果与今天完全一致。
3. 既有 `OrderEvent`/`ExecutionLogEntry`/`eventHistory` **照旧写入**（双写期），保证第 4 步快照与前端行为不变。
4. 新增 `--dump-journal` 产物（`过程系统/_journal/`，仅新产物，不进九步门的判定）。
5. 跑 `verify.bat`，逐项比对 §10 的基线；**任何一项数字变化都必须能解释**，否则回滚。

**回滚方式**：删除 `Process/` 目录 + 还原 `OrderService.ExecuteOrder` 的调用点（改动集中在 6 个文件，见 §6.2）。

---

## §9 风险与对策（Phase 0 冻结版）

| 风险 | 触发 | 对策 |
|---|---|---|
| 长过程锁死世界 | 步间隔过大 / 过程数过多 | 每 tick 每过程至多一步 + 过程上限 + 优先级（Phase 2/3） |
| 日志膨胀 | 每步全量快照 | 只记增量摘要 + 里程碑；超窗整段裁剪（Phase 2） |
| 修饰器冲突 | 多来源改同一规则 | 优先级 + 同源覆盖 + 冲突显式报错（Phase 3） |
| 回放不兼容 | 新键/新语义 | 存档版本号 + 未知键报错（沿用既有策略） |
| 浮点破坏确定性 | `double` 产率累加 | 定点化（Phase 2 前置） |
| 契约膨胀 | 每次加键都动基线 | 只加不改 + 三件产物自动生成 + 门判定 |
| 过程无感 | 只在内部推进 | Phase 4 必须把进度/时限/阻断投影成世界语言 |

---

## §10 附录：本次基线（2026-10-16 复跑，`CI=1`）

| 项 | 值 |
|---|---|
| 环境 | .NET SDK 10.0.302 |
| 主测试基线 | `0 bugs / 2000 random + 0 bugs / 3000 capacity + 22 deterministic` |
| 文档口径 | `docs baseline: OK (9 carriers say 22)` |
| 契约快照 | `state_keys.txt` 223 行 / `state_keys.baseline.txt` 11021 字节 |
| Schema | 10 类型 / `total loose=1`（唯一空洞：`eventHistory`，已在 `重构报告/10` §5.3 论证为有理由的例外） |
| 前端类型 | `contract.d.ts` 224 行 |
| 配置世界 | `CONFIG-WORLD-MATCH` |
| 协议 | `PROTOCOL-OK (commands=18, messages=10)` |
| JS | `JS-OK` |
| 求解器差分 | `SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)` |
| 会话回放 | `REPLAY-OK (commands=29, stateBytes=2369, logBytes=7748)` |
| 结论 | `VERDICT=OK`；`git status` 显示受版本控制的文件**零改动** |

---

## §11 决策记录（ADR）与待裁决项

### 11.1 已决策（附理由与替代方案）

| # | 决策 | 理由 | 替代方案与否决原因 |
|---|---|---|---|
| ADR-1 | **两套时钟**：逻辑 tick 判定 / 墙钟只属表现 | 保住"读档回放逐字节一致"这条既有硬资产（Temporal 重放经验 + Lamport 逻辑时间） | 用墙钟驱动判定：会破坏回放，且行为随机器快慢漂移 |
| ADR-2 | **单线程固定顺序管线**，不引入并发 | 可中断 + 确定性同时成立；避免锁与竞态 | 真并发/多线程推进：确定性不可保证，回放门必红 |
| ADR-3 | **`RuleContext` 默认空栈 == 今天的行为** | 让"临时规则"成为叠加层而非新法则，且迁移可分批、可回滚 | 直接给 `WorkshopCapability` 加修饰器字段：污染核心不变量 |
| ADR-4 | **契约只加不改** | 沿用项目既定硬约束与三件产物机制 | 改既有键：破坏前端/Godot/契约快照 |
| ADR-5 | **Journal 与 `WorldCommand` 双层** | 世界级意图 vs 过程级步进，粒度不同；双层可分别回放与裁剪 | 合并为一层：世界回放会背上过程细节，日志急速膨胀 |
| ADR-6 | **浮点定点化放 Phase 2 前置** | 它决定"过程日志能否跨平台复现" | 留到以后：越晚改，越多既有数据与用例需要重算 |
| ADR-7 | 过程 `Plan` 采用**不可变快照** | 过程不受运行期外部改动影响（与既有 `CreateSnapshot` 同一思路） | 引用实时路径：中途被改会导致过程与实际不符 |

### 11.2 ✅ 已裁决（2026-10-16，用户决定）

| # | 问题 | 裁决 | 落地位置 |
|---|---|---|---|
| 1 | `state.processes[]` 的 13 键是否照此冻结？ | **冻结**；并明确"能够优化的结构只要有好处，不必在意过小改动" | P2/P4 按本表投影 |
| 2 | `remainingTicks` 由谁算？ | **服务端下发**（客户端不知道 tick 速率） | P2/P4 |
| 3 | Phase 2 默认步间隔 | **更平缓**（取 2 tick/步；它是**默认值**，将来随修饰器调整） | P2 |
| 4 | 玩家能否取消进行中的过程？ | **允许**；并明确"还会有其它造成取消或直接失败的原因" ⇒ 过程结尾需要**原因分类**（P4 补；P1 的三态 + `reason` 结构上已可承载） | P4 |
| 5 | Journal 是否进存档？ | **进存档**；必要时为省空间**采用「最近保留」** | ✅ **P1 已实现**（有界保留：超限丢最旧一半、摊销 O(1)，并记 `DroppedEntries`） |

---

## §12 下一步

---

## §13 实现现状对照（2026-10-16 追加，P5 收口后回填）

本规格是**冻结稿**，实现过程中有几处**有意的偏离**（都已写进对应阶段的板块报告，此处集中登记，避免"规格与实现两张皮"）：

| 规格处 | 规格写的 | 实际实现 | 理由 |
|---|---|---|---|
| §4 `IProcessObserver` | 五个具名回调（OnStarted/…/OnFinished） | **一个** `OnChanged(instance, reason, tick)` | `totalSteps` 在过程建立时还不知道；单回调让顺序天然正确（见 `IProcessObserver` 注释） |
| §4 `ProcessPlan` / `ProcessStep` | 计划作为含步骤序列的不可变对象 | **未实现**（只有"声明步数"） | P1 是瞬发模式，步骤序列不参与排程；`PlanSource` + 声明步数已够用（见 P1 报告 §7） |
| §6 `Process/Rules/` | 规则层放在 Process 下 | **移到顶层 `Rules/`** | 审查发现它导致 `Domain → Process` 错层（见 P4d 报告） |
| §6 `Process/Execution/` | 会话与工作体 | `IProcessAdvancer`/`GuardResult`/`IWorldEventSource` 已移出（Ports/、Rules/） | 通用端口与订单专用件分开（见 P4c 报告） |
| §3.5 `JournalEntry` 的 `modifier` 键 | 记录相关修饰器 | 保留但**当前恒为 null** | 修饰器生效点已由 `blocked` 的原因与 `phase` 承载；待"修饰器参与单步判定"时再接 |
| §2.1 `JournalKinds` | 13 个取值 | 4 个保留未用（`step_begin`/`step_skipped`/`modifier_on`/`modifier_off`） | 冻结词汇表里预留；写入时机要等修饰器细化到单步 |
| `ProcessDefinition.Priority` | 同 tick 内的推进顺序 | ✅ P5 收口时**补上**（此前声明未读） | 审查发现（见 §14 报告） |
| `IRuleModifier.Window.From` | 区间起点 | ✅ P5 收口时**补上**（此前只判 `To`） | 审查发现（见 §14 报告） |
| `ProcessInstance.DeadlineTick` | 超时即失败 | ✅ P5 收口时**补上**（此前只投影给客户端） | 审查发现（见 §14 报告） |

你确认 §11.2 的 5 项（或直接说"按推荐来"）后，我就开工 **Phase 1**：

1. 建立 `Process/Model` + `Execution` + `Journal`（不含时间化与修饰器）；
2. 把 `ExecuteOrder` 表达为瞬发过程（行为逐字节不变）；
3. 新增 `--dump-journal` 产物；
4. 跑 `verify.bat` 并与 §10 基线逐项比对；
5. 按"板块报告"格式产出 `过程系统/01_Phase1_骨架.md`（职责/改动/等价性论证/验证结果/遗留项）。

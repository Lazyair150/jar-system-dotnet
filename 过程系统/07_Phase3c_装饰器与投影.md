# Phase 3c 报告：修饰器的统一处理层（装饰器）、事件注入、以及对外投影

> 生成时间：2026-10-16　生成者：Codex
> 前置：用户四项指示（① 用"求解器之上的抽象层"处理新规则；② Require/Intensity 需要改动；③ 事件注入照做；④ 确认投影）。
> 格式沿用「板块报告」：职责 → 设计裁决 → 改动 → 等价性论证 → 验证 → 遗留。

---

## 1. 设计裁决：为什么**不**改求解器，而用装饰器

**问题**（用户提出）：让既有求解器"知道"后来新增的临时规则，是不是过度入侵？

**结论：是，而且有更好的做法。**

1. 求解器是被**差分门（4003 次比对）**与 5000 用例钉住的既有资产。把临时规则塞进它的规则读取点，
   等于**每加一条新规则都要重走一遍求解器的正确性论证**。
2. 更本质的是职责：修饰器是**世界状态的一部分**（区间性、随时来去），求解器是**纯算法**。
   混在一起等于把"世界的易变"灌进"算法的稳定"。
3. 但"在求解器之上加一层"有一个**必须说清的前提**：不能把"这一层拒绝了某个计划"当成"不可行"——
   第一个找到的路径踩到限制，不等于没有别的路径。

**因此落地为 `ModifierAwareSolver`（装饰器，求解器内部一行不改），三条 sound 语义**：

| # | 语义 | 说明 |
|---|---|---|
| ① | **无修饰器 ⇒ 直通** | 逐字节等于原实现（等价性的第一道保障） |
| ② | **可表达的规则 ⇒ 前置投影** | 被抑制的液体折进禁令表；被授予的形状折进附加标记（都不改调用方对象） |
| ③ | **无法表达的规则 ⇒ 未判定** | 经 `IWorkBudgetedSolver.InconclusiveReason` 上报；**绝不说"不可行"** |

后者的判定标准很具体：抑制**液体**（倒入/倒出/存储/改形成本，基础法则里的"禁令"恰好覆盖这四件事）
与授予**形状**可表达；抑制**形状/操作**（如 MATCH/TRANSFER/GREEDY）与 `Require` 这类额外条件不可表达；
`Retime`/`Intensity` 只影响时间与强度，**不影响静态可行性**，故被忽略。

配套：`IWorkBudgetedSolver` 新增 `string? InconclusiveReason => null;`（**带默认实现** ⇒ 不破坏既有实现者的源码兼容），
`SinglePhaseCheck` 与 `PartitionSearch` 的 3 处"未判定"文案改为读它 —— 于是"预算用尽"与"规则无法表达"
两种未判定在报告里能分清。

## 2. 改动清单

| 文件 | 改动 |
|---|---|
| `Process/Rules/ModifierAwareSolver.cs` | **新增**：装饰器求解器（含前置投影与未判定上报） |
| `Abstractions/IWorkBudgetedSolver.cs` | 新增 `InconclusiveReason`（默认实现） |
| `Orders/Feasibility/FeasibilityChecks.cs`、`PartitionSearch.cs` | 3 处未判定文案改为读 `InconclusiveReason` |
| `JarSystem.cs` | 组合根：`Modifiers` 先建，`Checker` 用 `new ModifierAwareSolver(注入的求解器, Modifiers)` 包一层；守卫新增"新增前置条件"检查 |
| `Process/Rules/IRuleModifier.cs`、`ModifierStack.cs` | `RequireModifier` 接上消费者（`Violation` 谓词）；`IntensityModifier` 改**整数千分比**（`FactorPerMille`）+ `ApplyIntensity` |
| `Services/ProductionService.cs` | 液体产出速率经 `ApplyIntensity("liquid:名", 基础速率)`（无匹配修饰器时恒等） |
| `Process/Execution/IWorldEventSource.cs` | **新增**：世界事件源 + `WorldEvent`（名字/说明/效果闭包） |
| `Process/ProcessSystem.cs`、`Scheduler/TickPipeline.cs` | 管线第 ① 步 = 事件注入（事件**只改世界**，不得改过程状态） |
| `Services/StateDtos.cs`、`StateSerializer.cs` | **投影**：`state.worldRevision` + `state.processes[]`（13 键的 `ProcessStateDto`） |
| `test/JarSystem.Test/Program.cs` | 新增确定性用例 S27/S28/S29；schema 导出清单加 `ProcessStateDto`；附 `TestEventSource` 替身 |
| `重构报告/verify.bat` | 第 5 步期望文案 `10 types` → `11 types` |
| 9 个文档载体 + `过程系统/README.md` | 同步确定性用例计数 `26 → 29` |

## 3. 等价性论证

| 点 | 论证 | 实测证据 |
|---|---|---|
| 装饰器求解器 | 无活跃修饰器 ⇒ **直接委托**给被装饰的求解器 | `SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)` 逐字未变 |
| 事件源 | 默认 `EventSource == null` ⇒ 第 ① 步空操作 | 主测试 `0 bugs / 2000 + 0 bugs / 3000` 未变 |
| 强度 | `ApplyIntensity` 在无匹配修饰器时**恒等返回**；且全程整数运算（不引入浮点） | 同上；`REPLAY-OK` 逐字 |
| 新增前置条件 | 无 `Require` 修饰器 ⇒ 检查返回 null ⇒ 不影响 | 同上 |
| 投影 | **只加两个键**（`worldRevision`、`processes`），既有键一字未动 | 契约快照 diff 只有两行新增（见 §4） |

## 4. 验证

### 4.1 九步门

| 项 | 结果 |
|---|---|
| 主测试 | `0 bugs / 2000 random + 0 bugs / 3000 capacity + 29 deterministic` |
| 文档口径 | `docs baseline: OK (9 carriers say 29)` |
| 契约快照 | **有意重建**：223 → **225 行**（新增 `state.worldRevision`、`state.processes = <empty>`，其余逐字未动） |
| Schema | 10 → **11 类型**（新增 `ProcessStateDto`，`loose=0`）；`total loose=1` 不变 |
| 前端类型 | `contract.d.ts`：30 接口 / 224 行 → **31 接口 / 242 行** |
| 配置世界 / 协议 / JS | `CONFIG-WORLD-MATCH` / `PROTOCOL-OK (commands=18, messages=10)` / `JS-OK` |
| 求解器差分 | `SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)` |
| 会话回放 | `REPLAY-OK (commands=33, stateBytes=3125, logBytes=8797, journalBytes=1415)`（`stateBytes` +34 = 新增两键） |
| 末尾判定 | `VERDICT=OK`（19.3 秒） |

### 4.2 三条新确定性用例

| 用例 | 场景 | 机械断言 |
|---|---|---|
| **S27** | 事件源在第 2 刻注入"井流倍增"（强度 2000 千分比，持续生效） | 事件恰好触发 1 次；实验组仓库酒库存 **高于**对照组 |
| **S28** | 抑制某个**形状**（求解器无法表达的限制） | 无修饰器时 `feasible=true`；加了抑制后 `feasible=false` 且 **`undetermined=true`**（而不是"不可行"） |
| **S29** | `Require` 的 `Violation` 先返回原因、后返回 null | 订单成功；里程碑顺序 `blocked → resumed → succeeded` |

### 4.3 新机制抓到的真实缺陷（值得记）

S29 首次运行直接崩：

```
FAIL S29-...: 过程 1 状态为 Ready，不能进入阻断（要求 Running）
```

根因：**守卫可能在第一个单位之前就判阻断**（流程刚建立、世界当时就不满足前置条件），
而 `Block` 要求状态是 `Running`。修法：`ProcessRun.NoteBlocked` 先 `EnsureStarted()`
——于是过程日志里也会有一条 `started`，状态机合法（`Ready → Running → Blocked`）。

这正是"新机制必须配新用例"的实证：这条路径在 P3a 的三条判据下永远不会触发，只有 `Require` 才暴露它。

## 5. 遗留

1. **协议与表现（P4 后半）**：`state.processes[]` 已经能下发，但**过程消息与命令**（`process*` 五条消息、
   `startOrder` / `cancelProcess`）与前端消费还没接 —— 接的时候必须同步 `--check-protocol` 的期望清单。
2. **修饰器的数据驱动**：现在修饰器由宿主代码构造（含 `Require` 的谓词）⇒ 关卡/事件数据（JSON）→ 修饰器
   的映射还没做；`Require` 目前是"宿主给谓词"，将来若要纯数据表达，需要一个小条件语言（属内容侧设计）。
3. **`Intensity` 的更多消费者**：目前只接"液体每 tick 产出"；产出（生产者速率）/ 容量 / 丢弃计数可逐个接。
4. **装饰器求解器的表达力**：目前"抑制形状/操作 ⇒ 未判定"偏保守。若将来希望这类规则也能**精确判定**，
   可以让装饰器**在受限输入上重跑**（例如把被抑制形状的罐子从池子里去掉）；但那是新的正确性论证，
   必须先有差分用例（现有 `--check-solver` 门可以扩展成"带修饰器的差分门"）。

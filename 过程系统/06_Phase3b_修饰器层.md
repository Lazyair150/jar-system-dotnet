# Phase 3b 报告：修饰器层（区间性的临时规则）

> 生成时间：2026-10-16　生成者：Codex
> 格式沿用「板块报告」：职责 → 改动 → 语义 → 等价性论证 → 验证 → 遗留。

---

## 1. 板块职责

让"某些状态下新出现的规则限制"成为一等机制：世界可以在**时间区间**内对基础法则做
**抑制 / 放宽 / 改拍 / 新增前置条件**，而不修改基础法则本身（基础法则是世界的不变量）。

这正是设计文档里那句"某个 tick 变化被 ban，而这个 ban 又不出现在原有规则下"的正主：
**守卫负责发现前置条件不成立，修饰器负责改变前置条件本身。**

## 2. 改动清单

### 2.1 新增（`src/JarSystem.Core/Process/Rules/`）

| 文件 | 职责 |
|---|---|
| `IRuleModifier.cs` | 修饰器契约（名字/种类/来源/优先级/区间）+ 五种实现：`Suppress`（抑制）、`Require`（新增前置条件）、`Grant`（放宽）、`Retime`（改拍）、`Intensity`（强度） |
| `ModifierStack.cs` | 修饰器栈：作用域回收（`ModifierScope`，幂等）、区间到期自动摘除（`Expire`）、同优先级冲突显式报错、抑制查询、改拍查询、被授予形状查询 |
| `RuleContext.cs` | 规则读取的唯一入口：基础能力 + 活跃修饰器 |

### 2.2 改动

| 文件 | 改动 |
|---|---|
| `Domain/Workshop.cs` | 规则读取全部经 `RuleContext`；四个操作（`PourIn/PourOut/ReshapeEmpty/ReshapeFull`）在基础校验之后加一道**抑制检查**（防御纵深：绕过守卫直接调用也会被拒，原因是世界语言）；`WithTemporaryAdditionalMarks` 改为走 Grant 机制（旧行为是它的特例，只有一套实现）；新增 `AttachRuleStack` |
| `Process/Scheduler/TickPipeline.cs` | 第 ② 步接上 `ModifierStack.Expire(tick)`：区间到期自动解除 |
| `Process/ProcessSystem.cs` | 持世界的修饰器栈；步间隔优先级变为：单次定义覆盖 > Retime 修饰器 > 分配器默认 |
| `JarSystem.cs` | 新增 `Modifiers`（世界唯一的修饰器栈）；过程系统、装配器、工作台创建/收养全部绑同一实例；守卫新增抑制检查（下一步被 ban 则 `Blocked`，等区间过去即 `Resumed`） |
| `Bootstrap/SystemBootstrapper.cs`、`Services/WorkshopService.cs` | 建工作台时传入世界的修饰器栈 |
| `test/JarSystem.Test/Program.cs` | 新增确定性用例 S25 / S26 |
| 9 个文档载体 + `过程系统/README.md` | 同步确定性用例计数 24 → 26 |

## 3. 语义（四种已接 + 一种已声明）

| 修饰器 | 生效点 | 语义要点 |
|---|---|---|
| Suppress | ① 守卫（阻断，可等待解除）；② 工作台四个操作（兜底拒绝） | 按"操作 / 形状 / 液体"匹配；原因是世界语言，直接显示给玩家 |
| Retime | 过程创建时的步间隔 | 优先级低于单次过程显式覆盖、高于分配器默认 |
| Grant | `RuleContext` 的附加标记与可改形目标 | 既有 `WithTemporaryAdditionalMarks` 现在就是它的一个实例 |
| Require | 声明就位（P3b-2 接守卫条件） | 内容侧可先写数据，消费者后接 |
| Intensity | 声明就位（无消费者） | 同上 |

三条机制约束：

1. **必回收**：入栈一律经 `Push` 返回的作用域，`using` 结束或异常路径都回收（`Dispose` 幂等）。
2. **区间自动失效**：每个 tick 由管线摘掉过期项 —— 这是"临时禁令会自己解除"的实现。
3. **冲突显式报错**：同种类、同目标、同优先级却同时命中 ⇒ 抛异常，而不是悄悄挑一条。

## 4. 等价性论证（默认空栈 = 改造前）

| 点 | 论证 |
|---|---|
| 规则判定 | 空栈时抑制查询恒返回 null、被授予形状为空 ⇒ `RuleContext` 的每个查询都退化成 `WorkshopCapability` 的原语义 |
| 操作结果 | 抑制检查插在基础校验之后（不改变既有失败文案的优先级）；空栈时永不触发 |
| 临时标记 | `WithTemporaryAdditionalMarks` 的作用域语义不变（作用域内可见、结束回收、已标记的不重复加），只是底层换成 Grant |
| 对外契约 | `GetState()` 读的仍是 `Capability` 与快照字段，修饰器不进任何对外 JSON（投影是 P4 的事） |

实测（九步门）：主测试仍 `0 bugs / 2000 random + 0 bugs / 3000 capacity`；契约快照 223 行未变；
schema `total loose=1`；`CONFIG-WORLD-MATCH`；`PROTOCOL-OK (commands=18, messages=10)`；`JS-OK`；
`SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)`；
`REPLAY-OK (commands=33, stateBytes=3091, logBytes=8797, journalBytes=1415)`；`VERDICT=OK`（18.7 秒）。

## 5. 两条新确定性用例

| 用例 | 场景 | 机械断言 |
|---|---|---|
| S25 | 从台那一步要做的 `POUR_IN` 被 `Suppress` 在第 0~3 刻禁掉 | 订单成功；里程碑顺序 `blocked → resumed → succeeded`（区间到期自动解除后恢复并完成） |
| S26 | 同一局面跑两遍：普通节奏 vs `Retime` 4 刻/步 | 两遍都成功；带改拍那遍的完成 tick 晚于普通那遍（只改时刻、不改结果） |

## 6. 遗留（P3b-2 / P4）

1. **求解器侧口径统一**：修饰器目前接在**执行侧与守卫**；`WorkshopSnapshot` / `WorkshopInfo` 还没带
   `RuleContext`，勘验仍按基础法则判断。后果是**可解释的而非静默的**：勘验说可行、执行时被抑制
   ⇒ 守卫把它变成 `Blocked`（等解除）或宽限耗尽后 `Failed`。要统一口径，下一步把同一个
   `RuleContext` 穿到快照与求解器（改动集中在 3 处）。
2. **`Require` / `Intensity` 的消费者**：前者需要"守卫条件"的表达方式（或宿主提供的谓词），
   后者要接进产出 / 容量 / 丢弃这些强度参数。
3. **世界事件注入（`IWorldEventSource`）**：让"谁能加修饰器"从宿主代码变成数据驱动
   （事件 → 修饰器），这是把"事故"写进关卡数据的最后一块（P4/P5）。
4. **投影**：是否把"当前生效的修饰器"下发给客户端，属 P4 的契约决策（只加键）。

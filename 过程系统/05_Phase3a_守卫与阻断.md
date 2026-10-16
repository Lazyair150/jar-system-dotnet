# Phase 3a 报告：守卫与阻断（过程可被异化、可恢复、可失败）

> 生成时间：2026-10-16　生成者：Codex
> 格式沿用「板块报告」：职责 → 改动 → 语义 → 验证 → 与既有语义的一致性 → 遗留。

---

## 1. 板块职责

把设计文档里"**过程会被世界异化**"真正落地：异步轨的每个单位执行前先过**守卫**；
守卫不成立 ⇒ 进 `Blocked` 等待（玩家可能把局面改回来）；宽限窗口内恢复 ⇒ `Resumed` 继续；
窗口耗尽或前置条件**永久**不成立 ⇒ `Failed`。

## 2. 改动清单

### 2.1 新增

| 文件 | 职责 |
|---|---|
| `Process/Execution/GuardResult.cs` | `GuardStatus`（Ok / Blocked / Fatal）+ `GuardResult`（含世界语言的原因） |
| `Process/Model/ProcessDefaults.cs` | 默认值集中处：**宽限窗口 4 刻**、最近过程保留 64 条 |

### 2.2 改动

| 文件 | 改动 |
|---|---|
| `IProcessAdvancer.cs` | 新增 `CheckGuard()`（只读检查，不得改世界）与 `MarkFailed(reason)`（幂等，供过程系统判定失败时调用） |
| `OrderProcessAdvancer.cs` | 接入守卫（由门面提供的委托；缺省恒为 Ok ⇒ 同步轨与既有测试不受影响）；`MarkFailed` 转发给会话 |
| `OrderExecutionSession.cs` | 新增 `FailByGuard(reason)`（标记失败；已完成部分**不回滚**）与 `RemainingJarIds()`（守卫用：还要用到的器物） |
| `ProcessInstance.cs` | 新增 `BlockedAtTick`（宽限倒计时用） |
| `ProcessRun.cs` | 新增 `NoteBlocked` / `NoteResumed` / `NoteGraceExpired`（写过程日志里程碑） |
| `ProcessSystem.cs` | `AdvanceActive` 改为**守卫感知**：已阻断的每 tick 复查（恢复即继续 / 超时即失败）；未阻断的到点先过守卫再推进；新增 `AdvanceUnit` / `FailBlocked` 两条收尾路径 |
| `JarSystem.cs` | 新增 `CheckOrderGuard`（三条判据）；`StartOrder` 挂上守卫并传入默认宽限窗口 |
| `test/JarSystem.Test/Program.cs` | 新增确定性用例 **S23 / S24** |
| 9 个文档载体 + `过程系统/README.md` | 同步确定性用例计数 **22 → 24**（项目规矩：`CheckDocs` 会强制） |

## 3. 守卫语义（三条判据，都可解释、都可测）

| 判据 | 结论 | 理由 |
|---|---|---|
| 订单已不在世界里（被删除） | **Fatal** | 等下去没有意义 ⇒ 立刻失败 |
| 计划涉及的工作台不再被选中（主台/从台被换掉或取消选择） | **Blocked** | 玩家可以选回来 ⇒ 值得等 |
| 计划还要用到的器物已不在工作台（改容量裁掉、切换主台搬走） | **Blocked** | 世界可能被改回去 ⇒ 值得等 |

补充规则：

* **恢复即继续**：窗口内守卫重新成立时，本 tick 若已到点就顺手推一个单位，不必再等一个间隔。
* **宽限窗口**默认 `ProcessDefaults.GraceTicks = 4` 刻，可由 `ProcessDefinition` 单次覆盖。
* **同步轨不挂守卫**：它在一个调用里跑完，世界不会中途变化（双轨制的取舍之一）。

## 4. 验证

### 4.1 九步门

| 项 | P2b/P2c 基线 | P3a 实测 |
|---|---|---|
| 主测试 | `... + 22 deterministic` | **`0 bugs / 2000 random + 0 bugs / 3000 capacity + 24 deterministic`** |
| 文档口径 | `docs baseline: OK (9 carriers say 22)` | `OK (9 carriers say 24)` |
| 契约快照 / Schema / 协议 / JS | 全部不变 | 全部不变（223 行、`loose=1`、18/10、`JS-OK`） |
| 求解器差分 | `SOLVER-OK (compared=4003, …)` | 逐字相同 |
| 会话回放 | `REPLAY-OK (commands=33, stateBytes=3091, logBytes=8797, journalBytes=1415)` | 逐字相同 |
| 末尾判定 | `VERDICT=OK` | `VERDICT=OK`（18.3 秒） |

### 4.2 两条确定性用例（机械断言里程碑序列，而不是"人眼看日志"）

**S23（阻断 → 宽限耗尽 → 失败）** 断言三件事：

1. 过程被 `StartOrder` 起成；订单以**失败**收尾；
2. 过程日志的里程碑顺序为 `blocked → grace_expired → failed`（用下标比较，不靠文字匹配）；
3. 失败收尾走**既有语义**：跨工作台转移被回滚（器物回主台、从台清空）。

开发期观察到的实际 tick 序列（修正断言前后同一条路径，供排障参考）：
`t0:plan_accepted / t1:started / t1:phase(transfer) / t1:phase(transfer_liquid) / t3:blocked / t7:grace_expired / t7:failed`
—— 第一单位在 t1 照常执行；t3 到点发现主台已被取消选择 ⇒ 阻断；宽限 4 刻（t3→t7）耗尽 ⇒ 失败。

**S24（阻断 → 窗口内恢复 → 成功）** 断言：订单以**成功**收尾，里程碑顺序为 `blocked → resumed → succeeded`。

> 一个真实的坑（已写进用例注释，避免复犯）：订单状态在**收尾之前**一直是"待执行"
> （`EXECUTING` 记在 `OrderEvent` 上，不写回 `Order.Status`），所以"推进到结束"的循环条件
> 必须看**有没有活动过程**，不能看订单状态。

## 5. 与既有语义的一致性（为什么 S23 里器物会回到主台）

失败的收尾走的是**既有的** `OrderService.CompleteOrderExecution` ⇒ `OrderEvent.Fail()` ⇒ `SlaveRollback`：

* **跨工作台的转移回滚**（器物与液体回主台）—— S23 断言的就是它；
* **主台上已发生的操作不回滚**（改形消耗、已倒出的液体）—— `规则.txt` 的设计定案"失败要付代价"，
  既有 S 系列已覆盖。

因此 P3a 没有发明新的失败语义，只是把"世界中途变化"这条**新触发路径**接到了同一台收尾机器上。

## 6. 遗留（下一步）

1. **修饰器层（P3b）**：`Suppress / Require / Grant / Retime / Intensity` + 区间/优先级/冲突解析 +
   `RuleContext`（默认空栈 ⇒ 今天行为不变）。守卫只负责**发现**前置条件不成立；修饰器负责**改变**前置条件本身 ——
   两者合起来才是"某些状态下某一步被 ban"的完整机制。
2. **过程结尾的原因分类**（裁决 #4）：`Aborted` 细分玩家取消 / 契约违约 / 外部事故。
3. **`state.processes[]` 投影 + `process*` 消息 + `cancelProcess` + WS 的 `startOrder`**（P4）；
   届时必须同步 `--check-protocol` 的期望清单（否则第 7 步会红）。

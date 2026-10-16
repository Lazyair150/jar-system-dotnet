# Phase 2b 报告：双轨执行（同步 drain + 异步 tick 驱动）

> 生成时间：2026-10-16　生成者：Codex
> 前置裁决：P2b 选项 **(c) 双轨**（用户，2026-10-16）。
> 格式沿用「板块报告」：职责 → 改动 → 等价性论证 → 验证 → 语义边界 → 遗留。

---

## 1. 板块职责

让"过程"第一次拥有**真实的时间**：新增 `StartOrder`，把订单执行起成一个**由 tick 驱动**的过程，
每到达步间隔（默认 2 刻）推进一步；同时**旧路径一行不破** —— `ExecuteOrder` 仍"即时返回成败"，
全部回归资产继续有效。两条轨**共用同一个执行会话**，所以"计划怎么走、失败怎么算"只有一份定义。

## 2. 改动清单

### 2.1 新增（`src/JarSystem.Core/Process/Execution/`，3 个文件）

| 文件 | 职责 |
|---|---|
| `IProcessAdvancer.cs` | "可被 tick 驱动的工作体"契约：一个单位 = 一次可中断的推进；`OnFinished` 做一次性收尾（必须幂等） |
| `OrderExecutionSession.cs` | **订单执行的分步状态机**：阶段 `MasterPath → MasterGreedy → Transfer → SlavePath → SlaveGreedy → Done`，逐条对齐改造前的分支 |
| `OrderProcessAdvancer.cs` | 把会话接到过程系统上，并在结束时调用 `OrderService.CompleteOrderExecution`（交付 / 落账） |

### 2.2 改动

| 文件 | 改动 |
|---|---|
| `Executor/OrderExecutor.cs` | **编排迁出**：`Execute` 变成"建会话 + 同步 drain"；新增 `BeginSession` / `ApplyPlannedStep` / `RunGreedyStage`；删除三段内联编排 |
| `Services/OrderService.cs` | 拆成 `BeginOrderExecution`（前置校验 + 事件 + 计划 + 会话；**不改世界**）与 `CompleteOrderExecution`（交付 / 失败 / 事件历史 / 订单状态）；`ExecuteOrder` = 二者之间 drain |
| `Process/ProcessSystem.cs` | 在册工作体 `_work`；新增 `AttachAdvancer` / `AdvanceToCompletion`（同步轨）/ `AdvanceActive`（异步轨，按 `NextUnitTick` 判定） |
| `Process/Scheduler/TickPipeline.cs` | 第 ③ 步改为回调 `ProcessSystem.AdvanceActive`（每个过程每 tick 至多一个单位） |
| `Process/Model/ProcessInstance.cs` | 新增 `AdvancedUnits` / `NextUnitTick` / `NoteUnitAdvanced` |
| `Process/ProcessRun.cs` | **提交时**读时钟（原先在 Begin 捕获常量）——否则异步轨的多步会全记成同一个 tick（实测踩到） |
| `JarSystem.cs` | 新增 `StartOrder(index)`；`ExecuteOrder` 改为"过程 + 工作体 + 同步 drain"；新增私有 `BeginOrderProcess` |
| `Sessions/WorldCommand.cs` / `SessionReplay.cs` | 新增世界意图 `startOrder` 并支持回放 |
| `test/JarSystem.Test/Program.cs` | 脚本会话**追加异步段**（`StartOrder` + `Tick(4)`）⇒ 回放门同时覆盖两条轨；`--dump-journal` 追加异步演示 |

## 3. 等价性论证（同步轨逐字不变）

编排搬进会话，但**逐条对齐**，每条对应关系都写进 `OrderExecutionSession` 的类注释：

1. 路径步骤**失败也继续应用其余步骤**，最后按"有无错误 + 是否满足"判定；
2. A 类：路径成功即结束；失败退回贪心，按 `IsSatisfiedBy(master)` 判成败；
3. B 类：主台路径任一步失败 ⇒ **立即失败**（"状态与可行性分析不一致"），不做转移；
4. B 类转移：选择性清单（无清单则全量）；批次失败 ⇒ 失败；随后液体转移 + 切到从台阶段；
5. B 类从台：路径成功即结束；失败 ⇒ 记 fallback 并退回贪心；
6. 贪心批次：一个单位跑完（P3 起才细分）。

**门抓到的一处真实偏差（值得记）**：A 类的计划步骤，改造前记的阶段名是 `solver_path`，
我一开始写成 `master_path` ⇒ **契约快照立刻变红**（`eventHistory[0].operations[0].phase`）。
一个字符串差别靠肉眼几乎发现不了 —— 这正是"契约即产物 + 逐字节快照"的价值。

## 4. 验证

### 4.1 九步门

| 项 | 结果 |
|---|---|
| 主测试 | `0 bugs / 2000 random + 0 bugs / 3000 capacity + 22 deterministic`（不变） |
| 契约快照 / Schema / 协议 / JS | 全部不变（223 行、`loose=1`、18 命令 / 10 消息） |
| 求解器差分 | `SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)`（不变） |
| **会话回放（已含异步段）** | `REPLAY-OK (commands 29→33, stateBytes 2369→3091, logBytes 7748→8797, journalBytes 722→1415)` |
| 末尾判定 | `VERDICT=OK` |

回放门三个数字变化都来自"新增了异步段"（+4 条世界意图、过程日志翻倍）；
关键是：**异步轨的过程日志在回放中逐字节一致** —— 说明"时间也回去了"（tick、步间隔、里程碑全部可复现）。

### 4.2 异步轨实测（`--dump-journal`）

```
startOrder: started=True processId=3 error=-
async: ticksUsed=3 activeRemaining=0 orderStatus=成功

#11 tick=7  plan_accepted | 计划：来源 solver，声明步数 1，步间隔 2 刻
#12 tick=8  started       | 开始：委托 #5
#13 tick=8  phase         | 转移 1 个器物与所需液体
#14 tick=8  phase         | 酒×1
#15 tick=10 step_applied  | pourIn 满罐(高罐,酒)#8 酒
#16 tick=10 phase         | 交付完成
#17 tick=10 succeeded     | 成功：委托 #5（1 步）
```

读法：启动在 tick 7（还没动世界）；第一个单位在 **tick 8**（启动后的下一个 tick）；
第二个单位在 **tick 10**（间隔 2 刻）——**同一个过程的不同步骤落在不同 tick**，
这是"以时间为基础、可观且可回放"的最小证据。

## 5. 语义边界（诚实声明）

1. **粒度是"单位"**：一个计划步骤 = 一个单位；**贪心兜底 = 一个批次单位**（一次跑完）。
   把贪心也做成"先生成计划再执行"是 P3 的活儿。
2. **交付发生在最后一个单位**（同一 tick 内），不单独占一个间隔。
3. **同步轨忽略步间隔**（drain 不等待）—— 这是"旧路径零行为变化"的代价，也是双轨的核心取舍：
   时间只属于异步轨。
4. **过程中世界照常产出**（tick 管线第 ④ 步始终执行）⇒ 异步轨里"判定可能过期"是**预期行为**，
   守卫与前置条件（P3）正是为此准备；同步轨不受影响（一个调用里跑完，世界不变）。
5. **对外契约未动**：`state.processes[]` 投影与 `process*` 消息仍未接（P4）；`startOrder`
   目前只在核心 API 与测试脚本可用，**WS 命令与协议清单没动**（避免过早改协议）。

## 6. 遗留（P3/P4 输入）

1. **守卫与前置条件**：异步轨期间世界可被玩家改变（改容量、切换主台、注入事件）⇒
   每个单位执行前必须校验前置条件（P3），失败进入 `Blocked`，由宽限窗口决定 `Resumed` / `Failed`。
2. **过程结尾的原因分类**（裁决 #4）：把 `Aborted` 细分为玩家取消 / 契约违约 / 外部事故。
3. **`state.processes[]` + `process*` 消息 + `cancelProcess`**（P4）：只有异步轨才有"活动过程"可展示。
4. **WS 命令 `startOrder`**：P4 接入（届时必须同步 `--check-protocol` 的期望清单，否则第 7 步会红）。

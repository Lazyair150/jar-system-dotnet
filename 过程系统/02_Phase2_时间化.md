# Phase 2a 报告：时间机制上线（逻辑时钟 / tick 管线 / 步间隔 / 日志入回放门）

> 生成时间：2026-10-16　生成者：Codex
> 格式沿用「板块报告」：职责 → 问题 → 改动 → 等价性论证 → 验证 → 遗留/待裁决。
> 依据：`设计分析_过程系统内核.md` §3/§5、`00_Phase0_接口规格.md` §7.3、裁决记录（2026-10-16：步间隔取"更平缓"）。

---

## 1. 板块职责

把「时间」从一个**隐含的批处理参数**变成一等机制：世界有了明确的 tick 编号、每个 tick 有固定处理顺序、
过程有了"一步占几刻"的分配规则；并且**过程日志第一次成为回放门的一部分**（不再只是诊断产物）。

## 2. 改动清单

### 2.1 新增（`src/JarSystem.Core/Process/Scheduler/`，3 个文件 107 行）

| 文件 | 职责 |
|---|---|
| `LogicalClock.cs` | 逻辑时钟：整数、只增、从 **1** 开始（0 = 世界尚未推进）；过程日志的 `tick` 就是它 |
| `StepAllocator.cs` | 步间隔：默认 **2 刻/步**（裁决"更平缓"）；可按过程种类覆盖；P3 的 `Retime` 修饰器将在此基础上做区间覆盖 |
| `TickPipeline.cs` | 一个 tick 的**固定顺序**：时钟 +1 → ① 事件（P3）② 修饰器（P3）③ 过程（每过程至多一步）→ ④ 世界产出（由 `ProductionService` 紧随其后） |

### 2.2 改动（`src` 4 个文件 + 测试 1 个文件，+278 / −40）

| 文件 | 改动 |
|---|---|
| `Services/ProductionService.cs` | `Tick(count)` 拆成 `TickStep()`（一个世界步进的产出）+ `FlushFlow()`（流入一次），由门面按 tick 组合 |
| `JarSystem.cs` | `Tick(count)` 成为**时间与过程的唯一推进口**：逐 tick 走管线 + 产出，最后流入一次 |
| `Process/ProcessSystem.cs` | 持有时钟 / 管线 / 分配器；`Begin` 用时钟起点并在此时**解析步间隔**写进实例 |
| `Process/Model/*`、`ProcessRun.cs` | `ProcessDefinition.TicksPerStep`（可覆盖）+ `ProcessInstance.TicksPerStep`；`plan_accepted` 摘要记录"步间隔 N 刻" |
| `test/JarSystem.Test/Program.cs` | `--check-replay` 新增**第三份账**（过程日志）并进判据；`--dump-journal` 的产物因此带真实 tick |

## 3. 等价性论证（为什么"世界行为逐字节不变"）

1. **逐 tick 拆分 ≡ 批量调用**（本例最核心的一条）：
   * 生产者：原实现是"累加 count 次后统一入库"，现在是逐 tick 累加逐 tick 入库；
     `Warehouse.StoreJars` 本就**逐罐**判容量与计数 ⇒ 丢弃计数与库存逐项相同；
   * 液体：原实现本来就是逐 tick 循环 ⇒ 相同；
   * 流入：仍是"**每次宿主 `Tick(count)` 调用一次**"（刻意不改批量口径）⇒ 相同。
   **实测证据**：`0 bugs / 2000 random + 0 bugs / 3000 capacity + 22 deterministic` 与 P1 基线**逐字相同**。
2. **时钟不参与判定**：`LogicalClock` 只被写进过程日志（新产物），不改世界、不进 `GetState()`
   ⇒ 契约快照 223 行、schema 10 类型 `loose=1`、`contract.d.ts` 224 行、协议 18/10 全部不变（已实测）。
3. **步间隔暂不参与执行**（重要，见 §6）：P2a 的宿主仍以同步方式把一次意图走完，
   间隔只是**被解析并记录**（`plan_accepted` 摘要里可见"步间隔 2 刻"），因此不改变任何结果。

## 4. 验证结果（2026-10-16，`CI=1`，耗时 19 秒）

| 项 | P1 基线 | P2a 实测 | 结论 |
|---|---|---|---|
| 主测试 | `0 bugs / 2000 random + 0 bugs / 3000 capacity + 22 deterministic` | **逐字相同** | ✅ |
| 文档口径 | `docs baseline: OK (9 carriers say 22)` | 逐字相同 | ✅ |
| 契约快照 / Schema / `contract.d.ts` | 223 行 / 10 类型 `loose=1` / 224 行 | 全部未变 | ✅ |
| 配置世界 / 协议 / JS | `CONFIG-WORLD-MATCH` / `PROTOCOL-OK (18/10)` / `JS-OK` | 逐字相同 | ✅ |
| 求解器差分 | `SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)` | 逐字相同 | ✅ |
| **会话回放** | `REPLAY-OK (commands=29, stateBytes=2369, logBytes=7748)` | `REPLAY-OK (commands=29, stateBytes=2369, logBytes=7748, `**`journalBytes=722`**`)` | ✅ **新增第三份账** |
| 编译 | 0 error / 0 warning | 0 error / 0 warning | ✅ |
| 末尾判定 | `VERDICT=OK` | `VERDICT=OK` | ✅ |

过程日志现在带**真实 tick**（`--dump-journal` 实测）：

```
#1 p1 tick=3 rev=5  plan_accepted | 计划：来源 greedy，声明步数 0，步间隔 2 刻
#3 p1 tick=3 rev=5  failed        | 失败：委托 #1（主台资源不足）
#4 p2 tick=7 rev=37 plan_accepted | 计划：来源 solver，声明步数 1，步间隔 2 刻
#6 p2 tick=7 rev=37 phase transfer        | 转移 1 个器物与所需液体
#7 p2 tick=7 rev=37 phase transfer_liquid | 酒×1
#8 p2 tick=7 rev=37 step_applied pourIn   | pourIn 满罐(高罐,酒)#7 酒
#10 p2 tick=7 rev=37 succeeded     | 成功：委托 #4（1 步）
```

## 5. 实测发现：**契约里已经写进了浮点漂移**（需要你裁决）

查 `重构报告\_contract\state_keys.txt`（契约快照本身）发现三个值：

```
state.producerStatus.generators[0].accumulated = 0.20000000000000018
state.producerStatus.generators[1].accumulated = 0.8999999999999999
state.producerStatus.generators[2].accumulated = 0.7999999999999999
```

"本该"是 0.2 / 0.9 / 0.8。这不是显示问题：`ShapeProductionLine.Tick` 用 `Math.Floor(Accumulated)` 取整产出，
一旦累加值恰好落在整数**下方**（如 6.999999999999999），就会**少产一个罐子**——
这正是 Gaffer On Games 那篇讲"浮点让确定性几乎不可能"时说的情形（本机同进程内暂时没踩到，跨平台/换运行时就难说）。

**定点化**（内部按千分之一整数累加，对外仍以 double 输出）能根治，但它会**改动上述 3 个契约数值**
⇒ 属于"可观察值变更"，按本项目规矩需要**明确批准 + 有意重建基线**（先例：2026-09-16 那次基线重建）。
因此本回合**没有动它**，列为待裁决项。

## 6. 待裁决：P2b 的关键分叉（把过程改为 tick 驱动）

P2a 已把机制就位，但**过程仍由同步调用走完**。要真的产生"时间感"，必须决定
`ExecuteOrder` 的语义怎么变——这是本回合**刻意没有擅自决定**的地方，因为它会牵动承重不变量。

### 三个选项

| 选项 | 做法 | 代价 | 保留不变量？ |
|---|---|---|---|
| **(a) 同步 drain** | `ExecuteOrder` 保持"即时返回成功/失败"，内部按步间隔把"过程刻度"一次走完 | 时间感只存在于**账里**，玩家看不见（进度无法逐 tick 呈现） | ✅ 全部保留 |
| **(b) 异步** | `ExecuteOrder` 改为"启动 + 由 tick 推进"，完成/失败在随后的 tick 里产生 | 主测试的"可行⇒成功 / 不可行⇒失败"双向断言需要重述（执行期间世界会产出，判定可能过期）；回放门、前端、宿主都要改 | ⚠️ 需要重述（这正是"事件异化过程"的常规化） |
| **(c) 双轨（推荐）** | 旧的 `executeOrder` = 启动 + **同步 drain**（不变量与九步门全保，测试与旧宿主零改动）；**新增** `startOrder` = 异步、由 tick 驱动（新宿主的"时间感"走这条路，P4 接进度消息） | 需要两条路径共用同一套过程机制（工程上就是把执行编排做成**可暂停/可续跑**的分步状态机） | ✅ 旧路径全保；异步路径按新语义定义 |

### 推荐 (c) 的理由

1. 它同时满足两件本来互相冲突的事：**既有回归资产（5000 用例 + 回放门）继续有效**，
   且新宿主能拿到"每 tick 至多一步"的真实时间感；
2. 与本项目一贯的做法一致：**新增路径而不是改写老路径**（例如 S3b 的配置装配就是"新增装配入口，`new JarSystem()` 仍走默认世界"）；
3. 异步路径天然是 P3 的落地点：守卫与前置条件就是为"世界里发生意外"准备的，而同步路径根本不暴露这种可能。

### 无论选哪个，(c)/(b) 都需要的一次重构

把 `OrderService.ExecuteOrder` 里"执行 + 交付 + 事件历史"整段，改成**可暂停的分步过程**
（过程实例持有：当前阶段、剩余步骤、参与者与转移清单），由 `TickPipeline` 每 tick 推进一步。
这是设计文档 §6.3 早就点名的"P2 会把编排迁到 `Process/TickPipeline`"，改动集中在
`OrderService` / `OrderExecutor` / `ProcessSystem` 三处，并需要为异步路径新增 S 用例。

## 7. 回滚方式

```bat
:: P2a 新增/改动的文件：删 Scheduler 目录 + 还原 5 个受版本控制文件
rmdir /s /q src\JarSystem.Core\Process\Scheduler
git checkout -- src\JarSystem.Core\JarSystem.cs src\JarSystem.Core\Services\ProductionService.cs ^
    src\JarSystem.Core\Process\ProcessSystem.cs src\JarSystem.Core\Process\ProcessRun.cs ^
    src\JarSystem.Core\Process\Model\ProcessDefinition.cs src\JarSystem.Core\Process\Model\ProcessInstance.cs ^
    test\JarSystem.Test\Program.cs
```

（`Process/Model/*` 与 `Process/ProcessSystem.cs` 在 P1 时是新增文件，若只回滚 P2a 请按上面的清单局部还原；
回滚后重跑 `verify.bat` 应仍是 `VERDICT=OK`，且 `REPLAY-OK` 行会退回不带 `journalBytes` 的旧形状。）

## 8. 遗留项（P3 输入，已更新）

1. **P2b 的选项裁决**（§6）——这是继续时间化的前提。
2. **产率定点化**（§5）——需批准 + 有意重建契约基线。
3. **修饰器层**（`Suppress`/`Require`/`Grant`/`Retime`/`Intensity`）+ 守卫与前置条件 + `Blocked/Resumed/Failed` 语义（P3）。
4. **过程结尾的原因分类**（裁决 #4 的后续）：`Aborted` 细分玩家取消 / 契约违约 / 外部事故——P4。
5. **`state.processes[]` 投影与 `process*` 消息**（P4）：只有异步路径（(b) 或 (c)）才让"活动过程"非空，投影才有意义。

# Phase 1 报告：过程骨架（瞬发模式，行为逐字节不变）

> 生成时间：2026-10-16　生成者：Codex
> 格式沿用本项目既有的「板块报告」：职责 → 问题 → 改动 → 等价性论证 → 验证结果 → 遗留项。
> 依据：`设计分析_过程系统内核.md`（设计与实施规格）、`00_Phase0_接口规格.md`（接口与契约冻结稿）。

---

## 1. 板块职责

把「订单执行」从**一次瞬时调用**变成**一个有身份、有状态、有步骤账的过程**，为 P2 的时间化
（步间隔、每 tick 一步）、P3 的阻断与临时规则（修饰器）、P4 的对外投影留出结构位置。

**P1 只做骨架，不改任何对外行为**：过程在同一个 tick 内跑完，既有 `OrderEvent` /
`ExecutionLogEntry` / `eventHistory` 照旧写入（双写期）。

## 2. 这次要解决的问题（P0 遗留的三处空缺）

| # | 空缺 | P1 的处置 |
|---|---|---|
| 1 | 订单执行没有任何「进行中」的状态（同步瞬时完成，日志没有时间戳、没有剩余步数） | 新增 `ProcessInstance`（状态机 + 进度 + 时限字段），执行期间它是活的 |
| 2 | 步骤级信息散在事后日志里，没有一条**带过程身份**的账 | 新增 `ProcessJournal`（每一步 / 每个里程碑一条，带 seq / tick / 修订号） |
| 3 | 过程系统要观察内核，但内核不能反向依赖它（否则依赖成环） | 新增 `Abstractions.IProcessRecorder` 作为**依赖倒置缝**：内核只认接口，Process 包实现它 |

## 3. 改动清单

### 3.1 新增（10 个文件，`src/JarSystem.Core/Process/`，共 713 行）

| 文件 | 职责 |
|---|---|
| `Process/Model/ProcessState.cs` | 状态枚举 + 结尾枚举 + **契约字符串映射**（`ProcessContract`，唯一来源） |
| `Process/Model/ProcessKinds.cs` | 过程种类常量（`order_execution` / `workshop_migration`） |
| `Process/Model/JournalKinds.cs` | 日志条目种类常量（13 个，冻结） |
| `Process/Model/JournalEntry.cs` | 日志条目（10 键，`[JsonPropertyName]` 钉死） |
| `Process/Model/ProcessDefinition.cs` | 过程定义（不可变模板） |
| `Process/Model/ProcessInstance.cs` | 过程实例（**状态迁移只经意图方法**；终结幂等、终态后再推进抛异常） |
| `Process/Journal/ProcessJournal.cs` | 追加写日志 + **有界保留**（超限丢最旧一半，摊销 O(1)） |
| `Process/Journal/JournalCodec.cs` | 存档 v1 读写（未知键**报错**，与 `WorldCommandJson` 同策略） |
| `Process/ProcessRun.cs` | 执行句柄 + **记账实现**（实现 `IProcessRecorder`；`started` 惰性写入） |
| `Process/ProcessSystem.cs` | 门面：建过程、在册、终结后收进"最近过程"（有界 64） |

### 3.2 新增（1 个文件，`src/JarSystem.Core/Abstractions/`）

| 文件 | 职责 |
|---|---|
| `Abstractions/IProcessRecorder.cs` | 内核侧记账出口 + `PlanSources` + `ProcessMilestones`（**内核词汇与日志契约分离**） |

### 3.3 改动（5 个受版本控制的文件，+226 / −11）

| 文件 | 改动 | 为什么这样改 |
|---|---|---|
| `JarSystem.cs` | 持有 `ProcessSystem`；新增 `Processes` / `Journal` / `WorldRevision`；`ExecuteOrder` 包成「建过程 → 执行 → 收尾」 | 门面是核心的组合根；`WorldRevision` 复用既有命令日志序号（`_logSeq`），不新增概念 |
| `Services/OrderService.cs` | `ExecuteOrder(index, IProcessRecorder? = null)`；声明计划来源与步数；交付里程碑 | 内核只依赖抽象；默认参数保证既有调用点零改动 |
| `Executor/OrderExecutor.cs` | `Execute(...)` 增加 `recorder`；**每应用一步即记账**；转移/转移液体/降级里程碑 | 复用既有 `ExecutionLogEntry` 词汇，不发明第二套名字 |
| `test/JarSystem.Test/Program.cs` | 新增 `--dump-journal` 模式（导出 + 三项自检 + 一段必定成功的演示场景） | 新产物需要可检查；演示场景保证 `step_applied` 与 `phase` 两类路径都被覆盖 |
| `.gitignore` | 忽略 `过程系统/_journal/` | 与 `重构报告/_contract/*.txt` 同一口径：运行产物忽略，结论进报告 |

## 4. 等价性论证（为什么对外行为逐字节不变）

1. **`recorder` 为 null 时全部是空操作**：`OrderService`/`OrderExecutor` 的新参数都有默认值，
   既有调用点（含 2000 + 3000 用例、`StrategyExperiment`、xUnit 测试）不传 ⇒ 代码路径与重构前逐字相同。
2. **不投影进契约**：`StateDto` / `StateSerializer` / WS 消息 / 命令名**一处未动** ⇒
   契约快照 223 行、schema 10 类型、`contract.d.ts` 224 行、协议清单（18 命令/10 消息）必然不变。
3. **不参与判定**：过程只读取世界（读快照、读日志序号），从不改世界；真正改世界的仍是既有
   `Command` / `Workshop` 意图方法 ⇒ 世界状态序列不可能改变。
4. **不参与回放比对**：`--check-replay` 比对的是 `GetState()` 与 `WorldCommand` 日志；
   过程日志是**第三份**产物，不在这两者里 ⇒ 回放门的数字（`commands=29, stateBytes=2369, logBytes=7748`）不变。
5. **有界日志不影响正确性**：裁剪只丢"最旧的诊断记录"，不参与任何判定；`seq` 单调，缺口可查。

## 5. 验证结果（2026-10-16，本机）

### 5.1 九步门（`重构报告\verify.bat`，`CI=1`，耗时 17.8 秒）

| 项 | 基线（P0 记录） | P1 实测 | 结论 |
|---|---|---|---|
| 主测试 | `0 bugs / 2000 random + 0 bugs / 3000 capacity + 22 deterministic` | **逐字相同** | ✅ |
| 文档口径 | `docs baseline: OK (9 carriers say 22)` | 逐字相同 | ✅ |
| 契约快照 | 223 行 | 未变化（`git diff` 无该文件） | ✅ |
| Schema | 10 类型 / `total loose=1` | 逐字相同 | ✅ |
| 前端类型 | `contract.d.ts` 224 行 | 未变化 | ✅ |
| 配置世界 | `CONFIG-WORLD-MATCH` | 逐字相同 | ✅ |
| 协议 | `PROTOCOL-OK (commands=18, messages=10)` | 逐字相同 | ✅ |
| JS | `JS-OK` | 逐字相同 | ✅ |
| 求解器差分 | `SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)` | 逐字相同 | ✅ |
| 会话回放 | `REPLAY-OK (commands=29, stateBytes=2369, logBytes=7748)` | 逐字相同 | ✅ |
| 末尾判定 | `VERDICT=OK` | `VERDICT=OK` | ✅ |
| 编译 | 0 error / 0 warning | 0 error / 0 warning | ✅ |

### 5.2 新产物：过程日志（`--dump-journal`）

```
JOURNAL-OK (processes=3, entries=13, steps=2, phases=3)
kinds: failed=1, phase=3, plan_accepted=2, started=3, step_applied=2, succeeded=2
```

过程 2（演示场景）的完整账（这也是 P1 最直观的成果）：

```
#4 plan_accepted | 计划：来源 solver，声明步数 1
#5 started       | 开始：委托 #4
#6 phase transfer        | 转移 1 个器物与所需液体
#7 phase transfer_liquid | 酒×1
#8 step_applied pourIn   | pourIn 满罐(高罐,酒)#7 酒
#9 phase delivery        | 交付完成
#10 succeeded    | 成功：委托 #4（1 步）
```

三项自检（都对应设计规格 §7 的不变量）：

1. **读回**：日志按存档路径解码成功（未知键会当场抛，不会静默丢字段）；
2. **终结幂等**：已终结的过程再推一步、再终结一次、再取消 ⇒ 全部安全忽略（不抛、不新增条目）；
3. **进度一致**：每个过程的"已应用步数" == 它在日志里的 `step_applied` 条数。

## 6. 实现过程中真实咬到的三处问题（都已在 P1 内修掉）

| # | 现象 | 根因 | 处置 |
|---|---|---|---|
| 1 | 演示订单被判成 `greedy` 计划（明明有求解器可行结论） | 计划来源按"路径条数 > 0"判定，而划分搜索的可行解**主台路径或从台路径都可能为空** | 改为按**勘验结论**判定：`FeasibilityResult.Feasible == true` ⇒ `solver`；步数取主台 + 从台路径之和 |
| 2 | 演示过程的日志显示"成功（0 步）"，一步没记 | 从台阶段的 `ExecuteViaSolverPath(...)` 调用**漏传记账器**（`Execute` 里那处传了，`ExecuteTypeB` 里那处没传） | 补传；这条只有"账与进度对不上"才暴露 ⇒ 也说明第 5.2 节自检 ③ 有真实价值 |
| 3 | 构造演示场景时两次被判不可行 | 撞到两条**既有设计定案**：① 禁液不得**游离存储**（`AddLiquid("酒")` 在禁酒台上会被拒）；② 满罐需求**不能靠改形满足**（一罐一操作 ⇒ 改形只服务空罐需求） | 不改规则（规则是对的），改场景构造；两条陷阱已写进 `Program.cs` 的注释 |

## 7. 与 `00_Phase0_接口规格.md` 的偏离（诚实记录）

| 规格里写的 | 实际做的 | 理由 |
|---|---|---|
| §6.1 "Phase 1 只建前三个（Model / Rules / Execution）" | 实际建 **Model / Journal / ProcessRun+ProcessSystem** | 规格那句本身是笔误（与 §8 自相矛盾）。P1 不需要 `Rules`（修饰器属 P3）；`Execution` 在 P1 只有"记账"这一半职责，已并入 `ProcessRun`；**Journal 是 P1 的真正主体**，规格 §8 也是这么写的。已回头修正 §6.1 |
| §4 的 `ProcessStep` / `ProcessPlan.Steps` | **未建**（只保留"声明步数"） | P1 是瞬发模式，步骤序列不参与排程；P2 需要"每 tick 一步"时再落地 `ProcessStep`（届时它同时承担"守护声明"） |
| `StepRunner` / `GuardResult` / `PreconditionChecker` | **未建** | 它们服务的"阻断/失败语义"属 P3；P1 建了也无处用，属提前抽象 |
| 改 `Workshop` / `StateDtos` / `StateSerializer`（投影 `processes[]`） | **未改** | P1 的瞬发模式让"活动过程"恒为空 ⇒ 投影没有信息量，却会**改动契约与快照基线**。P2 把过程拉到跨 tick 之后再做投影，收益才是真的 |
| `ProcessKinds.WorkshopMigration` | 建了常量但**未接** | 迁移过程的改造要等 P2 的时间化（它天然跨 tick），P4 再做 |

## 8. 遗留项（P2 的输入）

1. **时间化**：`LogicalClock` + `TickPipeline`（每 tick 每过程至多一步）+ 步间隔默认值
   （裁决：**更平缓**，建议执行类 2 tick/步、迁徙类 2 tick/步，均可在 P2 调整——它是默认值）。
2. **浮点定点化**：`Producer.Rates` / `Accumulated` 仍是 `double`；时间化之后"跨平台可复现"才真正重要，
   因此 P2 前置加固（对外 JSON 不变）。
3. **过程纳入回放门**：P2 起把过程日志纳入 `--check-replay` 的比对（判据：重放出的过程日志与原日志逐字节一致）。
4. **`Reset()` 与过程日志的关系**：当前 `Reset()` **不清空**过程日志（它是 append-only 的历史）。
   是否清空属 P4 决策（涉及"世界重来"的语义）。
5. **取消/失败的原因分类**（裁决 #4 的后续）：你已同意"允许玩家取消，也会存在其它导致取消或直接失败的原因"
   ⇒ P4 需要在过程结尾里补**原因分类**（`Aborted` 细分：玩家取消 / 契约违约 / 外部事故…）。
   P1 的 `ProcessOutcome` 只到三态 + `reason` 字符串，**结构上已可承载**，细分留到那一步。

## 9. 回滚方式

改动集中在 **6 处**（5 个受版本控制文件 + 1 个新目录）：

```bat
:: 1) 删新目录与新接口
rmdir /s /q src\JarSystem.Core\Process
del src\JarSystem.Core\Abstractions\IProcessRecorder.cs

:: 2) 还原 5 个受版本控制文件（P1 前它们没有未提交改动，可直接 checkout）
git checkout -- .gitignore src\JarSystem.Core\JarSystem.cs ^
    src\JarSystem.Core\Services\OrderService.cs src\JarSystem.Core\Executor\OrderExecutor.cs ^
    test\JarSystem.Test\Program.cs
```

（回滚后重跑 `verify.bat` 应当仍是 `VERDICT=OK`；P1 未改动任何契约产物，因此无需重建基线。）

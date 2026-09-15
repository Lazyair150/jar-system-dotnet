# 板块 E — Executor（修复报告）

> 范围：`Executor/IndependentExecutor.cs`（现 `OrderExecutor`）、`Executor/SolverStepApplier.cs`、`Executor/GreedyPlanBuilder.cs`、`Executor/JarSelector.cs`、`Executor/JarStrategies.cs`、`Executor/WorkshopStageResult.cs`
> （原 `Executor/SolverPathAdapter.cs` 已于**阶段 3-D 废弃删除**，见 `../06_阶段3D_路径步骤强类型化.md`）
> 阶段：1（批 3）+ 2（批 2C）+ 3（策略模式）
> 验证状态：**待验证**（`重构报告\verify.bat`）

---

## 1. 板块职责

把「可行性分析产出的路径」或「贪心策略」落到真实工作台上：A/B 类订单的执行编排、求解器路径单步应用（按 jarId 定位 + 状态预检）、贪心回退、罐子选择策略。

## 2. 重构前的问题（引用审计）

| 编号 | 问题 | 证据 |
|---|---|---|
| E1 | `Execute` 返回 4 元组（成功/操作/错误文案/原因），错误用字符串承载 | `IndependentExecutor.cs:16-26` |
| E2 | 判断已废弃的 `"combined_under_slave"`/`"two_phase_simulation"`（第七轮后不存在的检查方式） | `:69` |
| E3 | 领域层解析 UI 字典 + 兼容旧嵌套格式（弱类型契约、LoD 破坏） | `:175-260` |
| E4 | 策略用 enum + `switch` 内联在 `FindJar`；`Random` 分支每次 `new Random()` | `:263-280` |
| E5 | `HasFreeLiquid` 与 `Workshop.CanDrink` 语义重复 | `:288-291` |
| E6 | `ExecOnWs/TryFull/TryEmpty` 95 行贪心逻辑 + 3 处自递归 | `:293-386` |
| E7 | 直接改 `master.LiquidStorage` | `:139-143` |
| E8 | 文件级 internal 类 `WsResult` 藏在文件底部 | `:389` |
| E9 | 类名 `IndependentExecutor` 语义空洞 | 全文件 |

## 3. 改动清单

### 3.1 阶段 1（批 3）

| 改动 | 说明 |
|---|---|
| 类名 `IndependentExecutor` → `OrderExecutor` | 与 `JarSystem.Executor` 属性类型同步（外部无其他引用） |
| `WsResult` → `WorkshopStageResult` | 命名规范化；阶段 2C 独立成文件 |
| 删除死条件 `"combined_under_slave"`/`"two_phase_simulation"` | 改用 `Constants.CheckMethod.SLAVE_ONLY`/`PARTITION`（这两个值在任何路径下都不会写入 `CheckMethod`） |
| `HasFreeLiquid` | 改委托 `Workshop.HasUsableLiquid`（语义单一来源） |
| 直接改 `LiquidStorage` → 意图方法 | `slave.TryReceiveLiquid` + `master.RemoveLiquid`（容量感知的部分填充语义不变） |
| `foreach(...) IsMarked = false` → `slave.ClearMarks()` | 封装 |
| 字面量 → 常量 | `Constants.OrderType.A/B`、`Constants.OrderPhase.SLAVE` |

### 3.2 阶段 2C（拆分为 4 个协作者）

| 新类 | 职责 | 与原实现的对应 |
|---|---|---|
| ~~`SolverPathAdapter.cs`~~ | **阶段 3-D 已废弃并删除**：步骤读取不再需要适配器（`SolverStepDto` 属性直读）。原职责（旧嵌套 `operation` 子字典拍平 + `ji/ts/tl/cst` 映射 + 9 个读取器）经查**在现代码库不可达** | 原 `NormalizeStep` + 散落的 `step.TryGetValue(...)`；删除论证见 `../06_阶段3D_路径步骤强类型化.md` §5 |
| `SolverStepApplier.cs` | 单步应用：按 `jarId` 首选定位 → 执行前形状/液体预检 → `MATCH/POUR_IN/POUR_OUT/RESHAPE` 分派（RESHAPE 按 `reshapeMode`）。**3-D 后签名改为 `Apply(Workshop, SolverStepDto, phase)`** | 原 `ApplySolverStep`，逐行照搬 |
| `GreedyPlanBuilder.cs` | 贪心回退：`ExecOnWs`/`TryFull`/`TryEmpty`/`HasFreeLiquid`（含「就简原则」与自递归） | 整体迁入，逐行照搬 |
| `JarSelector.cs` + `WorkshopStageResult.cs` | 罐子选择器 / 阶段结果类型 | 原 `FindJar`/`CountInWorkshop` / 文件底部的 `WorkshopStageResult` |

`IndependentExecutor.cs` 由 390 行 → 约 165 行编排（`Execute`/`ExecuteViaSolverPath`/`ExecuteTypeA`/`ExecuteTypeB` + `SelectionStrategy` 转发属性）。

### 3.3 阶段 3（策略模式）

`JarSelector` 退化为「枚举 → 策略实例」注册表 + 候选过滤；新增 `JarStrategies.cs` 的 5 个 `IJarSelectionStrategy` 实现（FIFO / LIFO / RareShapeFirst / Random / FirstMatch 兜底），每个**逐行对应**原 switch 的一个分支。

## 4. 等价性论证（要点）

1. **搬运而非改写**：`ApplySolverStep`、`TryFull`、`TryEmpty`、`ExecOnWs`、`NormalizeStep`、`FindJar` 的方法体逐行照搬；只把字典读取换成适配器调用、`FindJar` 换成 `_selector.Select`。比较运算符、默认值、日志 `Action` 字符串（`direct_match`/`pourIn`/`pourOut`/`reshape`/`reshape_full`/`use_existing`/`pour_out_and_use`）、递归结构均未变。
2. **读取语义**：`ReadString` = 原 `v as string`；`ReadInt` = 原 `v as int?`。
3. **选择语义**：`Select(jars, predicate)` = 原 `FindJar`（先过滤再分流）；`RareShapeFirst` 仍用**完整列表**统计同形空罐；`default` 分支保留为 `FirstMatchJarSelection`（枚举值越界时行为不变）。
4. **策略可变性**：`OrderExecutor.SelectionStrategy` 转发共享 `JarSelector`，`StrategyExperiment` 的 `sys.Executor.SelectionStrategy = st;` 无需改动、影响一致。
5. **可见性零变化**：`WorkshopStageResult` 仍 internal；`JarSelectionStrategy` 枚举仍 public（同命名空间）。
6. **A/B 编排不变**：`Execute` 的分派条件、`ExecuteTypeB` 的主台路径 → 选择性转移 → 从台路径/降级顺序、失败文案（"主台路径执行失败，状态与可行性分析不一致"/"从台容量不足，无法接收转移清单中的全部罐子"/"从台资源不足"）全部保留。

## 5. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| `dotnet build` | 0 error / 0 warning | 🟡 **部分证据**：`src\JarSystem.Core\bin\Release`、`test\JarSystem.Test\bin\Release`、`test\StrategyExperiment\bin\Release` 产物存在且**晚于最后一次代码改动** ⇒ Core 与两个测试工程在 Release 下构建成功；**warning 计数仍未知**（需 `verify.bat`） |
| 全测试 | `0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic` | ❓ 该工程跑过（Release 产物在），但结论行只在控制台，待回贴 |
| **策略实验** | FIFO 最高；四种策略的相对排序与 `问题/问题十测试记录-重跑.txt` 一致 | ✅ **已实证**：FIFO 44.96%（严格最优 1330）、LIFO 40.26%（585）、RareShapeFirst 27.13%（3）、每模式最优 520/170/0/0 与重构前基线**逐值一致**；Random 23.31% vs 基线 23.30%（该策略每次 `new Random()`，预期内波动）。⇒ **罐子选择（FIFO/LIFO/RareShapeFirst）、贪心回退路径、`Tick`/流入推进在重构后行为不变**——本板块 2C 拆分与阶段 3 策略替换的等价性由此得到实证 |
| B 类场景 | S16/S17/S19（选择性转移、液体只转需要的量、工具罐回流）PASS | 待回贴 |

## 6. 遗留项

| 项 | 计划 |
|---|---|
| `Execute` 4 元组返回（E1） | 阶段 3-3：改为结果对象（含操作日志与错误码），同时消除 `OrderEvent` 的类型判定日志分支 |
| ~~领域层读取弱类型字典（E3）~~ | ✅ **已完成（阶段 3-D）**：`SolverStepDto` 强类型化，`SolverPathAdapter` 整个类被删除；执行端与生产端全部编译期受检（见 `../06_阶段3D_路径步骤强类型化.md`） |
| `RandomJarSelection` 的 `new Random()`（E4 残余） | **待你裁决**：改为注入式随机源属行为变更（会改变随机策略序列） |
| 贪心构建器 95 行（E6） | 已独立成类；是否再拆 `TryFull`/`TryEmpty` 的策略化待评估（当前职责单一，暂不动） |
| 文件重命名 | `IndependentExecutor.cs` 文件名未跟上类名，需手动重命名为 `OrderExecutor.cs`（我的工具无法删/改名文件） |

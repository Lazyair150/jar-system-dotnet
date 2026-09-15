# 板块 C — Orders（修复报告）

> 范围：`Orders/Order.cs`、`Orders/OrderEvent.cs`、`Orders/WorkshopSwitchEvent.cs`、`Orders/FeasibilityChecker.cs` 及 `Orders/Feasibility/`（`PartitionSearch`、`JarMatcher`、`RequirementOrdering`、`LiquidRequirementCalculator`、`PathSummaryFormatter`、`FeasibilityResultFactory`、`FeasibilityContext`、`FeasibilityChecks`）、`Orders/Switching/`（`SwitchCases`、`JarValuePolicy`、`ResourceTransferrer`、`SwitchOperationLog`）
> 阶段：1（批 2/3）+ 2（批 2B/2D）+ 3（责任链、结果工厂、路径摘要器）
> 验证状态：**待验证**（`重构报告\verify.bat`）

---

## 1. 板块职责

订单领域：需求建模与校验（`Order`/`Requirement`）、订单生命周期与交付（`OrderEvent`）、可行性分析（`FeasibilityChecker` + 检查链 + 划分搜索）、主台切换的资源重组（`WorkshopSwitchEvent` + 分支策略）。

## 2. 重构前的问题（引用审计）

| 编号 | 问题 | 证据 |
|---|---|---|
| C1 | `Order` 静态 ID 计数器 | `Order.cs:11,21` |
| C2 | `Status`/`FeasibilityResult`/`Requirements`/`Type` 可被外部任意改写，状态机无唯一入口 | `Order.cs:14-17`；`JarSystem.cs:232,239` |
| C3 | 空罐需求用 `Liquid == null` 表达（null 语义重载） | `Order.cs:77-78` |
| C4 | `FeasibilityResultData` 15 个可变字段 + `List<object>` 路径（弱类型契约） | `Order.cs:81-100` |
| C5 | `SolverStep/SolverOperation/JarState` 缩写属性名（`Ji/Ts/Tl/Cst/Cl`） | `Order.cs:127-157` |
| C7 | `CheckTypeB` 90 行巨型方法 | `FeasibilityChecker.cs:53-174` |
| C8 | 液体需求计算直接链式访问 `step.Operation.Ji/Before[...]` | `FeasibilityChecker.cs:181-215` |
| C9 | 路径摘要生成中文 UI 文案（表现层关注点泄漏进领域层） | `FeasibilityChecker.cs:275-320` |
| C10 | `OrderEvent` 12 个可写公共状态 | `OrderEvent.cs:15-26` |
| C11 | `command is XxxCommand` 类型判定拼日志（新增命令类型必须改此处） | `OrderEvent.cs:63-67` |
| C12 | `CurrentPhase` 字符串状态机无合法迁移约束 | `OrderEvent.cs:19,44,101` |
| C13 | 回滚直接改 `LiquidStorage`、自算容量 | `OrderEvent.cs:204-212` |
| C14 | `WorkshopSwitchEvent` 301 行 5 职责混合、`JarValue` 内联规则 | `WorkshopSwitchEvent.cs:*` |
| C15 | Case2 临时改写规则对象 `AdditionalMarks` | `WorkshopSwitchEvent.cs:111-114,205-206` |
| C16 | 禁令判断 3 份拷贝（其中 `FeasibilityChecker.IsBanned` 是完全无调用者的死方法）；满罐优先排序 3 份；交付匹配 3 份；`JarState` 投影 6 处 | 审计 §2.7 |

## 3. 改动清单

### 3.1 阶段 1（封装/命名/死代码）

| 文件 | 改动 |
|---|---|
| `Order.cs` | `Status`/`FeasibilityResult` 改 `private set` + 新增 `MarkSucceeded`/`MarkFailed`/`RecordFeasibility`（状态机唯一入口）；`Requirements`/`Type` 改只读；删除无调用者的 `GetEmptyRequirements`/`GetFullRequirements`；`SolverOperation` 6 个缩写属性对齐 JSON 字段（`JarIndex/TargetShape/TargetLiquid/CostLiquid/Consumed/Description`）；`type = "A"` → 常量；**阶段 2A**：删除 `_idCounter`，构造追加可选 `IIdGenerator` |
| `OrderEvent.cs` | 三处 `foreach(...) IsMarked = false` → `Master/Slave.ClearMarks()`；回滚液体改 `Slave.RemoveLiquid` + `Master.TryReceiveLiquid`；`CurrentPhase`/`Order.Type` 字面量 → `Constants.OrderPhase.*`/`Constants.OrderType.*` |
| `WorkshopSwitchEvent.cs` | `Jars.Remove` → `TransferOut`；`LiquidStorage[l] = 0` → `DrainLiquid`；`JarValue` 改用 `IsAdditionalMarked`；**阶段 2D**：整体拆为分支策略 + 价值策略 + 转移器 + 日志（301 → 68 行） |
| `FeasibilityChecker.cs` | 删除私有死方法 `IsBanned(WorkshopSnapshot, string)`；**阶段 2B/3**：算法迁至 `PartitionSearch`，结果构造迁至 `FeasibilityResultFactory`，路径摘要迁至 `PathSummaryFormatter`，输入归 `FeasibilityContext`，分派改责任链（321 → 65 行） |

### 3.2 阶段 2/3 的结构拆分

| 新类 | 职责 | 与原实现的对应 |
|---|---|---|
| `Feasibility/PartitionSearch.cs` | 2^N 需求子集划分搜索（`PartitionSearchRequest`/`PartitionSearchOutcome`） | 原 `CheckTypeB` 主体，**逐行照搬** |
| `Feasibility/JarMatcher.cs` | 需求 ↔ 罐子匹配唯一实现（真实罐子 / 求解器状态两个重载 + `MatchAll`） | 收敛 `Order.IsSatisfiedBy`、划分搜索的交付罐匹配、`OrderEvent.Deliver` 三份 |
| `Feasibility/RequirementOrdering.cs` | 「满罐需求优先」唯一实现 | 收敛 3 处 `OrderBy(r => r.Liquid != null ? 0 : 1)` |
| `Feasibility/LiquidRequirementCalculator.cs` | 从台阶段所需液体峰值计算 | 原 `ComputeRequiredLiquids`，逐行照搬 |
| `Feasibility/PathSummaryFormatter.cs` | 路径步骤 → **`SolverStepDto` 强类型列表**（阶段 3-D 前为 14 字段字典；14 键现由 `Orders/SolverStepDto.cs` 的 `[JsonPropertyName]` 钉死）+ 命中/操作统计 | 原 `MakeSummary`/`ExtractDirectMatches`/`ExtractOperations` |
| `Orders/SolverStepDto.cs`（**阶段 3-D 新增**） | 路径步骤对外模型：14 个 `[JsonPropertyName]`，声明顺序 = 原字典插入顺序；生产端与消费端双向编译期受检 | 取代 `PathSummaryFormatter` 里手写的字典键 + `SolverPathAdapter` 的读取器 |
| `Feasibility/FeasibilityResultFactory.cs` | `FeasibilityResultData` 字段拼装唯一来源 | 原 `BuildResult`/`BuildSuccessResult` |
| `Feasibility/FeasibilityContext.cs` | 检查链共享输入（需求排序 + 两工作台规则信息） | 原 `BuildWorkshopInfo` 迁出为 `WorkshopInfoFactory` |
| `Feasibility/FeasibilityChecks.cs` | `IFeasibilityCheck` + `SinglePhaseCheck`/`SlaveOnlyCheck`/`PartitionCheck` | 原 `if (A) CheckTypeA else CheckTypeB` 的链式等价改写 |
| `Switching/SwitchCases.cs` | `BanComparison`、`ISwitchCase` + `SameBansCase`/`DifferentBansCase` | 原 `BansAreSame`/`ExecuteCase1`/`ExecuteCase2`，逐行照搬 |
| `Switching/JarValuePolicy.cs` | 罐子对新主台的五级价值排序 | 原 `JarValue` |
| `Switching/ResourceTransferrer.cs` | 罐子按价值转移、液体按禁令/容量转移或丢弃 | 原 `TransferAll` |
| `Switching/SwitchOperationLog.cs` | `SwitchLogEntry` + 只读日志 | 原 `Operations` 可变 List |

## 4. 等价性论证（要点）

1. **责任链分派等价**：`SinglePhaseCheck` 仅当 `Type == A` 参与，`SlaveOnlyCheck`/`PartitionCheck` 仅当 `Type != A` 参与 —— 与原来的 `else` 分支一致（非 A 的第三种类型仍走 B 路径）；A 类必然在第一个检查终止；B 类失败时仍返回 `lastBlockage ?? "资源冲突或液体不足"`。
2. **状态机封装**：`MarkSucceeded/MarkFailed/RecordFeasibility` 的方法体即原来的赋值语句，取值与时机不变。
3. **交付匹配收敛**：三处本就是同一贪心规则（按需求顺序、首次命中、命中即占用），收敛后各调用点仍按原方式消费（布尔 / 收集 Id / 收集罐子）。
4. **切换事件**：分支选择由 `BanComparison.Same` 决定（原 `BansAreSame` 逐行照搬）；`CaseType` 常量值不变；`Operations` 元素与顺序不变；异常仍回滚双工作台并追加 `error` 项。
5. **划分搜索**：`mask` 升序、主/从归属位运算、`Path[^1].After/AfterLiquids` 真实终态、`budget = 从台罐位 − 原有无罐 − 交付罐数` 与 `budget < 0 → continue`、液体池 `Math.Min` 截断、转移清单收集、末次 blockage 记录全部照搬；求解器实例由「每次新建」改为复用注入实例（`Solve` 入口自重置状态，入参在 `SolverState` 构造时深拷贝，故行为等价）。

## 5. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| `dotnet build` | 0 error / 0 warning | 待回贴 |
| 随机 + 容量 + 确定性 | `0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic` | 待回贴 |
| 关键确定性用例 | S1（满罐改形路径）、S13/S14（禁液令边界）、S15–S19（划分搜索/选择性转移/工具罐回流）、S20（撤销账目）全部 PASS | 待回贴 |
| 契约快照 | 路径步骤 14 字段与 `feasibilityResult` 10 键不变 | 待回贴 |

## 6. 遗留项

| 项 | 计划 |
|---|---|
| `SolverPath`/`MasterPath` 仍是 `List<object>`（14 键字典） | 阶段 3-1b：升级为强类型 `SolverStepDto`，需同步改执行器读取路径（风险集中，单列） |
| `OrderEvent.ExecuteCommand` 的 `is XxxCommand` 日志分支（C11） | 阶段 3-3：命令自报日志或日志装饰器 |
| `CurrentPhase` 无合法迁移约束（C12） | 已用 `Constants.OrderPhase` 常量收敛；是否引入迁移表待评估（当前 3 阶段，收益有限） |
| Case2 仍直接改写 `AdditionalMarks`（C15） | 待 `Workshop` 标记只读化（阶段 3 后续），需要作用域对象 API |
| `Requirement.Liquid == null` 语义（C3） | 记录为「评估后不引入 Null Object」（见《03 阶段3》§1） |
| `FeasibilityResultData` 的 `DirectSatisfactions`/`OperationsNeeded` 只写不读 | 保留（前端历史兜底读取），若将来确认无用可删 |

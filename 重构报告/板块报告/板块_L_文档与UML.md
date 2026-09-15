# 板块 L — 文档 / UML / Godot（修复报告）

> 范围：`UML/*.puml`、`UML.txt`、`Godot迁移指南.txt`、`Godot移植设计对话记录.md`、`问题/*.md`、`重构报告/*`
> 阶段：贯穿全程（每阶段末同步）
> 验证状态：文档改动无需编译；渲染需你在 plantuml.com 或 VS Code 插件复核

---

## 1. 板块职责

承载「行为契约」与「设计依据」：规则定案（`规则.txt`）、架构与设计（`问题/系统设计与构造.md`）、UML 图（8 个 `.puml`）、Godot 迁移指南（移植时的权威行为清单）、以及本次重构的全套报告（`重构报告/`）。

## 2. 本次重构中的文档同步（已做）

| 文件 | 改动 |
|---|---|
| `UML/02_ClassDiagram.puml` | ① `IndependentExecutor` → `OrderExecutor`，方法表按拆分后更新（`ApplySolverStep/NormalizeStep/ExecOnWs/TryFull/TryEmpty/FindJar/HasFreeLiquid` 已迁出，改为列出 `SolverStepApplier`/`GreedyPlanBuilder`/`JarSelector`）；② `+ GetState() : Map` → `: StateDto`；③ 新增 4 个 package 块（`Abstractions` 接口、`Services`、`Feasibility` 内部协作、`Switching` 内部协作）与实现/依赖关系（`..\|>` 实现、`-->` 关联）<br>**阶段 3-D 追加**：删除 `SolverPathAdapter` 类块 → 新增 `SolverStepDto` 类块（14 字段）；`SolverStepApplier.Apply(ws, step : SolverStepDto, phase)`；关系改为 `FeasibilityResultData *-- SolverStepDto`、`PathSummaryFormatter ..> SolverStepDto : 产出`；另修正 `FeasibilityChecker` 的过期方法表（`CheckTypeA/B`、`MakeSummary` 早已迁出）与 `..> ISolver` 标注。<br>**批 A 追加**：Commands 包补 `CommandExecutor` 类块、Services 包补 `ConsoleSystemLog`；新增 `ConsoleSystemLog ..\|> ISystemLog`、`JarSystem --> ISystemLog : Log`、`CommandExecutor ..> ISystemLog : 回滚失败留痕`、`OrderEvent --> CommandExecutor : owns`；`JarSystem` 属性表加 `+ Log : ISystemLog`。<br>**批 E 追加**：`JarSystem` 属性表 `Workshops/Orders/EventHistory` 由 `List` 改为 `IReadOnlyList`，并补 9 个意图方法（`AddWorkshop`/`ClearWorkshops`/`AddOrder`/`ClearOrders`/`SetMasterIndex`/`SetSlaveIndex`/`ResetProducerModule`/`SetLiquidProductionRate`/`SetFlowSettings`）。<br>**P1/P4-a 追加**：Orders 包补 `AutoCheckResultDto`；Services 包补 `ProducerStatusDto`/`ProducerInfoDto`/`GeneratorStatusDto`/`ProducerWarehouseDto`；新增关系 `StateDto *-- ProducerStatusDto`、`OrderService ..> AutoCheckResultDto`、`AutoCheckResultDto --> FeasibilityResultData`、`FeasibilityResultData *-- SolverStepDto`；`JarSystem` 属性表加 `+ Rng : Random`（T4 注入），并补 `JarSystem ..> OrderExecutor → JarSelector → RandomJarSelection` 的随机源注入关系。<br>**S1/S2/S3 追加（本轮）**：Config 包补 `Vocabulary`（形状/液体目录唯一权威）、`WorldOptions`（世界即数据）+ `FlowOptions`/`ProducerOptions`/`WorkshopOptions`/`NamePoolOptions`/`WorldValidationResult`/`WorldOptionsJson`；Domain 包补 `WorkshopCapability`，并把 `Workshop` 的字段表改为 `Capability` + 只读委托视图 + `ReplaceCapability` + 临时标记；新增关系：`Workshop *-- WorkshopCapability`、`WorkshopSnapshot *-- WorkshopCapability`、`WorkshopInfo --> WorkshopCapability`（求解器与领域层同源）、`Jar/Workshop/ProducerModule/Order ..> Vocabulary`、`JarSystem --> WorldOptions`、`SystemBootstrapper ..> WorldOptions` |
| `UML/04_Component_模块边界.puml` | **新建（P1/P4-a 之后）**：contract-first 模块边界图 —— 核心零依赖 → 契约产物（`state.schema.json` / `state_keys.txt`）→ 多宿主（Web/CLI/Godot）+ 客户端类型生成 + 验证模块；并把三处"明确不引入"（SignalR / OR-Tools / DI 容器）写成 note 挂在对应组件上 |
| `UML/07_Component_架构.puml` | 组件名 `IndependentExecutor` → `OrderExecutor` |
| `Godot迁移指南.txt` | 第 13 行（`GetState()` 返回 `StateDto`，JSON 键与旧字典一致）；2.3 节 UI 绑定思路；§四 集成检查清单第 3 项；**§六 新增第 13 条**：记录重构后对外形态（`StateDto`、服务分层、可注入接口、类名 `OrderExecutor`、`SolverOperation` 属性名对齐 JSON） |
| `重构报告/00_审计报告.md` | 审计正文（逐板块问题条目、证据行号、原则矩阵、23 模式落点表、分阶段计划、风险与回滚、待裁决项）；后续两处补充（移植回归发现、Undo 溯源结论） |
| `重构报告/01_阶段1_封装与安全.md` | 批 1/2/3 的改动清单、逐项等价性论证（含 6 处关键推导）、受影响调用点清单（grep 核实）、执行记录 |
| `重构报告/02_阶段2_职责拆分与依赖注入.md` | 目标结构、逐类拆分方案与等价性约束、6 批实施记录、契约快照说明、五 God class 拆分前后对照 |
| `重构报告/03_阶段3_设计模式与契约强类型.md` | 23 项模式落点总表（引入/降级/不引入及理由）、Strategy 与 CoR 的等价性论证、DTO 契约核对表、剩余项 |
| `重构报告/板块报告/` | 索引 `README.md` + 10 份板块报告（A+B、C、D、E、F、G、H、I、J、K、L，本文件为其中之一） |
| `重构报告/verify.bat` | 一键验证（build → 测试 → 策略实验 → 契约快照 diff），并备份将被策略实验覆盖的历史记录文件 |

## 3. 已知的文档问题（记录，部分已在报告中标注）

| 编号 | 文件 | 问题 | 处置 |
|---|---|---|---|
| L-a | `问题/移植报告.md` | **过时**：描述早期 SignalR 版（端口 3456、含 Hubs/RuleEngine/WorkshopService），与最终 WebSocket 版不符 | 已在本报告记录；建议在文件顶部加醒目「历史文档，勿据此实现」标注 |
| L-b | `_test_diag.csx` | 临时诊断脚本，用旧的字典式 `GetState()` 访问，阶段 3 DTO 化后已失效 | 建议删除（我的工具无法删文件） |
| L-c | `回答.txt` | 空文件 | 建议删除或填入说明 |
| L-d | `src/JarSystem.Core/Solver/StateSpaceSolver.js.ref`、`src/JarSystem.Core/Executor/IndependentExecutor.js.ref` | 原始 Node 版参照文件残留在源码目录 | 建议移入 `重构报告/_refs/` 或删除（不影响编译） |
| L-e | `Godot移植设计对话记录.md` | 第 176 行仍写「`GetState()` 返回纯 `Dictionary<string, object?>`」 | 建议同步为 `StateDto`（我未改动该文件，避免与你的会话记录冲突） |
| L-f | `UML/02` 的渲染 | 本机无 Java/PlantUML，新增块**未经渲染验证**（结构性检查：`package {}` 配对、`@enduml` 唯一、关系语法为标准写法） | 建议在 plantuml.com 或 VS Code 插件复核一次 |
| L-g | `规则.txt` / `问题/系统设计与构造.md` | 本次重构**未改规则**，故这两份权威行为文档无需改动（这也是重构的验收条件之一） | 保持不动 |

## 4. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| UML 结构 | 8 个 `.puml` 的 `@startuml/@enduml` 配对、花括号配对、`package` 嵌套正确 | 待你在 plantuml.com 渲染复核 |
| 行为文档一致性 | `规则.txt` 的 7 节规则与实现一致（本次未改规则） | 由测试套件（2000+3000+20）间接保证 |
| 报告完备性 | 12 个板块均有报告（本文件补齐 L；当前 11 份 + 索引） | 见 `板块报告/README.md` |

## 5. 遗留项

| 项 | 计划 |
|---|---|
| `UML/02` 其余细节（类图未逐一画出每个 DTO 属性、`StateSerializer` 与 DTO 的关系已示意但未展开） | 若你需要更细的类图，可再补一版；当前版本已覆盖类与关系的主干 |
| L-a/L-b/L-c/L-d 的清理 | 需你执行（删除/移动文件）；内容已在报告中说明 |
| L-e 同步 | 需你确认后我可改动该文件（避免覆盖你的对话记录） |
| 重构总结报告 | 待阶段 3 评估项（Observer / 命令强类型结果 / Decorator·Proxy 评估）收口后，出一份总报告（已完成内容、遗留项、模式取舍总表、验证结论） |

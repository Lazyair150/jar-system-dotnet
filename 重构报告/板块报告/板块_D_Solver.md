# 板块 D — Solver（修复报告）

> 范围：`Solver/StateSpaceSolver.cs`（含 `SolverState`、`WorkshopInfo`、`SolverStats`、`SolverResult`）、`Abstractions/ISolver.cs`
> 阶段：1（批 2/3）+ 2（批 2A/2B）
> 验证状态：**待验证**（`重构报告\verify.bat`）

---

## 1. 板块职责

状态空间搜索求解器：在「工作台规则 + 罐子快照 + 液体账目」上做 DFS 回溯，产出满足需求序列的操作路径（`MATCH`/`POUR_IN`/`POUR_OUT`/`RESHAPE`），并支持「选择性转移预算」约束。**只读推演**：从不修改真实工作台状态。

## 2. 重构前的问题（引用审计）

| 编号 | 问题 | 证据 |
|---|---|---|
| D1 | `SolverState` 12 个缩写方法名（`Gl/Hl/Cl/Pl/Mj/Rj/Pij/Ej/Ie/Ib/Ct/Gcl`）+ 可变公共状态 + 直接改字典 | `StateSpaceSolver.cs:9-47` |
| D2 | `Stats`/`BestPartialPath` 为可变实例状态 ⇒ 求解器非可重入；`FeasibilityChecker.Solver` 复用同一实例 | `:85-86` |
| D3 | `JarState` 手工投影 6 处（跨 3 个文件） | `:20,118-119`；`FeasibilityChecker.cs:47,61,74,76,125-126` |
| D4 | `switch (op.Type)` 字符串分派 | `:192-207` |
| D5 | 操作生成与搜索耦合在同一类 | `:141-181` |
| D6 | `Block()` 生成中文文案（表现层关注点） | `:212-226` |
| D7 | `FindJarIndex(Func<>)` 分配闭包 + O(n) | `:30` |
| D8 | `SolverStats` 四个计数器只自增不读取；`GetPartialPath()` 无调用者 | `:61-66,101`；审计 §2.8 |
| — | 求解器无抽象（0 个 interface，无法注入替身） | 审计 §2.2 |

## 3. 改动清单

| 文件 | 改动 | 阶段 |
|---|---|---|
| `Abstractions/ISolver.cs`（新） | 抽取 `Solve(requirements, workshopInfo, jars, liquids, transferBudget)` 契约 | 2A |
| `StateSpaceSolver.cs` | `class StateSpaceSolver : ISolver`（**编译期固定签名与接口一致**） | 2A |
| `StateSpaceSolver.cs` | `SolverState` 方法改为意图命名：`Gl→LiquidCount`、`Hl→HasLiquid`、`Cl→ConsumeLiquid`、`Pl→AddLiquid`、`Mj→MarkJar`、`Rj→SetShape`、`Pij→SetLiquid`、`Ej→TakeLiquid`、`Ie→IsEmpty`、`Ib→IsBanned`、`Ct→CanTransform`、`Gcl→AvailableCostLiquids` | 1（批 3） |
| `StateSpaceSolver.cs` | 删除无调用者的 `GetPartialPath()`；`JarState` 投影 3 处改 `JarState.Copy` | 1/2B |
| `Order.cs` | `SolverOperation` 缩写属性对齐 JSON 字段（`Ji→JarIndex`、`Ts→TargetShape`、`Tl→TargetLiquid`、`Cst→CostLiquid`、`Cl→Consumed`、`Desc→Description`）；`JarState.From/Copy` 工厂（`Copy` 默认保留 `MasterOrigin`） | 1/2B |
| `FeasibilityChecker` / `PartitionSearch` | 求解器改为构造注入（`ISolver`），划分搜索复用同一实例而非每次 `new` | 2B |

## 4. 等价性论证（要点）

1. **改名不改语义**：`SolverState` 的 12 个方法体逐字保留（仅方法名与参数名变化）；`ConsumeLiquid/HasLiquid/LiquidCount/AddLiquid` 的账目逻辑、`TakeLiquid` 的"取走并清空"语义完全一致。
2. **`JarState` 投影收敛**：`From(Jar)` 用 `IsMarked = false`（与原 5 处一致）；`Copy(JarState)` 保留 `IsMarked` 与 `MasterOrigin`（用 `bool?` 参数表达"显式覆盖"，选择性转移池显式传 `true`/`false`）——**这一点若写错会静默改变转移预算语义**，已在报告中单列强调。
3. **求解器实例复用**：`Solve` 入口处重置 `Stats`/`BestPartialPath`；`SolverState` 构造时对入参 `jars`/`liquids` 做深拷贝 ⇒ 循环复用同一实例不会污染快照；`SolverResult.Stats` 无读取方，故复用无可观测差异。
4. **接口签名一致**：`ISolver.Solve` 与实现逐参数（含 `transferBudget = int.MaxValue` 默认值）一致，由 `: ISolver` 在编译期强制。
5. **未改动**：搜索算法（`Bt` 的回溯结构、POUR_OUT 的索引推进条件、`FullOps/EmptyOps` 的操作生成顺序、`Apply` 的守卫顺序）与 `Block()` 文案全部保持原样。

## 5. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| `dotnet build` | 0 error / 0 warning（含 `: ISolver` 的接口实现校验） | 待回贴 |
| 全测试 | `0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic` | 待回贴 |
| 策略实验耗时 | 与基线同量级（约 7.5s / 8532 条运行流）；求解器未改算法 | 待回贴 |
| 路径长度/可行性 | `feasibilityResult.solverPath` 步骤数与内容不变（契约快照可对比） | 待回贴 |

## 6. 遗留项

| 项 | 说明 / 计划 |
|---|---|
| `SolverState` 仍是可变公共状态（直接改 `Liquids`/`Jars` 元素） | 属求解器内部工作副本（快照语义），非领域对象；保持现状并在此说明，避免与「领域封装」混淆 |
| `switch (op.Type)` 字符串分派（D4） | 求解器操作类型（`MATCH/POUR_IN/POUR_OUT/RESHAPE`）尚未常量化——它们同时出现在执行器与路径摘要器中，属跨模块协议；计划与 `SolverStepDto` 一起收口 |
| `SolverStats` 只写不读（D8 残余） | 保留为诊断统计（可暴露给状态快照）；已删除的只有 `GetPartialPath()` |
| 求解器无节点预算 | 保持现状（订单需求上限 10 已约束 2^N 规模），如需再补迭代上限 |
| `Block()` 中文文案在求解器内（D6） | 保留（属对外 `blockage` 文案契约），未迁移 |

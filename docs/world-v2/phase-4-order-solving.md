# World v2 阶段四 4.2：订单规格求解适配

## 目标

将阶段三的通用 `OrderDemand` / `IOrderSpecification` 接入阶段四的 `WorldSnapshot`，形成从订单规格到领域求解算法的明确分派路径。

本阶段只做求解分析，不预留或消耗真实资源，不启动执行过程。

## 新增职责

### `WorldOrderSolverRegistry`

- 以 `IOrderSpecification` 的**精确运行时类型**为键注册求解器。
- 不做基类/接口模糊匹配，避免注册顺序改变分派结果。
- 没有注册适配器时返回 `UnsupportedSpecification`，不能把“系统不认识该规格”伪装成“资源不可行”。
- 不解释订单规格内容，不依赖罐子玩法字段。

### `JarOrderSpecificationSolver`

- 只处理 `JarOrderSpecification`。
- A 类：在主台快照上调用既有 `ISolver`。
- B 类：复用既有 `PartitionSearch`，在主台与从台快照上进行需求子集划分。
- 需求执行顺序继续使用唯一实现 `RequirementOrdering.FullFirst`。
- 求解器输入由 `WorkshopSolveSnapshot` 生成；不读取实时 `Workshop`、`Jar` 或 `ModifierStack`。
- 通过快照专用投影器应用可精确表达的静态规则：
  - 整种液体抑制 → 加入求解能力禁令；
  - 整个形状抑制 → 从候选罐池排除该形状；
  - Grant → 加入附加标记。
- opaque 或无法精确表达的规则返回 `Undetermined`，不能忽略后误报可行或不可行。
- B 类要求主从岗位都存在，且从台快照必须为空；不满足时返回 `InvalidWorld`。

### `WorldOrderSolveResult`

每个结论固定绑定：
- `DemandId`
- `WorldVersion`
- `Status`
- `CheckMethod`
- `Reason`
- 直接匹配数与操作数

状态分为：
- `Feasible`
- `Infeasible`
- `Undetermined`
- `UnsupportedSpecification`
- `InvalidWorld`
- `NotReady`（订单 CD 尚未结束）

`Feasible` 仅表示该订单规格在该快照上存在可行解。它不代表资源已经预留，也不代表该结果可以不经执行前校验就直接执行。此阶段结果尚不包含执行计划载荷；执行计划的持有、版本确认和资源预留属于后续阶段。

## 预算语义

一次订单求解只启动一次预算：
- A 类：单次主台求解使用该预算。
- B 类：`PartitionSearch` 中主台/从台的全部划分共享同一预算。
- 预算耗尽或快照规则不可判定时统一返回 `Undetermined`，不能将其降格为 `Infeasible`。

## 尚未实现

- 需求板 Dirty 标记、CD 到期触发和统一调度。
- 主动/被动区域的执行启动策略。
- 主动与被动区域之间的执行优先级。
- 求解结果缓存、世界版本变化后的失效管理。
- 执行前二次验证、资源预留与执行期间独占。
- 持续订单成功后的重新求解策略。

这些事项不能由本阶段求解器自行决定。

## 测试范围

新增测试覆盖：
- 罐子规格注册和 A 类主台求解；
- 未注册规格与不可行状态的区分；
- opaque 规则的未判定语义；
- 可表达的整形状抑制规则投影；
- B 类主从划分；
- B 类缺少从台的世界错误；
- 搜索预算耗尽时返回未判定。

本阶段代码已推送到独立 Draft PR，必须以本地完整 `重构报告\\verify.bat` 的结果作为最终验证依据。

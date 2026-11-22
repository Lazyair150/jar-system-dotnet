# World V2 漏斗求解器基础与统一规则模型

> 状态：架构目标与迁移决策（2026-11-22）。本文定义后续实现约束，不表示目标架构已经落地。

## 1. 核心决策

Jar 世界的求解基础不是工作台类型，也不是订单类型，而是**规则（Rule）**。

- 规则定义可执行操作、适用条件、状态转换、资源输入/输出和失败语义。
- 世界定义本次求解时适用的规则集合、修饰结果、词表与全局约束。
- 工作台只提供自身属性：能力参数、资源容器、容量与局部状态。工作台不再拥有一套与求解器平行的规则实现。
- 订单只描述最终目标与业务语义，不预先携带主从台分配、操作路径、Jar 选择或液体消耗估算。
- 漏斗求解器读取订单目标、世界规则和资源事实，推导步骤、候选资源与可行解集。
- 执行器执行已证明合法的计划；执行时仍在动态前置条件边界重新验证。

这不是把所有代码塞进一个万能规则类。规则定义、规则上下文、工作台属性、世界资源、搜索算法、执行计划必须分层。

## 2. 统一术语

### 2.1 Rule Definition（规则定义）

一条规则是稳定、可识别、可版本化的领域算子。至少声明：

1. RuleId：稳定标识，不依赖 C# 类型名或显示名称。
2. RuleVersion：规则语义版本；语义变化必须改变版本。
3. Applicability：适用操作、Jar facts、工作台能力和世界上下文。
4. Preconditions：执行前必须满足的条件。
5. Transition：对 Jar 与游离液体账目的确定性状态转换。
6. ResourceEffect：按来源分别声明消耗、产出、转移、丢弃或锁定。
7. FailureSemantics：明确拒绝、条件未知、预算耗尽等状态。
8. Explain：稳定原因码及参数。

规则核心不是返回 bool，而是回答：给定状态和操作，能否应用；若能，得到什么新状态和资源变化；若不能，为什么；若无法判断，缺少什么上下文。

### 2.2 Rule Set / Rule Profile（规则集合）

规则集合是某个世界在某一求解时点采用的规则定义集合与修饰结果。它是不可变纯值输入，并具有规范化指纹。

规则集合不等于工作台能力。工作台能力是规则求值的输入之一；规则集合决定这些能力如何解释。

### 2.3 World State（世界状态）

世界状态包含求解所需事实，而不是规则本身：

- 工作台实例及其能力、Jar 集合、游离液体库存和容量；
- 自机液库、生产库存等独立资源账户；
- Master / Slave / Spare 岗位映射；
- 规则时间窗口所需的逻辑时间事实；
- 词表、资源身份、订单资源范围等约束。

资源账户必须保持来源和所有权独立。Jar 内液体、工作台游离液体、自机液库、订单 staging、主台预置液不能压成一个 liquidAmount。

### 2.4 Rule Context（规则上下文）

规则上下文是一次规则评估所需的只读事实集合，包含规则集合、目标工作台属性、世界时间、资源视图以及必要的订单阶段信息。它不是可变 Workshop 的别名，也不能持有任意闭包而声称可序列化、可缓存。

### 2.5 Funnel Solve（漏斗求解）

漏斗是约束逐层收敛的求解过程，不是单一算法或固定过滤器。每层只使用统一规则契约，逐步缩小候选操作、Jar、资源来源和执行路径；每次收敛都保留证据。最终结果是零个、一个或多个完整解，而不是只返回可行/不可行。

## 3. 规则与工作台的职责边界

### 3.1 工作台只提供属性与资源

工作台保留：
- WorkshopCapability：基础标记、附加标记、Ban 等配置值；
- Jar 容器与游离液体容器；
- Jar 数量、液体容量与订单 staging 容量；
- 对资源进行原子接收、移出、扣除和归还的状态操作；
- 自身身份与名称。

工作台不应再分别实现供求解器复制的 PourIn、PourOut、Reshape 规则语义。它可以提供资源操作门面，但门面必须调用统一规则评估与状态转换契约。

岗位 Master / Slave 是世界编排关系，不是 Workshop 能力。工作台类型扩展不应通过继承整个 Workshop 并复制规则完成。

### 3.2 四种基础 Jar 算子

| 算子 | Jar 转换 | 游离液体效果 |
|---|---|---|
| PourIn | 空罐 → 指定液体满罐 | 消耗工作台可用游离液体 1 |
| PourOut | 满罐 → 空罐 | 尝试向当前工作台 staging 接收 1；拒收/超限按既定丢弃规则处理 |
| ReshapeEmpty | 空罐 → 目标形状空罐 | 消耗指定成本液体 1 |
| ReshapeFull | 满罐 → 目标形状空罐 | 消耗 Jar 内部液体；不生成游离液体 |

MATCH 是订单目标与现有 Jar 的匹配/占用规则，不是四种资源转换算子。Mark 是阶段资源使用约束；跨台 Transfer 的清标记是交接语义，不能误建模成普通改形。

规则转换必须显式区分 Jar 内部液体、工作台游离液体、订单 staging、工作台容量、资源来源与所有权。

禁液的现有语义必须保持：禁令禁止取用，不自动禁止接收/存储；PourOut 是否能产出、产出是否被 staging 接收，是两个独立判定。

## 4. 规则修饰器是规则集合的一部分

现有 Suppress / Require / Grant / Retime / Intensity 是重要基础，但当前快照只对部分类型做静态投影；RequireModifier 保存任意委托，导致规则身份、确定性、缓存与恢复边界不完整。

目标：
- Suppress、Grant 等转成有稳定 RuleId/参数/优先级/时间窗口的声明式修饰。
- Require 不得只存任意闭包。可缓存规则使用可序列化、可版本化的条件表达式或稳定谓词标识及参数。
- 外部任意代码谓词可以作为运行时扩展，但必须声明 opaque / non-cacheable；不能静默忽略，也不能把未知当作拒绝。
- 同优先级冲突在规则集合装配时显式诊断。
- 时间窗口和作用域变化产生新的有效规则集合指纹。
- Retime / Intensity 若只影响执行节奏或生产，不应无意义地使纯 Jar 结构求解缓存失效；按依赖维度划分指纹。

## 5. 漏斗求解器的输入与产物

### 5.1 输入

一次求解输入是不可变 SolveProblem，至少包含：
- 订单目标（规范化需求多重集，而非主从台分配）；
- 规则 Profile / Rule Set；
- 工作台属性快照与岗位关系；
- Jar 纯值状态；
- 按来源分离的资源账户；
- 订单类型所需的世界约束（A/B 阶段关系等）；
- 求解完整性策略与预算。

不得从实时可变 Workshop 读取数据。所有输入必须在同一世界版本边界下捕获。

### 5.2 输出

求解结果不只是单条贪心路径，而是：
- SolveStatus：Complete / Infeasible / Undetermined；
- SolutionSet：完整计划集合或惰性枚举器；
- 每个解的步骤 DAG / 有序操作计划；
- 每个解的资源需求证明（按来源和阶段）；
- 每个解的 Jar 身份分配、交付集合和跨台交接集合；
- 规则版本、世界输入指纹、订单规格指纹；
- 完整性证明：搜索空间是否穷尽、剪枝依据、预算是否耗尽；
- 诊断轨迹与可解释失败原因。

找到至少一个完整解才可报告 Feasible；只有搜索空间完整穷尽且所有规则均可判定才可报告 Infeasible；预算耗尽、opaque 规则或不完整搜索只能返回 Undetermined。

## 6. 订单求解与工作台切换共用规则，不共用搜索方向

订单求解：从最终 Jar 需求反推可能的来源 Jar、规则算子、操作序列和资源需求。

工作台切换：从当前 Jar/液体资源出发，在目标工作台规则与容量下正向推导可迁移、可重组的结果；目标是尽力利用资源，不是订单交付。

两者共享 Rule Definition、Rule Context、ResourceEffect、状态转换、失败语义与证明结构，但使用不同目标函数和搜索策略。不要为了统一而把它们硬塞进同一业务算法。

## 7. 解集与执行态：静态求解和动态资源分离

在规则集合、订单规格、工作台属性和结构性世界条件不变时，订单的**结构解集**不变。资源余额变化不应自动触发完整求解。

必须拆分：
1. SymbolicSolution：由订单目标、规则 Profile、工作台能力/容量与世界阶段约束推导出的抽象操作程序。它描述需要什么 Jar 输入事实、可选算子序列、Jar 角色、资源种类/数量公式和跨台依赖，不绑定当前 Jar ID，也不假定当前库存已经满足。
2. ResourceBinding / Readiness：将 SymbolicSolution 的输入角色绑定到当前真实 Jar 身份与资源账户，检查形状、液体、标记、位置、所有权、容量和余额是否满足；此层可以随资源变化重新评估，但不重跑规则空间搜索。
3. Admission：原子检查并锁定资源，创建执行 Lease。
4. Execution：按计划执行，动态规则/前置条件在操作边界再次验证。
5. Settlement：按资源来源进行返还、丢弃和结算。

因此，战前构筑可以求出结构解集并预测成本；战斗中生产和消耗只更新 Readiness；工作台切换改变能力、Jar 分布、岗位和资源来源，导致受影响订单重新求解；运行时新增/移除规则导致规则 Profile 指纹变化，重新求解受影响订单。

“固定几个解”必须有边界：解集很大时支持惰性枚举、Top-K 或按目标函数筛选；不得无界缓存全部解。缓存可丢弃、可重建，不是权威世界状态。

## 8. 版本与缓存失效

至少区分：
- RuleSetFingerprint：规则定义版本、有效修饰器与规则参数。
- WorkshopStructureFingerprint：工作台能力、容量、岗位/目标工作台结构等影响路径的属性。
- OrderSpecificationFingerprint：最终目标多重集、订单阶段语义与稳定订单定义身份。
- WorkshopProfileFingerprint：参与工作台的能力参数、容量、岗位结构和会改变抽象操作程序的属性。
- ResourceBindingFingerprint：当前真实 Jar 身份/形状/液体/标记/位置、各资源账户余额和资源预留状态。
- ExecutionPolicyFingerprint：预算、完整性模式、目标函数等影响求解输出策略的设置。

符号解缓存键由规则集合、订单规格、工作台 Profile 与结构阶段约束组成；真实 Jar 与液体余额由 ResourceBinding 单独评估。真实 Jar 的形状/液体/标记会决定能否绑定某个输入角色，但不必因此重算抽象规则程序。若某条规则确实依赖某项资源事实并改变算子本身，则该事实必须显式声明为规则依赖。具体依赖由规则声明，不允许依赖“整个 WorldSnapshot 每 tick 变化”的粗粒度键。

缓存命中只代表同一输入可复用，不代表资源已预留。Admission 必须基于当前版本原子重验并建立资源租约。解集本身不应进入权威世界快照；快照保存重建所需的权威事实、规则 Profile 身份/版本和必要扩展状态。

## 9. 当前代码审计：可复用基础与结构偏差

可复用基础：
- WorkshopCapability 是不可变能力值对象。
- LiquidRules、ReshapeRules 已把部分液体与改形语义抽成纯值规则。
- WorkshopRulebook 已表达四种操作的判定。
- ModifierStack 有时间窗口、作用域回收、优先级与冲突检查。
- WorldSnapshot 已脱离实时 Workshop，并通过内容指纹版本化。
- PartitionSearch 已对 B 类需求做子集划分搜索，并要求主台真实终态后再求从台路径。
- WorldOrderSolvePlan、WorldOrderResourceManifest、Admission/Lease/Session 已把求解、资源准入和执行拆开。

结构性偏差：
1. 同一规则仍由 Workshop、WorkshopRulebook、StateSpaceSolver/SolverState、SnapshotProjectedSolver 多处解释。
2. WorkshopRulebook 文档明确说明热路径不强制调用它，因此它是对拍表，不是规则权威来源。
3. WorldSnapshot 将修饰器投影为另一套 WorldRuleModifierSnapshot；部分规则可静态投影，部分 opaque，规则身份与规则执行未统一。
4. PartitionSearch 是 B 类分配搜索，不是通用漏斗；搜索策略不应承担规则定义。
5. 当前 B 类算法枚举主从需求划分，逐个划分先解主台，再以主台真实终态解从台。它是精确划分搜索，不是从台优先贪心；但只返回首个可行解，尚未提供完整订单解集。
6. 主台切换过程拥有自己的迁移逻辑，尚未表达为规则算子与资源转换计划。
7. WorldSnapshot.Version 是求解输入内容指纹，但尚未显式区分规则版本、结构可行性和资源就绪状态。
8. World V2 checkpoint 保存权威运行态是合理的；未来不得把可重建解集、缓存、诊断轨迹作为权威状态重复存入快照。

## 10. 迁移策略

不得一次性删除现有 Snapshot、Manifest、Lease、Checkpoint 或 Host 边界。它们解决资源所有权、执行恢复、原子替换等真实问题；新模型只消除其中重复表达规则或重复推导可行性的部分。

### 阶段 A：规则契约统一（第一优先级）
- 定义稳定的 RuleDefinition / RuleSet / RuleContext / RuleEvaluation / RuleTransition / ResourceEffect。
- 将四种基础算子与 MATCH/Mark/Transfer 的边界写成规则定义。
- 先让 Workshop 执行门面调用统一规则入口；再让 Solver 使用相同规则评估/转换。
- 以旧实现作为差分 oracle，语义一致后再移除重复判定。
- 将 Suppress/Grant 声明式纳入规则集合；Require 定义声明式条件契约与 opaque 边界。
- 规则语义不能被纯值表达时，明确返回 Undetermined / NonCacheable。

### 阶段 B：漏斗搜索内核
- PartitionSearch 保留为 B 类分配策略，不让它定义规则。
- 建立统一状态节点、候选算子枚举、规则评估、状态转换、资源来源追踪和证明节点。
- 订单从终态需求向前驱状态反推；同一算子也可用于工作台切换的正向资源转换。
- 完整性边界、剪枝单调性、重复需求规范化、预算耗尽和多解枚举必须显式。
- 第一版保持旧求解器输出兼容，再逐步替换搜索核心。

### 阶段 C：结构解集与 Readiness
- 将结构解集缓存从资源余额状态中拆开。
- 资源产出/消耗只更新 Readiness。
- 工作台能力、规则集合、订单规格或结构性 Jar 分布变化时才重新求解。
- Admission 对当前资源做原子重验与 Lease 创建，不信任过期 readiness。

### 阶段 D：工作台切换接入
- 将切换建模为目标工作台约束下的正向资源转换问题。
- 输出 MigrationPlan / ResourceManifest / Trace。
- 继续遵守现有主台切换提交边界、预置液回滚、资源所有权与失败隔离。
- 从台替换仍是岗位替换，不伪装成主台资源迁移。

### 阶段 E：清理与持久化简化
- 以依赖图逐项审查 Snapshot、Wire DTO、Checkpoint、Fingerprint。
- 保留权威资源与执行恢复事实。
- 删除重复规则投影、可重建缓存的持久化、重复派生状态；每次删除必须有恢复与重放证据。
- 将 Solver Registry / RuleSet Profile 稳定身份和版本纳入存档兼容性边界。

## 11. 验收不变量

1. Workshop 执行门面、Rulebook 兼容入口与 Solver 对同一事实和操作给出一致规则结果。
2. 同一规则 Profile + 结构输入 + 订单规格，得到相同规范化解集和指纹。
3. 只改变自机液库余额，不改变结构解集；只更新 Readiness。
4. 改变工作台能力、Jar 结构、岗位或规则 Profile，受影响订单重新求解。
5. opaque 规则绝不被忽略；不可判定不等于不可行。
6. 搜索预算耗尽不等于不可行；只有完整穷尽才可报告 Infeasible。
7. A 类不创建 Slave 资源范围；B 类 Slave 液体唯一来源是 Master 单向交接。
8. Jar 内液体、游离液体、Ship inventory、staging 与 prefill 始终分账。
9. 诊断、缓存启用与否不改变解集、资源状态或执行结果。
10. 快照只保存恢复权威世界所需事实，不保存可重建的解集缓存。
11. 工作台切换使用同一规则定义，但不改变订单求解的反向目标语义。
12. 新工作台能力通过声明属性接入，不复制操作规则实现。

## 12. 明确不做

- 不立即删除现有 Snapshot / Checkpoint / Lease / Manifest。
- 不把工作台、订单、规则合并为一个巨大聚合。
- 不把 A/B 订单业务策略误做成通用规则算子。
- 不把订单结构解与资源是否够用混成一个布尔值。
- 不以找到一个解冒充完整解集，也不以当前没有资源宣告结构无解。
- 不允许任意运行时委托进入可持久化、可指纹化的规则集合。
- 不声称 NP-hard 搜索在理论上消失；目标是减少重复求解、共享规则语义并正确利用结构不变性。


## 13. 关键实现现状：当前版本指纹仍混合结构与库存

当前 WorldSnapshot.ComputeVersion 写入 Master、Slave、ShipLiquidInventory 的完整余额和 MasterPrefillLiquidBalances；WorkshopSolveSnapshot 也包含 Jar 列表、游离液体余额与修饰器投影。因此生产入库、预置液余额或其他资源事实变化会改变当前 SolveSnapshot.Version。当前订单求解器又直接以该快照构造 StateSpaceSolver 输入，实际结构求解仍受当前库存约束。

这正是下一轮要拆分的地方，不应把当前代码描述成已经具备“资源变化只更新 Readiness”的能力。

目标是引入独立的 SymbolicSolveProfile / ResourceBindingSnapshot：
- SymbolicSolveProfile 不包含当前库存余额和真实 Jar ID；它包含规则、订单规格、工作台能力/容量、岗位阶段约束，以及由规则声明的结构依赖。
- ResourceBindingSnapshot 保存真实 Jar、液体余额、Ship inventory、prefill、staging、reservation 与 ownership。
- SymbolicSolution 表达可满足的 Jar 输入角色和资源需求，不直接声明某个当前 Jar ID。
- Binding 通过匹配真实 Jar 与资源账户验证可执行条件；准入仍以当前世界独占边界内的事实重验并建立 Lease。
- 对需要特定资源状态才能改变规则语义的扩展，规则必须声明依赖；该类状态进入对应 Profile 指纹，不能偷偷从库存中读取。

此拆分是架构目标，需通过旧 Solver 差分测试与资源变动不重算测试逐步迁移。


## 14. 首轮代码迁移进度（2026-11-22）

已进入代码的第一小段：
- StateSpaceSolver 的四种候选操作现在通过 WorkshopRulebook 评估；Apply 不再自行重复判断这些操作的领域前置条件。
- WorkshopRulebook 新增纯值 WorkshopOperationTransition，包含稳定 RuleId/RuleVersion、Jar After facts、游离液体消耗/产出和 Jar 内液体自耗描述。
- StateSpaceSolver.Apply 使用该转换描述更新搜索副本；RuleContext 在 SolverState 内复用，避免每个搜索节点重复创建规则上下文。
- 新增测试验证四种操作的转换结果、资源效果和输入纯度。
- 保留 MATCH 为需求匹配/占用规则，不混入四种 Workshop 转换算子。
- GitHub Actions verify #1650 已对规则判定接入提交通过；后续包含纯转换及测试的 verify #1657 / #1659 / #1661 仍需以各自最终结果为准。

尚未完成：
- Workshop 真实执行门面尚未消费同一个 WorkshopOperationTransition；当前它仍负责自身资源账户事务和 Jar 变更。
- 规则拒绝目前仍主要以 bool 表达，稳定的 Decision / ReasonCode / Undetermined 契约尚未落地。
- RuleId/RuleVersion 目前是四种转换结果上的第一版稳定身份，不等于已经有完整 Rule Registry / Profile。
- 搜索仍以当前具体 Jar/液体快照求一条首解；尚未实现抽象 SymbolicSolution、多解集、ResourceBinding 与 Readiness 分离。


## 15. 待执行解、准入绑定与活动执行计划不能混为一谈

这里必须明确三种不同对象，否则“解集不进入快照”会被误解为“执行计划也不需要保存”。

1. **待执行的符号解集**：规则/工作台 Profile/订单规格推导出来的候选程序。它是可重建的求解缓存，不是权威世界状态；普通世界检查点不必保存整套候选解。
2. **资源绑定与准入结果**：从符号解中选定一个候选，将输入 Jar 角色绑定到真实 Jar ID，并按当前资源账户精确计算、校验和预留资源。绑定必须在 Host 独占操作边界内完成；Readiness 本身不拥有资源。
3. **活动执行计划**：准入成功后，选定的 BoundExecutionPlan、Manifest、Lease、操作游标、阶段、已提交的 Transfer/Settlement 收据，已经成为正在发生的业务事实。为了跨 Host / JSON 检查点恢复而不重复扣液、泵送或执行步骤，这些数据仍必须进入活动执行检查点。

因此，新架构可能减少的是“把待执行的可重建解缓存当作权威状态保存”的重复设计，不是删除活动会话恢复所需的 Plan / Manifest / Lease / Cursor。当前根级检查点中活动 Session 的 Plan 与进度属于第三类，原则上应保留；SolveBook 的待执行结果缓存属于第一类，原则上可以在恢复后重建。


## 16. 运行时规则的真实接入缺口

当前 World V2 Context 的检查点注释明确说明：Context 尚未持有动态 ModifierStack；执行资源检查点也不会捕获动态 ModifierStack。另一方面，WorkshopSolveSnapshot 能捕获工作台所见的修饰器投影，但这不等于 World V2 已经拥有可持久化的世界级动态规则系统。

因此，未来的运行时规则不能只靠在某个 Workshop 上临时 Push 一个委托来实现。规则激活必须经过 World V2 的权威入口：
- World RuleSet/Profile 管理已注册规则定义与当前生效的规则修饰；
- 每个修饰器有稳定身份、参数、目标作用域、优先级、生效区间和版本；
- Context/Checkpoint 捕获可恢复的声明式活动规则状态；
- Restore 校验规则注册表版本与所需定义是否存在；
- 任意 opaque 扩展只能作为明确不可缓存/不可跨 Host 恢复的运行时能力，不能伪装成普通持久化规则。

在该入口完成前，不能声称 World V2 已支持运行时添加规则并在恢复后保持相同解集。

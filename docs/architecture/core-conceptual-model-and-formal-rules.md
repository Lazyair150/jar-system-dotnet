# Jar Core 概念模型与形式化规则规范

> 状态：目标架构规范（2026-11-25）。适用于 Jar Core、World V2、WorldV2Bridge 与所有消费 Core 规则能力的宿主。本文区分当前实现与目标；目标不得误读为已经实现。

## 1. 系统定义

Jar Core 是一个**世界隔离、规则驱动、资源守恒、确定性求解与执行系统**。

World 是语义闭包：拥有自己的身份目录、规则配置、规则板装配、资源账户、订单目标、运行时状态、版本指纹和执行历史。不同 World 可以使用不同资源名称、不同极性映射、不同元件组合和不同规则配置，并在同一进程中并存，互不污染。

Core 不等于界面、工作台流程或某个求解算法。Core 提供稳定领域概念、规则契约、资源事务与运行时边界；宿主负责内容、输入、显示、存档介质和引擎集成。

## 2. 规范性术语

“必须 / MUST”表示强制不变量；“不得 / MUST NOT”表示禁止；“应 / SHOULD”表示默认要求，偏离时必须记录理由；“可以 / MAY”表示可选实现。

## 3. 概念本体与所有权

### 3.1 World
World 是唯一语义与状态隔离边界，至少拥有：
- WorldIdentity 与配置版本；
- Vocabulary（形状、液体身份及身份到极性的显式映射）；
- BoardDefinition 与 BoardAssignment；
- EffectiveRuleProfile / Rule Registry 版本；
- Workshop Roster、岗位关系与世界级约束；
- 资源账户、生产、订单目标和执行策略；
- 逻辑时钟、命令日志、检查点和活动执行会话。

任何领域对象都必须能追溯到所属 World，或由不可变 World-owned value context 验证。禁止用进程级默认值推断自定义 World 语义。

### 3.2 Rule Board / Slot / Component
- BoardDefinition：声明槽位身份、兼容类别、数量约束、必需性、作用域与安装约束。
- SlotDefinition：稳定安装位置，不包含元件运行时状态。
- ComponentDefinition：不可变模板，带精确 ID/version 与贡献 schema/version。
- OwnedComponentInstance：玩家拥有的独立个体，带独立身份、成长状态和 progression revision。
- BoardAssignment：规范化的 slotId -> ownedInstanceId + exact definitionId/version 映射。
- RuleContribution：元件实例经精确 Definition 与成长状态解析后提交的声明式规则。
- EffectiveRuleProfile：校验、规范化、冲突处理后实际生效的不可变规则集合。

主台、从台、备用台、订单区、生产区、分解区等是槽位/岗位角色，不是元件 Definition 的固有类型。相同 Definition 的两个 Owned Instance 仍是两个独立个体。卸载、替换或移动不得重置实例成长与身份。

### 3.3 Resource / Jar / Liquid
Jar 是独立资源个体，最小状态为 JarId、ShapeId、LiquidId?、IsMarked；所属账户/工作台归属由权威资源账户维护，不在多个对象中复制真相。

Liquid identity 是不透明、稳定的资源键，不承载显示名语义。World 液体目录严格包含两个互斥身份，并显式映射到 Cathode 与 Anode，各出现一次。Jar 极性由液体身份和所属 World Vocabulary 派生，不是第二份可变状态：
- 无液体 -> Neutral；
- 液体映射为 Cathode -> Cathode；
- 液体映射为 Anode -> Anode。

禁止通过液体名称、列表位置、排序或全局静态 resolver 推断极性。不同 World 可以重命名身份，也可以反转映射。

### 3.4 Workshop 与资源账户
Workshop 是局部资源容器、局部能力/规则上下文和受控操作端口，不是规则本身，也不是 Solver。

资源账户必须显式区分，至少包括：
1. Jar 所有权账户（每只 Jar 同时且仅属于一个权威账户）；
2. Workshop free-liquid staging；
3. Order-scoped staging；
4. World/Ship inventory；
5. Producer output / Warehouse；
6. 明确声明的预置、租赁、保留或外部资源账户。

同一液体身份在不同账户中的数量不得合并后再判断可用性。每种操作必须声明读取、扣减、产出、转移、拒收和丢弃所涉及的账户。

Ban 是作用于指定 Workshop free-liquid staging 入口/取用/接收的规则，不是 Jar 属性、液体全局属性或 Jar 操作权限别名。Jar 内部液体自耗、Jar 物理交接、Jar 所有权迁移不得仅因 staging Ban 被拒绝。主台 Ban 液体不得通过先装入 Jar 再交给从台绕过来源约束；资源进入和跨台液体转移必须经过各自账户准入规则。Jar 移动与后续操作是两个独立事件；Mark 不因交接清除。

## 4. 四类输入必须分开

| 类别 | 定义 | 例子 | 指纹归属 |
|---|---|---|---|
| Rule | 决定合法性、转换和代价的声明 | 一罐一操作、Ban、交接方向 | Rule Profile / Registry |
| Configuration | 本局如何布置 | Slot、容量、产率、初始库存、岗位 | World Configuration / Assignment |
| State | 当前发生了什么 | Jar 状态、账户余额、订单状态、时钟 | Resource / Runtime State |
| Algorithm Policy | 如何寻找解 | DFS、候选排序、启发式、预算、剪枝 | Solver Configuration |

算法策略不得伪装成玩法规则。搜索预算耗尽是 Undetermined，不是 Infeasible。改变候选排序不得改变可行性语义；若算法不完备，必须明确报告完备性状态。

## 5. 规则的形式化契约

每条可执行规则必须具备稳定、可审计的定义：

RuleDefinition = (RuleId, RuleVersion, SchemaVersion, Category, Scope, Inputs, Dependencies, Preconditions, Transition, ResourceEffects, FailurePolicy, Determinism, SolverSupport)

- RuleId：稳定机器身份，不使用 CLR 类型名、显示文案或枚举序号作为身份。
- RuleVersion：语义变更递增；不得复用旧版本表达新语义。
- SchemaVersion：参数与序列化结构版本。
- Category：Transform / Allocation / Lifecycle / ResourceFlow / WorldPolicy / Temporal / Capability 等语义族。
- Scope：World、Board、Slot、Workshop、Order、Jar、Account 或明确局部作用域。
- Inputs / Dependencies：读取哪些事实；依赖必须可枚举，以支持正确缓存失效。
- Preconditions：纯判定，不产生副作用。
- Transition：允许时产生的规范化状态差异。
- ResourceEffects：Consume / Produce / Transfer / Discard 等显式账目效果。
- FailurePolicy：拒绝、未判定、回滚范围、已提交效果与丢弃语义。
- Determinism：相同规则版本、输入事实和参数必须得到相同结果。
- SolverSupport：可静态求解、仅运行时求值、opaque 或不支持；不得伪报支持。

统一评估结果：

RuleEvaluation = (Decision, ReasonCode, Diagnostic, Transition?, ResourceEffects, DependencyFootprint)

Decision 仅允许：
- Allowed：已证明满足前置条件，并提供可执行 Transition；
- Rejected：已证明违反规则，提供稳定 ReasonCode；
- Undetermined：缺少可解释依赖、遇到 opaque 规则或求解预算不足；不得当作拒绝或忽略。

纯评估不得直接修改 Jar、Workshop、账户、订单、时钟或日志。提交副作用只能发生在拥有相关状态的事务/执行边界。

## 6. 规则组合与冲突处理

规则贡献不是任意代码闭包，而是可序列化、可版本化、可验证的声明。

每条贡献必须包含来源 Definition/Instance、Slot、来源 revision、RuleId/version、参数、作用域、优先级、生效窗口和依赖。Profile 装配必须：
1. 验证来源实例、精确 Definition 版本与槽位兼容；
2. 验证贡献 schema 与参数；
3. 规范化身份、参数与排序；
4. 按规则类别声明的合并器/冲突策略组合；
5. 检查不可满足冲突与不支持贡献；
6. 生成不可变 Profile、来源追踪和 SHA-256 指纹。

禁止使用未声明的“最后写入者获胜”。冲突策略必须按规则类别明示，例如 RejectConflict、PriorityWins、IntersectConstraints、Accumulate 或 ExclusiveProvider。未注册的 Effect/RuleId/version 必须 fail-closed；不能静默忽略。

Core 可拥有受控的内建规则解释器，但不允许每个调用方自行解释相同 RuleContribution。扩展规则若不能被当前解释器安全执行，应返回 Unsupported/Undetermined，不允许反射式加载任意代码或隐式执行脚本。

## 7. Solver 的职责与结构

Solver 的输入是冻结的 SolveProblem，至少包含 Order Goal、EffectiveRuleProfile 与 Registry exact version、Workshop/Role topology、World constraints / transfer policy、Algorithm Policy，以及只读资源可见性事实和依赖声明。

Solver 输出分为两个层次：

### 7.1 Structural Solution
结构解不绑定物理 Jar ID、不持有当前账户余额、不预留资源。它描述目标、规则算子身份及版本、抽象输入/输出状态、依赖 DAG / 部分序、资源需求公式、岗位约束、完备性/证明状态和未判定原因。

结构解缓存只依赖规则、目标、岗位拓扑和显式声明的结构依赖。不得因无关资源余额变化就无条件重建，也不得在相关规则依赖变化时错误复用。

### 7.2 Resource Binding / Readiness
资源绑定将抽象资源角色绑定到真实 Jar ID 与权威账户，模拟操作顺序、检查容量/禁令/流向，计算 ResourceBinding、Readiness、账户级 Manifest 和预计 Consume / Produce / Transfer / Discard，以及 Resource Fingerprint。

Readiness 不拥有资源，不代表资源已预留。只有 Admission 在原子边界校验 Profile、World、Resource 版本并成功建立 Lease 后，才获得执行权。

### 7.3 算法不得定义领域语义
搜索算法可以替换 DFS、BFS、A*、约束求解或启发式实现，但必须消费相同 SolveProblem / RuleEvaluation 契约。算法不得硬编码液体名称、形状目录、槽位数量或资源上限；不得通过列表位置解释身份；不得复制 Ban、容量、极性、Mark 或资源转移规则；不得以 A/B 字符串直接分支并把流程写死在搜索循环；不得把某个固定首解顺序当成玩法要求；不得在候选生成、资源模拟和执行器中分别实现同一规则。

A/B 等订单差异必须建模为显式 OrderPolicy / WorldTransferPolicy 声明。现有语义必须保留：A 类不依赖从台；B 类允许主台准备后向从台单向交接，交接前从台为空，Jar 只能来自主台，主台已完成的 Jar 也必须按交付策略交接；从台接收 Jar 不等于允许其使用自有液体；从台 Ban 不得阻止 Jar 交接或 Jar 内液体操作。抽象成策略不意味着删除这些语义。

## 8. 执行、事务与结算

生命周期：

Solve -> Bind -> Check Readiness -> Admit -> Lease -> Execute -> Settle -> Complete

- Admit 必须原子校验 Profile/Registry/World/Resource Fingerprint。
- Lease 明确预留账户与资源，不得重复消费。
- Execution Session 固定 Plan、RuleSetFingerprint、Registry Version、Lease、Cursor、已提交步骤和收据。
- 每个操作边界重验动态前置条件；Pinned 或 Step-boundary Revalidated 必须由规则作用域显式声明。
- 规则变更不能让活动会话悄悄混用新旧语义。
- 失败不等于全部回滚。已提交的 Jar 化学操作不自动回滚；跨台交接按原子边界回滚；被 staging 拒收的产液记录为 Discard，不可重新计入库存。
- Settlement 是资源账目的唯一最终落点；每项效果可追踪来源账户、目标账户、数量和原因。

## 9. 确定性、指纹与持久化

指纹必须分域：
- WorldConfigurationFingerprint：世界词表、极性映射、布置和配置版本；
- BoardAssignmentFingerprint：槽位到 Owned Instance / Definition exact version；
- RuleProfileFingerprint：贡献、参数、优先级、作用域、冲突策略和规则版本；
- StructuralSolveFingerprint：目标 + 规则 + 结构依赖 + 算法完备性要求；
- ResourceFingerprint：Jar/账户余额/资源位置等动态事实；
- ExecutionFingerprint：冻结计划、Lease、执行游标和已提交收据。

指纹必须使用规范化排序、明确编码、稳定文化规则和版本前缀。不得把无关动态余额塞进 Rule Profile，也不得漏掉规则实际依赖的事实。

普通检查点可重建待执行的派生解缓存；活动执行的 Bound Plan、Manifest、Lease、Cursor、已提交收据必须持久化。Restore 必须先完整验证版本与资源一致性，再原子发布恢复后的 World；不兼容必须拒绝恢复。

## 10. 失败语义与安全边界

- 不支持规则：Unsupported / Undetermined，不能忽略。
- 规则冲突：明确拒绝或按已声明策略解决，不能靠枚举顺序。
- 搜索预算耗尽：Undetermined。
- 资源不足：Readiness=false / 不准入，不伪造结构无解。
- 资源账户越权：拒绝并保留稳定诊断码。
- 版本指纹不匹配：拒绝 Admission/Restore。
- 事务中途失败：按操作原子边界与提交收据补偿，不做全局想当然回滚。
- Host/插件/内容输入是不可信数据；Core 必须校验长度、数量、版本、身份、参数与预算。
- 宿主不得直接写 Jar/Workshop 内部状态绕过领域端口。

## 11. 分层依赖

依赖方向：

Primitives/Identity -> Configuration & Contracts -> Domain Rules -> Rule Assembly -> Solve Problem -> Solver Algorithms -> Binding/Admission -> Execution/Settlement -> Host Adapter

- Primitives 不依赖 Core 领域。
- Rule Definition/Contribution schema 不依赖 UI、Godot、Space 或具体 Solver。
- Domain Rule Interpreter 不依赖搜索算法。
- Solver 依赖规则评估接口，不依赖 Workshop 实体或宿主。
- Binding/Admission 依赖权威快照和资源账户端口。
- Executor 消费冻结计划和已批准转换，不自行复制判定。
- Host 只负责输入输出、线程/文件/引擎生命周期，不定义玩法规则。
- 兼容 Adapter 不得成为第二套权威数据源。

## 12. 硬编码治理标准

硬编码不是“一切常量都要删除”。必须区分：
1. 语义常量：Neutral/Cathode/Anode、规则结果枚举、协议字段名；可留在 Core。
2. 默认世界内容：酒/蜜、默认形状、默认关卡数值；只能作为显式 Default Fixture/Content Pack，算法不得依赖。
3. 安全护栏：最大搜索预算、最大输入数、序列化大小；必须由 Core 校验，可按受控策略覆盖。
4. 算法策略默认值：候选排序、启发式权重、DFS/剪枝阈值；应配置化或注入，不能成为领域语义。
5. 真实缺陷硬编码：液体/形状/角色/订单类别字符串、列表索引、重复操作 switch、调用方复制规则判断；必须消除。

代码审查必须回答：值属于哪一类？谁拥有它？改变它是否改变规则语义？是否进入正确指纹？其他调用方是否重复解释？

## 13. 强制验证矩阵

| 维度 | 必须证明 |
|---|---|
| Rename Isomorphism | 替换液体/形状身份并同步映射后，结构行为同构 |
| Polarity Swap | 对调身份到极性映射，行为按映射变换 |
| Multi-World Isolation | 不同词表/映射的 World 并存互不污染 |
| Rule Composition | 多贡献组合结果与来源、优先级、冲突确定 |
| Unsupported Fail-Closed | 未知 RuleId/Effect/Version 不会静默跳过 |
| Solver Differential | 多算法对同一 SolveProblem 的状态与证明结论一致 |
| Search Completeness | 完备/不完备/预算耗尽状态准确 |
| Resource Conservation | 每个账户分别守恒或显式 sink/discard |
| Ban Scope | staging Ban 不误伤 Jar 内部资源与 Jar 移动 |
| Mark Lifecycle | 交接不清除 Mark，一罐一操作 |
| Admission Atomicity | 版本/余额并发变化不能造成重复准入或超额消费 |
| Replay | 同输入、同规则版本和命令序列产生相同状态与日志 |
| Checkpoint | 活动会话恢复不重复扣费、不丢失规则版本 |
| Architecture | 层依赖、禁止引用、宿主隔离有机器门禁 |

## 14. 迁移路线

### Phase 0 — 规范与现状映射
冻结术语、规则语义、责任边界和测试矩阵；每项规范映射到代码/测试/缺口。

### Phase 1 — 世界身份与输入目录去硬编码
让 World Vocabulary 成为形状/液体唯一来源；默认常量限制在 Default Fixture；候选生成、快照、生产、订单和序列化不得引用默认内容目录。

### Phase 2 — Rule Registry 与统一规则解释器
从固定 enum/switch 与调用方分散结构迁移到稳定 RuleId/version、声明式定义、参数 schema、输入依赖、冲突策略和统一 evaluator。受控内建算子可保留，但新增规则不应要求修改每个求解器/执行器分支。

### Phase 3 — 通用 SolveProblem 与策略解耦
将订单阶段、岗位拓扑、跨台策略作为显式 policy/constraint 输入；拆除求解器内部对 A/B 字符串、Master/Slave 文案和固定流程的直接依赖。保留现有 A/B 行为作为策略 fixture 与回归合同。

### Phase 4 — 结构解、资源绑定与准入彻底分层
统一 Structural Solution、Resource Binding、Readiness、Manifest、Lease 和 Execution Plan 的版本依赖与缓存键。

### Phase 5 — 通用执行与结算
让规则转换、资源效果、账户准入和 Settlement 使用同一份已验证契约；删除调用方重复 if 与重复资源账目算法。

### Phase 6 — 多算法对拍与压力验证
用不同求解策略对同一问题做差分；扩大固定 Seed 资源守恒、多 World 并发、变更失效、恢复和故障注入样本。

### Phase 7 — 宿主/Space 收敛
Core 只暴露稳定领域契约；Space 只提供身份映射、Board Assignment、订单目标与效果绑定，不复制 Core 规则。

## 15. 完成定义

只有满足以下条件，才可宣称 Core 达到目标架构：领域语义有正式规则定义和来源；可变布置由 World/Board 数据表达；资源状态由权威账户持有；规则判定走统一 evaluator；Solver 算法不持有玩法语义；执行器不重复实现规则；未知规则 fail-closed；结构解与资源就绪分离；版本、指纹、回放和检查点覆盖完整；强制验证矩阵通过；Space 与其他宿主只消费 Core 契约。

本规范不授权一次性删除现有实现。迁移必须逐层进行、保持行为等价，并以测试证明每一步。

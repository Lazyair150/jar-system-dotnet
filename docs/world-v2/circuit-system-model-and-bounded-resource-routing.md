# World V2 电路系统模型与有界资源通路

> 状态：领域架构契约 / 下一阶段实现基线（2026-11-24）。
> 本文定义目标模型与当前实现边界；不把尚未实现的通用规则解释器、完整解集或换台优化器描述为现状。

## 1. 核心定义

World V2 应抽象为一个**有界资源状态转换网络（Bounded Resource Transition Network）**。

它不是把工作台画成节点、把 Jar 画成导线的纯视觉电路图。它是由一组版本化规则算子、元件提供的参数与能力、资源账户、订单目标和运行约束共同构成的可求解系统。

给定以下输入：

- Board Definition 与槽位拓扑；
- exact Board Assignment（slot、component kind、Owned Instance、Definition/version）；
- 每个 Owned Instance 的冻结成长属性与 progression revision；
- World Rule Set、工作台局部属性、岗位关系和资源容量；
- Jar / 液体词表与资源所有权边界；
- 订单目标或换台目标；
- 求解完整性策略、资源限制和搜索预算；

系统应先编译出不可变的 Circuit Image，再对目标构造有限的资源转换路径。

同一份 Circuit Image 可以服务不同订单；不同订单可以得到不同的路径或解集。资源余额改变时，通常只重新绑定资源和评估 Readiness，不应无条件重编译规则网络。

## 2. 六个领域层次

### 2.1 Board Topology：电路板拓扑

Board Definition 只描述可安装结构：

- BoardId / DefinitionVersion；
- SlotId、componentKind、areaId、roleId；
- 插槽约束、必需性、数量与兼容性；
- 槽位之间明确声明的结构关系（若某个领域确实需要）。

Board 不持有玩家安装结果，不持有成长数据，不直接执行规则。

Board Assignment 是玩家的独立安装映射。相同 Definition 可以有多个 Owned Instance；相同 Owned Instance 不得同时安装在多个槽位。主台/从台是槽位角色，不是工作台 Definition 的固有身份。

### 2.2 Component Instance：元件实例

元件分为两层：

- Definition：可复用、精确版本化的设计蓝图；
- Owned Instance：玩家拥有的具体实例，包含稳定身份、成长等级、经验、属性、成长曲线来源和 revision。

元件类型不是规则类型。它只是规则贡献者和子系统运行能力的来源。

元件职责：

- Workshop：提供工作台局部标记、Ban、容量、暂存资源容器与岗位执行参数；
- Ore Parser：提供有限输入解析能力、输入队列、处理速率、产出阈值和 Parser-local 规则；
- Skill / Order：将玩家能力映射到 Core 拥有的订单模板，并提供 Space 侧效果绑定；
- Production Device：提供其所属生产子系统的能力与规则贡献。

组件安装关系决定规则来源；成长属性改变贡献参数，但不能改变来源身份或偷偷替换 Definition 版本。

### 2.3 Rule Algebra：规则代数

规则不是任意回调。每条可冻结规则必须具备：

- RuleId、RuleVersion；
- RuleKind / Effect；
- Scope 与 Target；
- Applicability / Preconditions；
- 确定性 Decision：Allowed / Rejected / Undetermined；
- Transition（适用时）；
- ResourceEffect（适用时）；
- FailureSemantics 与稳定 ReasonCode；
- 对世界事实、资源账户和时间窗口的依赖声明；
- 来源 Definition、Owned Instance、progression revision、Board Slot。

规则形态至少区分：

1. Constraint：限制候选或执行条件，不直接改变资源；
2. Transform：改变 Jar 状态，例如 PourIn、PourOut、Reshape；
3. ResourceFlow：接收、扣除、转移、返还、丢弃或锁定资源；
4. Lifecycle：Mark、订单阶段、冷却、替换提交、规则窗口等生命周期语义；
5. Production：有限输入处理与离散产出，必须声明输入消耗、产出批次和归属。

规则贡献（Rule Contribution）只是元件提交上述规则数据的来源机制，不是完整的规则语义本身。不同 Scope 必须由对应的执行子系统消费；没有消费者的规则必须拒绝或返回 Undetermined，不能静默忽略。

### 2.4 Circuit Compiler：电路编译器

编译器将冻结的 Board Assignment、Definition、成长属性、世界规则、词表和结构参数合并为 Circuit Image。

Circuit Image 至少包含：

- Board identity/version 与 Assignment fingerprint；
- 规范化、去重、排序后的规则集合；
- 每条规则的精确来源与参数；
- 规则依赖清单；
- 各子系统消费者的能力声明；
- 未实现规则/不透明条件的诊断；
- 规则集合、结构配置和编译器版本指纹。

编译成功不代表规则全部可执行。编译结果必须区分 Supported、Undetermined、Rejected；不支持的 RuleKind 不得被当作无效果的空操作。

Circuit Image 在一次战斗/运行配置期间冻结。成长、安装或定义版本变化应产生新的编译结果，不允许在当前运行中悄悄改变既有规则。

### 2.5 Solver：路径求解器

求解器输入 Circuit Image、目标与世界结构约束，产出一个或多个资源转换计划。

输出至少区分：

- Complete：搜索完整结束；
- Infeasible：完整搜索后确认无解；
- Undetermined：规则不透明、能力未实现、输入不完整或预算耗尽；
- Feasible solution set：一个或多个完整可执行计划；
- 每个计划的有序操作/DAG、资源需求证明、Jar 角色绑定要求、跨工作台交接、丢弃/溢出结果和解释轨迹。

找到一个可行计划，不代表解集已穷尽。预算耗尽不等于无解。解集可以惰性枚举或 Top-K，不得无界物化。

### 2.6 Runtime Interpreter：运行时解释器

运行时只执行已经编译并通过求解/准入的计划，不再复制规则语义。

每一步执行：

1. 读取当前权威资源状态；
2. 检查该步动态前置条件；
3. 用统一规则 evaluator 作出判定；
4. 原子应用 Jar 转换和资源账户变化；
5. 记录来源、去向、消耗、产出、丢弃与原因；
6. 失败时按明确事务边界提交、回滚或进入不可重试状态。

运行时规则执行和求解器规则评估必须共用同一规则 evaluator / transition contract，避免“求解允许、执行拒绝”或反过来的语义漂移。

## 3. 资源账户：电路中的流动介质

Jar 和液体不是一个可以相互折算的总数。每种资源必须保留形态、来源、所有权、位置和生命周期。

至少区分：

- Ship Jar Inventory；
- Workshop Jar Inventory（按具体 Owned Workshop / Board Slot）；
- Jar Internal Liquid；
- Workshop Free Liquid Staging；
- Ship Free Liquid Inventory；
- Master-to-Slave Transfer / Prefill；
- Parser Input Stone Queue；
- Parser Pending Output Outbox；
- Core Central Warehouse；
- Order Resource Lease / Reservation。

所有转换都要声明来源账户和目标账户。

已确定的不变量：

- 工作台 Ban 约束该工作台暂存区液体的取用，不是全局 Jar 内液体禁令。
- Jar 内液体自耗不能冒充工作台暂存区液体检查。
- 主台 Ban 的液体不能由主台传给从台。
- 从台使用期间只接收主台提供的液体；主从资源流是单向的。
- 订单产生的游离液体先尝试进入当前工作台暂存区；拒绝或超限按规则丢弃。
- Jar Core 的中央仓库是 Parser 完成 Jar 的唯一接收入口；Parser 不得直灌主台或从台。
- 资源账户变化必须通过明确的 ResourceEffect 记录，不能只改总余额。

## 4. 两种不同的求解方向

### 4.1 订单：从目标反向求前驱

订单只声明最终目标，例如需要哪些形状、是否含液体、数量与订单阶段语义。它不预先指定具体 Jar、工作台分配、液体来源或操作顺序。

反向求解的基本过程：

1. 把订单归一化为最终目标状态/需求多重集；
2. 枚举能产生目标状态的规则算子及其前驱状态；
3. 对每个前驱状态继续展开，直到抵达可绑定的初始资源角色或不可行边界；
4. 累计每条路径对不同资源账户的需求；
5. 应用 A/B 订单阶段约束、主从单向交接与工作台能力约束；
6. 合并等价状态、进行有证明的剪枝；
7. 产出计划、资源需求集和完整性状态。

这不是简单地对每个操作求数学逆函数。PourOut 可能因 staging 拒收而丢弃液体；规则可能是多对一或有条件的；因此求解器应枚举合法前驱，而不是假定所有转换可逆。

结果应回答：要完成该订单，最初至少需要哪些形状/液体 Jar、哪些游离液体账户的数量、哪些资源必须在何时位于哪个工作台，以及中间会发生哪些消耗和丢弃。

### 4.2 换台：从当前资源向前找最佳转换

换台不是订单求解的反向版本，也不是简单搬运所有 Jar。

它从当前工作台资源状态出发，以目标工作台/目标岗位的规则和容量为约束，正向枚举可执行的转换与迁移：

1. 捕获当前资源及其所有权；
2. 捕获目标工作台的规则、Ban、标记、容量与岗位约束；
3. 枚举允许的倒液、倒罐、改形、转移、暂存和丢弃操作；
4. 在每一步应用相同的 Rule Evaluation / ResourceEffect；
5. 对可行终态按资源保留、目标可用性、操作成本、时间和丢弃量排序；
6. 输出 MigrationPlan、资源结算清单与失败原因；
7. 经过准入和原子提交后才改变权威资源归属。

“尽量转化”必须有明确目标函数与硬约束。不得假定更多液体、更少 Jar 或更少 Ban 总是更好；目标函数应由玩法规则明确指定。主台/从台岗位替换、工作台实例替换和主台资源转移也必须保持不同语义。

## 5. 固定电路与有限路径的含义

当 Board、Assignment、Definition 版本、Owned Instance 成长快照、规则参数、世界结构和目标订单固定时，Circuit Image 与结构搜索空间固定。

但不能因此声称解集天然有限或总能完整枚举。规则图可能存在循环，多个规则可能产生大量组合。要得到可计算的有限路径，必须有明确边界：

- 每条操作的资源消耗/状态变化；
- 操作深度、成本或逻辑时间预算；
- 有界资源数量与有限状态词表；
- 对重复状态的规范化与循环检测；
- 基于支配关系的安全剪枝；
- 对不改变目标进展的零成本循环作出拒绝或折叠；
- 明确预算耗尽时返回 Undetermined。

因此“暂时固定的复杂通路”应理解为固定配置下的**有限可枚举/可预算搜索空间**，不是预先烘焙的一条永不改变的路线。

## 6. 缓存与动态资源

分离以下指纹：

- CircuitRuleFingerprint：规则语义、参数和有效窗口；
- BoardAssignmentFingerprint：安装关系；
- ComponentGrowthFingerprint：实例成长快照；
- StructuralProfileFingerprint：工作台能力、容量、岗位和结构依赖；
- OrderTargetFingerprint：订单目标；
- ResourceBindingFingerprint：真实 Jar、液体余额、位置、所有权与预留；
- ExecutionPolicyFingerprint：预算、完整性策略与目标函数。

结构解集缓存键不应包含每个 tick 变化的所有库存余额；Readiness/ResourceBinding 负责检查当前资源能否绑定到结构计划。若规则声明确实依赖资源余额，则必须把该依赖显式纳入对应结构/规则指纹。

Admission 必须对当前资源原子重验并创建 Lease；缓存命中绝不等于资源已锁定。

## 7. 当前代码与目标模型的差距

已存在且可复用：

- Board Definition / Slot / Assignment Map 与 exact Owned Instance 身份；
- Definition 与 Owned Instance 分离及版本化成长；
- Space Rule Contribution 声明、Workshop Core-facing Profile 和 Parser-local Profile；
- Core EffectiveRuleProfile / RuleContext；
- Core 四种 Jar 基础操作相关规则、A/B 分支搜索、符号操作程序、资源模拟、计划、Admission/Lease、执行与结算；
- Workshop replacement / ResourceTransferrer；
- Parser 的有限输入队列、累计产出账本、pending outbox 和 Core import operation ID。

尚未完成：

1. 已新增共享 RuleEvaluationResult / RuleTransition / RuleResourceEffect / RuleResourceAccount 契约；四种化学操作由 Workshop Adapter 消费权威 proof，MATCH 由独立生命周期 Adapter 消费权威 proof。Mark 的产生/清理周期已部分审查，但所有跨台路径的周期测试仍需补齐；Transfer、Staging admission、Capacity、Parser、Skill 和 Production Device 的完整共享语义仍未覆盖，契约也尚未成为所有执行器的唯一入口。
2. 现有 RuleContext 仍只解释部分贡献效果；ModifyCapacity 保持 Undetermined。
3. Parser 规则由单独窄执行器消费；Skill 和 Production Device 的完整规则执行语义仍缺失。
4. Board Runtime Profile Set 是消费者身份/配置路由，不是电路编译器或规则执行器。
5. Core 现有求解器有 A/B 搜索、符号操作与资源模拟，但不是完整通用规则驱动的前驱搜索内核，也没有通用完整解集保证。
6. 当前求解输入版本仍混合部分资源库存与结构事实；Symbolic Solution / Readiness 的拆分尚未完整落地。
7. 工作台替换与 ResourceTransferrer 已有资源迁移实现，但还没有统一的目标规则驱动 MigrationPlan 搜索与优化器。
8. 当前 Space 生产效果/技能执行器仍未覆盖所有技能类别；不能把消费者路由存在当成效果可运行。

## 8. 实施顺序

### Phase 1：规则代数契约
共享 Rule Evaluation / Transition / ResourceEffect / ReasonCode 基础契约已建立；四种化学操作与 MATCH 已分别适配权威 proof。下一步补齐 Mark 周期测试、Transfer、Staging admission、Ban/Capacity 的账户级结果与事务语义。旧规则实现继续作为差分 Oracle。

### Phase 2：统一评估与执行入口
让 Workshop 门面、求解器和执行器调用同一个规则 evaluator。未实现语义返回 Undetermined；不得在某一层静默忽略。

### Phase 3：Circuit Compiler
从冻结 Board Assignment 与元件规则贡献生成规范化 Circuit Image，验证消费者能力、来源身份、作用域、冲突、指纹和未实现项。

### Phase 4：订单反向前驱搜索
保留现有 A/B 分支策略作为业务约束/枚举策略，逐步把规则选择和状态转换交给统一规则内核；补齐多解、资源证明、完整性状态与 Readiness 分离。

### Phase 5：换台正向迁移求解
复用同一 Rule Evaluation / ResourceEffect，新增目标工作台约束下的 MigrationPlan 搜索与明确优化目标；保留现有提交、回滚、预置液与资源所有权边界。

### Phase 6：运行时解释器与清理
统一计划执行、资源账本、checkpoint 和重放；确认新旧语义差分一致后，逐项删除重复规则投影与旧实现，不提前清理仍承担恢复责任的状态。

## 9. 验收标准

- 同一 Circuit Image + 目标 + 结构输入产生稳定的规范化计划集合/指纹。
- 求解器与执行器对同一规则、状态和操作给出一致判定与状态转换。
- 任意贡献都有可追踪的 Definition、Owned Instance、revision 和 slot 来源。
- 未支持的规则、opaque 条件、预算耗尽不会被误报为不可行。
- 订单反向计划能给出按资源账户区分的最初需求集。
- 换台计划能从当前资源正向模拟目标规则下的可行转换，并解释保留、消耗、转移与丢弃。
- 只改变资源余额时可更新 Readiness；只有结构/规则/目标依赖改变时才重算结构解。
- 主台 Ban、Jar 内液体自耗、主从单向传递、游离液体 staging 与 Parser 仓库入口等既有不变量不回退。
- Checkpoint 只保存恢复权威世界所需事实；Circuit Image/计划缓存可重建，不作为权威资源状态。

## 10. Phase 1 implementation slice: shared transition contract

Implemented in Core:

- `RuleEvaluationResult` carries Allowed / Rejected / Undetermined, stable ReasonCode, diagnostic text and an optional executable transition. Validation forbids transitions on rejection/undetermined and requires one on Allowed.
- `RuleTransition` carries stable RuleId/version, scope, target, before/after state payloads and explicit resource effects.
- `RuleResourceEffect` carries resource kind/identity/amount and exact account endpoints. Consume, Produce, Transfer and Discard have distinct endpoint invariants.
- `RuleResourceAccount` addresses a concrete owner identity; role labels and display names are not accepted as account ownership.
- `WorkshopRuleTransitionAdapter` adapts the existing four Workshop chemical operation proofs to the shared contract. PourIn and ReshapeEmpty consume Workshop free-liquid staging; PourOut produces into the current Workshop staging account; ReshapeFull consumes the liquid owned by that exact Jar. It does not confuse Jar-internal self-consumption with Workshop free-liquid balance.

Tests authored for these invariants in `RuleTransitionContractTests`. This is a compatibility adapter slice, not yet a replacement for WorkshopRulebook, not yet the Circuit Compiler, and not yet a universal evaluator. MATCH remains outside `WorkshopOperationKind` and is now consumed by the separate `MatchRuleTransitionAdapter`; this does not yet make it the universal execution entry point.
- State before/after is represented by `RuleStateSnapshot`, a sorted, immutable key/value snapshot; mutable domain object references are not embedded in the transition contract.


## 10. 共享规则转换契约：当前落地细节（2026-11-24）

实施记录与逐项验收清单见 [规则转换契约实施记录](rule-transition-contract-implementation-log.md)。

当前 Core 已新增 `RuleEvaluationResult`、`RuleTransition`、`RuleStateSnapshot`、`RuleResourceEffect` 与 `RuleResourceAccount`。这组类型只表达一次规则评估的确定结果、状态前后快照和资源账户变化，不直接修改库存，也不替代事务层。

### 10.1 三态结果是硬约束

- Allowed 必须携带 Transition；
- Rejected 与 Undetermined 不得携带 Transition；
- 每个结果必须提供稳定 ReasonCode。

这保证未知条件不会变成可执行操作，也不会把“尚不能证明”误报成“确定无解”。

### 10.2 资源账户不是角色标签

账户由 AccountKind 与精确 OwnerId 构成。工作台暂存液体与 Jar 内液体是两个不同账户：

- `workshop.free-liquid-staging:<ownedWorkshopInstanceId>`
- `jar.internal-liquid:<jarInstanceId>`

因此 PourIn 消耗工作台暂存液体，而 ReshapeFull 的自耗消耗 Jar 内部液体。两者不得共用一个“液体库存”账户，也不得因主台/从台角色交换而改变 Owned Instance 身份。

### 10.3 现有 Adapter 的真实边界

`WorkshopRuleTransitionAdapter` 目前适配旧 WorkshopRulebook 的四种化学操作，并将旧规则判定和状态转换转译为共享契约。它没有重新实现规则，也没有提交资源账本。

特别注意：PourOut 的 Produce 指向当前工作台暂存账户，只表达“尝试产出到此账户”的转换意图；staging admission、容量、接收拒绝和溢出丢弃仍须由后续独立规则/事务表达。不能把 Produce 记录当作已经成功入账。

MATCH 不是普通 WorkshopOperation，现已由独立 Adapter 适配其生命周期证明。Mark 周期验证、Transfer、Staging admission、Ban/Capacity 的完整事务表达仍是后续工作，不得根据契约类型已存在而推断完成。

### 10.4 验证状态

在提交 `e6f68e0ea21bce0db993a60358e8c8f030c47834` 上，GitHub Actions run `45360457315` 的 verify job 已成功。该结论只覆盖该 SHA 上仓库配置的 CI 工作流；Godot Probe 和未实现语义不在此结论范围内。

### 10.5 紧接着的工作

按实施记录的顺序先完成 Mark 周期边界测试，再补 staging admission 与主从 Transfer。MATCH 独立 Adapter 已落地，每一步继续保留旧实现作为差分 Oracle。上述规则语义与事务结果可被完整表达、验证并应用前，不进入通用 Circuit Compiler 或重写求解器。


## 11. 电路中的电流：Jar 与液体资源流

这里的“电流”不是第三种资源，也不是 UI 动画或抽象电信号。**Jar 和液体就是电流本身**，但它们具有不同的资源形态：

- Jar 是离散资源，具有稳定身份，并携带形状、内部液体与 Mark 生命周期状态。Jar 在工作台、订单阶段和资源账户之间流动；规则可以改变它的状态，也可以把它交付或丢弃。
- 液体是可计量资源，至少分为 Jar 内部液体与工作台游离液体暂存。前者属于具体 Jar，后者属于具体工作台实例的 staging account。
- Rule 是电路中的阀门、转换器和约束：决定资源是否可通过、如何改变状态、从哪个账户扣除、向哪个账户产出，以及拒绝/溢出时如何处理。
- Board 与 Assignment 决定哪些规则元件被安装以及它们的身份；它们定义可用通路，不是资源本身。
- Solver 不“制造电流”，而是根据订单目标反向寻找能产生目标 Jar 状态的资源路径；换台求解则从当前资源正向寻找目标规则下的迁移路径。

因此，任意一条可执行路径都必须同时回答两个问题：**Jar 状态如何变化**，以及**Jar/液体资源在账户之间如何流动**。只输出 Jar 的 After 状态、却遗漏液体消耗/产出/转移/丢弃，不构成完整的电路转换证明。

### 11.1 Ban 的电路边界

工作台 Ban 约束该工作台游离液体暂存账户的取用与接收。Jar 内部液体属于 Jar 自身资源，满罐改形自耗不读取工作台 staging，也不因 staging Ban 而失败。PourIn 与空罐改形成本仍然从 staging 取用液体，因此受 Ban；PourOut 产液进入 staging 时也必须接受目标 staging 的 Ban/容量准入。

这条边界不允许把 staging 液体装入 Jar 后再绕过其原本应接受的 staging 准入；但它也不把一个从未经过该 staging 账户的 Jar 内部液体错误计入 staging 库存。


### 11.2 Jar 的三态极性（派生状态）

当前液体词汇为两种，连同空罐构成三种 Jar 离散状态：

- 空罐 `null` → `Neutral`（中性）；
- 酒液 → `Cathode`（阴极）；
- 蜜液 → `Anode`（阳极）。

这是 Jar 内部液体身份的**派生分类**，不是额外资源，也不是独立可变字段。Core 通过 `JarPolarityResolver` 唯一映射，并由 `Jar` 与 `JarFacts` 暴露只读派生属性；未知非空液体必须显式失败，不能猜测极性。规则转换快照同时记录实际 `liquid` 与派生 `polarity`，使求解模型可以使用统一的三态分类，同时保留实际资源身份。

极性状态只回答“Jar 当前属于哪一类”，不回答液体数量、账户所有权、所在工作台或可否被某个规则接收。所有资源流仍由 Jar/Liquid ResourceEffect 表达，工作台 Ban 仍只作用于该工作台的游离液体 staging。

这里借用 Neutral / Cathode / Anode 作为游戏领域术语，不模拟真实电学或电化学。不要据此引入电压、电流强度、电阻或把极性推广成液体数量。未来若新增液体类型，必须先明确其极性映射或扩展状态模型，未知类型不能自动落入已有三态。


### 11.3 三态极性的输入兼容与输出契约

Jar 三态现已成为规则输入/输出的可验证边，而不是 UI 标签：

| Rule | 输入极性 | 输出极性 | 资源边界 |
|---|---|---|---|
| POUR_IN | Neutral | Cathode / Anode | 消耗当前工作台 staging |
| POUR_OUT | Cathode / Anode | Neutral | 产出到当前工作台 staging，受 Ban / 容量准入 |
| RESHAPE_EMPTY | Neutral | Neutral | 消耗当前工作台 staging 成本 |
| RESHAPE_FULL | Cathode / Anode | Neutral | 消耗 Jar 自有内部液体 |
| MATCH | 任意合法状态 | 保持不变 | 不产生液体资源流 |

`JarPolarityRuleContract` 对共享 `RuleTransition` 执行输入/输出极性边校验。它不执行资源记账；`ResourceEffect` 仍然是资源流的唯一表达。

求解器的 `JarState` 与 `Requirement` 暴露只读派生 `Polarity`，StateSpaceSolver、MatchingSolver 与 SolverPrecheck 用它区分空/满状态。精确液体身份仍用于需求匹配、数量核算和 staging admission。状态键继续只编码实际液体 ID，不重复编码其派生极性。

**关键 Ban 区分**：满罐自耗改形只消费 Jar 内部资源，不读取工作台 staging Ban；PourOut 才把 Jar 内液体送入 staging，因此仍必须经过 Ban 与容量检查。主从转移 Jar 本身不等于转移游离液体。


### 10.6 本轮新增：非 Jar 状态转换与 staging admission

共享 Transition 现在允许两种明确状态形态：Jar 转换必须绑定 exact Jar identity 并带派生极性；非 Jar 转换不得伪造 polarity。Staging Admission 使用非 Jar 快照表达工作台暂存占用与订单子账余额。

`StagingAdmissionTransitionAdapter` 消费 `WorkshopLiquidStaging.Receive` 的真实 receipt，将请求量、接收量和丢弃量拆成可守恒的资源效果：临时订单输出边界 Produce、接收部分 Transfer 到当前工作台 staging、拒收/溢出部分 Discard。新增 `WorkshopLiquidStaging.ReceiveWithTransition` 将权威 Receive 与该 Transition 配对返回；旧 `Receive` 保留兼容，其他生产输出入口仍需迁移。Adapter 本身不写入资源账户。

本轮还校正了差分参考求解器：其旧版满罐改形路径曾错误套用 staging Ban。现在参考实现与“Ban 只约束工作台游离液体 staging”的领域契约一致，并新增被 Ban Jar 内液体自耗的定向差分场景。Mark 跨主从交接保留、成功终态清理与失败终态清理也增加了执行会话回归断言。

当前 head 的 CI 尚待完成；仅 `4f9f98a` 上的 verify 步骤已成功，不能据此宣称后续 staging adapter 和 Mark 断言通过。下一步继续完成 Transfer 的统一 ResourceEffect，再接线实际 staging 入口；此时仍不进入 Circuit Compiler。


### 10.7 主从 Transfer 适配器与身份缺口

新增 `WorkshopRoleTransferTransitionAdapter`，为 B 类主→从交接构造显式资源效果。每只 Jar 按 exact Jar ID 从来源工作台库存 Transfer 到目标工作台库存；游离液体按液种/数量从来源 staging Transfer 到目标 staging。构造前校验来源 Jar 所有权、重复 ID、目标罐位、来源液体余额、目标 Ban 和目标 staging 总容量。所有 Transfer 都绑定精确 source/destination instance ID，不使用主台/从台角色名作为 OwnerId。

该适配器尚未接入 `OrderTransferService`。当前旧 Core `Workshop` 对象只有显示名称，没有可信 Owned Instance ID。不能把 Name 当作稳定资源所有权身份；需要由 World/Host 从 Board Assignment 提供 exact Owned Instance ID，并在实际交接事务中生成/提交同一份 Transfer receipt 与 Transition。

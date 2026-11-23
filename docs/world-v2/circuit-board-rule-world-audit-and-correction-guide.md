# Jar World V2 电路板式规则世界：运行模型审查与修正指导

> 状态：领域审查结论与强制迁移指导（2026-11-24）
> 审查对象：Jar Core / World V2，以及 Space Demo Hangar 到 World V2 的装配边界
> 依据：Jar Core PR #60 head `063e2b7151ea43e92cf6ba1d627b9c7d50819b5b`；Space Demo 分支 `world-v2/phase-4.10b-hangar-ui` 本轮审查源码
> 本文区分“当前实现事实”与“目标架构”。本文不是实现完成声明。

## 1. 最终领域定义

Jar World 应被理解为一块由规则构成、可通过安装与替换零件改变能力的电路板。

- **规则板（Rule Board / Rule Profile）**：当前世界在一个明确版本边界下生效的规则集合。它不是某个工作台，也不是订单流程。
- **槽位（Slot）**：规则板上的安装位置。槽位声明兼容类别、约束和作用域，不直接实现规则。
- **零件定义（Component Definition）**：可安装元件的模板，声明其提供哪些规则贡献、参数、约束或资源能力。
- **拥有实例（Owned Component Instance）**：玩家持有的独立元件个体。实例有独立成长状态；安装、替换、卸下不改变实例身份或成长。
- **规则贡献（Rule Contribution）**：零件安装到槽位后，向规则板提交的一组有稳定身份、版本、参数和作用域的规则定义/修饰声明。
- **Jar 与液体**：在规则世界中流动的资源状态。Jar 内部液体、工作台游离液体、订单暂存、主台预置液、自机液库等必须分账；它们不是规则本身。
- **订单（Order / Demand）**：目标订单只声明最终需要得到的 Jar 结果及业务阶段语义，不指定由哪个工作台、哪只 Jar、哪个算子或哪条操作路径完成。
- **求解器（Funnel Solver）**：读取规则板、已安装零件产生的有效规则、订单目标、工作台/资源事实，推导抽象操作程序、资源来源与物理绑定；不维护另一套独立规则语义。
- **执行器（Executor）**：执行已准入计划，并在动态操作边界再次验证规则和资源前置条件。

核心方向：

`Installed Components + Board Constraints -> Effective Rule Set`

`Effective Rule Set + Order Goal + World Facts -> Symbolic Solutions`

`Symbolic Solutions + Current Jars/Liquids -> Resource Binding / Readiness`

`Admission -> Execution -> Settlement`

求解器不是把零件拼成一条预制生产流水线，而是在当前规则集合下构造可满足目标的算法/操作程序。相同订单在不同规则板、不同安装组合下可以得到不同解；同一规则板在资源状态变化时，结构解不必重新计算，只需更新资源就绪状态。

## 2. 零件安装与规则装配的契约

### 2.1 槽位与实例

槽位属于 Board Definition；安装关系属于玩家的 Board Assignment。两者不得合并。

每个 Assignment 至少冻结：
- slot_id；
- component_kind；
- owned_instance_id；
- exact definition_id / definition_version；
- contribution schema/version；
- assignment revision 或 board configuration revision。

验证必须包含：
- 槽位存在且唯一；
- 元件类别与槽位兼容；
- Owned Instance 属于玩家库存；
- 实例精确指向对应 Definition 版本；
- 同一个 Owned Instance 不得同时安装到多个互斥槽位；
- 两个不同 Owned Instance 即使 Definition 相同，也必须保持独立成长和独立身份；
- 空槽、必需槽、可选槽和重复安装策略由槽位契约明确；
- Assignment 的规范化排序必须稳定，顺序不能无意改变语义或 fingerprint。

主台/从台是工作区角色位置，不是工作台 Definition 固有属性。玩家可以把不同工作台实例放入主台/从台槽；两个实例可以同 Definition，但仍是不同个体。订单区、分解区、生产区遵循同一原则。

### 2.2 Rule Contribution

零件不能通过把自定义逻辑塞进任意闭包来“提供规则”。规则贡献必须是可审计、可版本化、可重建的声明。

建议至少区分：
- Constraint：限制候选操作、输入状态、目标状态或资源来源；
- Transform：定义 Jar 状态转换；
- Resource Flow：定义资源消耗、产出、转移、返还、丢弃；
- Lifecycle：定义 Mark、订单阶段、冷却窗口、生效窗口等；
- Modifier：对已有规则进行 Grant / Suppress / Require / Retime / Intensity 等声明式修饰；
- Capability：工作台/设备提供的容量、支持形状、吞吐等事实。Capability 是规则评估输入，不等于规则本身。

每条贡献必须声明稳定 RuleId、RuleVersion、贡献来源 Definition/Instance、参数、作用域、适用条件、前置条件、状态转换/资源效果、失败语义与诊断原因码。

临时规则必须有明确的生效和失效边界：安装后进入有效 Rule Profile；替换/卸下后产生新的 Profile revision/fingerprint；旧订单会话使用已冻结的规则版本或按既定中断策略结束，不允许执行中途悄悄改变语义。

不透明外部谓词可以存在，但必须显式标记 opaque / non-cacheable；不能被静默忽略，不能把 Undetermined 当作 Rejected 或 Infeasible。

## 3. Jar 与液体的资源语义

Jar 和液体是电路板上流动的“电”，但资源状态与规则配置必须分离。

必须保持独立的资源账户：
1. Jar 内部液体；
2. 工作台游离液体；
3. 当前订单暂存子账；
4. 主台预置液；
5. 从台接收的主台交接资源；
6. Space 自机液库/生产资源；
7. 其他明确声明的资源账户。

基础 Jar 算子当前语义继续作为兼容基线：
- PourIn：空罐灌入液体，消耗当前工作台允许取用的游离液体；
- PourOut：满罐倒出，产出先进入当前工作台订单暂存；拒收或超容量按既定规则丢弃；
- ReshapeEmpty：空罐改形，消耗指定游离液体成本；
- ReshapeFull：满罐改形，消耗 Jar 内部液体，结果为空罐，不把该液体当作工作台暂存区输入；
- MATCH：订单匹配/占用与标记生命周期，不是化学转换算子；
- Mark：订单内操作锁，不能因主从转移而清除；
- Transfer：资源/归属转移规则，不能绕过来源工作台约束。

Ban 只作用于对应工作台暂存区的液体入口/取用语义，不能错误扩展到 Jar 内液体自耗。主台 Ban 液体不能通过装 Jar 绕过后传给从台。B 类订单允许需要液体；从台使用期间只接收主台提供的液体，主从流向单向。

## 4. 当前代码审查结论

### 4.1 已符合或可复用的基础

1. `WorkshopCapability` 已将基础标记、附加标记、Ban 表达为能力参数；`Workshop` 持有 Jar 和游离液体资源，并提供资源操作入口。
2. `RuleContext` 已把工作台基础能力与活跃 ModifierStack 汇合；`WorkshopRulebook` 已提供稳定规则 ID/version、Allowed/Rejected/Undetermined、纯值转换结果。
3. 四种基础 Jar 算子和 MATCH 已有统一规则判定入口；操作转换结果包含 Jar 终态及游离液体/Jar 内液体效果。
4. `WorldOrderRuleCandidateProfile` 已能按订单需求、规则上下文和工作台 Profile 生成候选操作，不绑定物理 Jar ID。
5. 求解链已拆出符号候选、操作程序、资源模拟、物理 Jar 绑定、资源 Manifest、Admission/Lease、执行 Session 与 Settlement 等边界。
6. A/B 分支枚举已处理不同工作台角色；B 类路径有主台准备、主到从交接、从台规则与液体依赖约束。
7. `WorkshopRoster` 已将工作台实例集合与 Master/Slave 岗位指向分离；`WorkshopReplacementProcess` 有岗位替换生命周期和主台资源迁移/回滚边界。
8. Space Demo 已有 Board Definition / Slot Definition、Owned Instance、成长快照和 Hangar 装配入口的基础；Board 结构校验与当前 Runtime Projection 校验也已分离。

这些基础应保留并演进，不应推倒重写。

### 4.2 明确偏离目标模型的部分

#### A. 规则目录仍是 Core 固定算子目录，不是可组合的零件规则板

当前 `WorkshopRulebook` 的规则身份目录以四种固定 WorkshopOperation 算子和 MATCH 为中心。操作种类由固定 enum 与 switch 分派，增加新的规则贡献仍需要修改 Core 的操作类型、评估和转换实现。它是统一了基础语义的规则入口，但不是由已安装零件动态组合的通用规则注册/求值系统。

**偏差**：规则的最终来源仍主要是 Core 预定义算子 + WorkshopCapability/ModifierStack，而不是完整的 Installed Component Contributions。

#### B. 零件安装尚未形成 Jar Core 的规则装配输入

Space Demo 的 Board/Owned Instance 目前主要被 Owned Loadout Assembler 投影成船、Parser、Master/Slave Workshop、设备、技能等专用字段。Hangar 的 Board Assignment 统一模型尚未完成；更关键的是，当前 Board Assignment 也尚未作为完整、版本化的规则贡献集合传入 Jar Core。

**偏差**：Board 目前更接近 Hangar 的装配布局定义，不是 Jar World 的运行时规则板。

#### C. Workshop Capability 与 Rule Contribution 的界线尚未完全建立

工作台的标记、Ban、容量、规则修饰器可被规则上下文读取，但不同来源（基础工作台 Definition、Owned Instance 成长、Board Slot、临时技能/订单效果、世界级规则）的贡献身份和优先级/冲突策略没有统一的装配产物。

**偏差**：当前能读取能力和修饰器，不代表任意零件都能向规则板提供可组合规则。

#### D. 求解器仍包含固定的规则候选构造与订单类别策略

`WorldOrderRuleCandidateProfile`、`WorldOrderAClassBranchEnumerator`、`WorldOrderBClassBranchEnumerator`、符号程序构造器等已经比预制订单流程更接近规则推导，但候选生成仍直接枚举当前固定操作种类、形状和液体词表；A/B 分支是明确的领域策略。它们不能被误称为已经完全通用的任意 Rule Contribution 求解器。

**偏差**：求解器的搜索策略仍部分知道固定算子和订单类别；规则候选尚未完全由可组合规则贡献提供。

#### E. 订单目标与规则板的输入契约还没有彻底统一

OrderTemplate 已有目标需求模板入口，但必须保持模板只描述最终目标和业务元数据，不把主从分配、物理 Jar 选择、液体消耗路径或固定操作步骤写入模板。订单目标应作为求解输入之一，与规则板并列，而不是把订单模板当作规则集合。

#### F. 工作台替换过程还不是通用槽位替换

Core 的 `WorkshopRoster` 目前表达一组 Workshop 与 Master/Slave 岗位；`WorkshopReplacementProcess` 分别处理主台资源迁移与从台空位替换。它解决了运行时岗位替换，但还不是任意 Board Slot 的通用 Assignment 替换。Space Board 与 Core Roster 之间仍需要清晰的 Adapter/Projection 边界。

#### G. 结构解、资源就绪与缓存指纹尚需继续分层

当前已有符号候选和资源模拟分层，但部分 WorldSnapshot/WorkshopSolveSnapshot 仍携带实时 Jar/液体余额，现有求解缓存/版本路径并非所有位置都彻底区分结构性规则输入与资源绑定输入。必须按规则依赖声明判断何时重新求解，不能简单以全世界每次资源变化都重建结构解，也不能在规则依赖确实受影响时错误复用旧解。

### 4.3 审查总判定

**当前判定：部分符合，整体尚未达到电路板式可组合规则世界。**

更精确地说：
- 资源与规则边界：已有正确基础；
- 固定基础算子的统一执行语义：已有相当基础；
- 从订单目标推导操作候选：已有实现；
- 主从岗位与单向资源关系：已有专门实现；
- 玩家可替换零件的统一 Assignment：Space 侧尚未完成；
- 零件向 Jar Core 提供可版本化 Rule Contributions：尚未形成完整链路；
- 规则板装配与动态 Rule Profile：尚未完成；
- 对任意贡献规则进行通用候选枚举/求解：尚未完成；
- 通用槽位替换驱动 Rule Profile 重建、缓存失效与执行会话版本边界：尚未完成。

不能因为当前代码中存在 Rulebook、Board、Owned Instance、Solver 这些类，就声称完整电路板模型已经实现。

## 5. 正式目标运行流程

1. **定义槽位**：Board Definition 声明槽位 ID、兼容元件类别、必需性、数量约束和作用域。
2. **玩家安装实例**：Board Assignment 将 Owned Instance 精确安装到槽位；可替换、卸下、重装。
3. **解析贡献**：根据实例 exact Definition version 与成长快照，生成 Rule Contribution；成长属性只影响其所属组件明确声明的能力参数/贡献参数。
4. **装配规则板**：收集世界基础规则、Board Slot 约束、所有已安装零件贡献、有效临时 Modifier；按确定性优先级和冲突策略合并，生成不可变 Effective Rule Profile。
5. **冻结求解输入**：冻结订单目标、Rule Profile、工作台属性、岗位关系、世界结构约束和求解预算，生成 SolveProblem。
6. **构造结构解**：从最终 Jar 需求反推可用规则算子、前驱状态、Jar 角色、操作依赖 DAG 和资源需求公式；输出完整/不完整状态与证明。
7. **绑定真实资源**：将抽象 Jar 输入角色绑定到当前物理 Jar ID、形状、液体、标记、位置和资源账户；计算 Readiness。
8. **准入与执行**：原子验证当前 Profile revision、World revision 和资源余额，创建 Lease；每个操作边界复核规则和动态前置条件。
9. **结算**：按来源账户分别消耗、返还、转移或丢弃资源；提交订单周期事件。
10. **变更与失效**：安装/卸下/替换实例或规则成长变化，生成新的 Assignment revision 和 Rule Profile fingerprint；只重算受影响的结构解，资源余额变化只更新受影响的 Readiness。

## 6. 强制分层与数据模型

建议逐步形成以下核心纯值模型（名称可按现有代码风格调整，不要求一次性照搬）：

- `BoardDefinition`：槽位结构与兼容约束。
- `BoardAssignment`：slot -> owned instance + exact definition identity。
- `RuleContributionDefinition`：组件模板声明的贡献 schema。
- `ResolvedRuleContribution`：实例成长参数代入后得到的精确贡献。
- `EffectiveRuleProfile`：规则板合成后的不可变规则集合，包含规范化排序、来源追踪、版本和 fingerprint。
- `RuleEvaluationContext`：单次求值的只读事实。
- `RuleEvaluation`：Allowed / Rejected / Undetermined + transition + resource effects + reason。
- `SolveProblem`：订单目标、EffectiveRuleProfile、工作台结构事实、资源边界与策略预算。
- `SymbolicSolutionSet`：不绑定当前物理 Jar ID 的抽象解集。
- `ResourceBinding` / `Readiness`：当前物理资源与抽象输入角色的绑定及可执行状态。
- `ExecutionLease`：基于当前版本原子预留资源的执行权。
- `ExecutionSession`：按冻结计划推进，操作边界复核规则。
- `Settlement`：按来源分账结算。

Rule Profile fingerprint 至少应覆盖：
- 世界基础规则版本；
- 每个安装槽位 ID；
- Owned Instance ID；
- exact Definition ID/version；
- contribution schema/version 与规范化参数；
- 成长属性中实际影响贡献的字段及 progression revision；
- 生效的临时规则/Modifier 及其时间窗口；
- 规则优先级、冲突处理策略与作用域。

不要把当前 Jar 库存余额放入结构规则 Profile fingerprint。真实资源状态应进入 ResourceBinding/Readiness fingerprint。若某个规则确实依赖库存事实改变其适用性，必须声明该依赖，并让该事实进入对应求解输入指纹。

## 7. 迁移顺序（不可跳步）

### Phase A — Space Board Assignment 成为唯一装配数据源
- 建立统一 Assignment Map；替代 SelectionDraft 的 parser/master/slave/device/skill 平行主字段。
- UI 所有槽位执行 assign / replace / unassign。
- 先保留旧字段作为只读 Adapter 输出，不让它们继续成为主数据源。
- Assignment canonicalization、唯一实例、exact Definition version、槽位兼容和 required slot 验证。

### Phase B — Component Rule Contribution Contract
- 在 Jar Core 建立可版本化、声明式 Rule Contribution 契约。
- 先把现有 WorkshopCapability、四种 Jar 算子、MATCH、ModifierStack 映射到新契约，行为保持一致。
- 将贡献来源标记为 Core Built-in / World / Installed Component / Temporary Effect。
- 处理贡献冲突、优先级、作用域、opaque 谓词与 Undetermined。
- 不先做任意 C# 插件/闭包系统；先做可序列化、可指纹化的声明式表达。

### Phase C — Effective Rule Profile 装配
- 从 Board Assignment + Owned Instance + exact catalogs 解析所有贡献。
- 装配不可变 Effective Rule Profile。
- Space 只提供冻结的安装实例身份和成长事实；Jar Core 负责解析/验证其规则贡献语义。
- Profile revision 变化必须明确影响 solve book、缓存和正在执行的 Lease/Session。

### Phase D — Solver 消费统一规则接口
- 规则候选生成不再硬编码某一批零件类型；从 Effective Rule Profile 查询候选算子/约束。
- 保留 A/B 订单策略，但把它们限定为资源分区/阶段依赖策略，不让其复制规则语义。
- 统一状态节点、规则评估、纯状态转换、资源来源追踪和证明。
- 支持完整解集/惰性枚举；预算耗尽、opaque、搜索不完整返回 Undetermined。
- 旧求解器继续作为差分 oracle；对拍通过后分阶段替换，不一次性推倒重写。

### Phase E — 资源 Readiness / Admission / Execution 对齐
- 结构解与当前 Jar/液体绑定分开。
- 资源生产/消耗只更新 Readiness；除非规则声明的依赖发生变化。
- Admission 原子检查当前 Rule Profile revision、世界版本与资源余额。
- 执行中规则板变化必须由明确中断/冻结策略处理，不得静默热替换执行语义。

### Phase F — 通用槽位替换与岗位投影
- Core 保留 Workshop 实例与 Master/Slave 岗位的资源/生命周期职责。
- Board Assignment 决定哪个实例被投影到哪个工作台角色槽。
- 主台/从台不是工作台 Definition 的固有属性；交换位置不改 Owned Instance 身份/成长。
- 先定义 Board Slot -> Core Runtime Projection Adapter，再逐步移除旧专用字段。
- 多 Parser/多工作台运行时必须有输入分配、调度、资源所有权和失败恢复模型；不能只放宽槽位数量。

### Phase G — 清理重复实现
- 审计 Workshop、WorkshopRulebook、Solver candidate builder、Snapshot projection 是否仍重复解释同一规则。
- 先通过差分测试证明一致，再删除重复逻辑。
- 保留 Manifest、Lease、Checkpoint、Journal 等真实所有权和恢复边界。
- 清理前查找 scene/resource/test/code 引用；记录删除原因，不盲删。

## 8. 必须新增的验收场景

1. 同一 Board Slot 安装不同 Definition，Effective Rule Profile 与可求解候选发生预期变化。
2. 同一 Definition 的两个 Owned Instance 分别安装两个槽位，实例成长属性互不污染。
3. 替换一个槽位的实例，只改变该实例来源的规则贡献；其他槽位贡献保持不变。
4. 卸下零件后，相关规则贡献消失；旧 Profile fingerprint 不可继续作为新装配的有效 Profile。
5. 临时规则到期/被移除后，候选和 Readiness 按声明的依赖失效。
6. 订单目标不指定主从分配/物理 Jar/操作序列；Solver 根据规则板推导这些结果。
7. 同一订单在两个不同 Rule Profile 下产生不同合法解集或明确不同的可行性结论。
8. 仅改变自机液库余额，不改变结构解集，只改变 Readiness。
9. 改变工作台能力/槽位/规则 Profile，重建受影响的结构解。
10. opaque Rule Contribution 返回 Undetermined，不能被忽略或误报 Infeasible。
11. 资源不足与结构无解可区分；只有完整穷尽搜索才可报告 Infeasible。
12. B 类订单允许液体需求；从台液体只能来自主台单向交接。
13. 主台 Ban 液体即使在 Jar 内，也不能作为主台向从台交接的绕过路径。
14. 满罐改形的 Jar 内液体自耗不触发工作台暂存区 Ban。
15. 订单产生游离液体先进入当前工作台暂存区，拒收/超限按规则丢弃。
16. Rule Profile 在执行期间变化时，旧 Lease/Session 按明确策略冻结或中断，不发生语义漂移。

## 9. 不允许的架构退化

- 不把零件安装等同于把某个固定设备类型写入专用字段。
- 不把 Board Definition 与玩家 Assignment 混在一起。
- 不把规则写死在订单模板中。
- 不把订单目标改造成预先指定的生产流程。
- 不把工作台类型/岗位当作规则本身。
- 不允许每种新零件都要求 Solver 新增一套 if/switch 才能提供新规则语义。
- 不把任意委托当作可持久化规则。
- 不把资源余额变化等同于规则板变化。
- 不让 Space 重复实现 Jar Core 的规则、订单可行性、冷却或 Passive Scheduler。
- 不把单 Parser 的 Runtime Projection 误称为 Board 只支持一个 Parser；反过来，也不允许仅放宽 Board 结构就宣称 Runtime 支持多 Parser。
- 不以 Probe 源码存在代替 Probe 已运行。

## 10. 当前审查结论与下一步

本次审查没有判定现有 Jar Core 应推倒重写。正确的路线是保留已经建立的规则评估、资源分账、符号求解、物理绑定、Admission/Lease/Execution/Settlement 边界，并把“可替换零件贡献 -> Effective Rule Profile -> Solver”这条缺失的主链补齐。

**下一步实施优先级：先完成 Space 统一 Board Assignment，再建立 Core Rule Contribution Contract 和 Effective Rule Profile；然后让现有候选生成器从 Profile 取规则，逐步替换固定候选来源。** 在这条主链完成前，不能将系统描述为已经是可组合电路板规则世界。


## 11. 第一轮实现进展（2026-11-24）

### 已落地的 Core 主链切片
- 新增 `RuleContribution` / `RuleContributionSource` / `RuleContributionParameter` 声明式契约，保留 RuleId/version、Effect、Target、Scope、Priority、来源 Definition/Source Instance/Revision/Slot 和规范化参数。
- 新增不可变 `EffectiveRuleProfile`：按 Priority、Slot、RuleId、SourceInstance 确定性排序；拒绝重复贡献身份；对 Board 身份、Assignment revision、贡献来源和参数计算 SHA-256 fingerprint。
- 新增 `WorkshopRuleContributionAdapter`，将现有 WorkshopCapability 的 BaseMark / AdditionalMark / Ban 与活跃 ModifierStack 声明式投影成 Rule Contributions。该适配器是迁移桥，不是最终任意零件 Definition catalog。
- `RuleContext.From(EffectiveRuleProfile, scope)` 已能从 Profile 重建对应作用域的基础能力与 Suppress / Grant / Require / Retime / Intensity 修饰器。无法重建的 Require 仍然是 Undetermined；未支持的容量贡献保守返回无法判定。
- `WorkshopSolveSnapshot` 现在冻结 EffectiveRuleProfile；`CreateRuleContext()` 通过 Profile 构造规则上下文；现有 `WorldOrderRuleCandidateCatalog` 经 `WorkshopSolveSnapshot.CreateRuleContext()` 间接消费 EffectiveRuleProfile，而不是再从快照字段单独重建规则。WorldSnapshot 仍保留 RuleSetVersion / WorkshopProfileVersion / ResourceVersion 三段指纹分离：能力参数变化继续落入 WorkshopProfileVersion，临时规则变化落入 RuleSetVersion，资源余额变化只落入 ResourceVersion；Profile fingerprint 是装配产物身份，不得破坏这三类变化的既有缓存边界。
- 静态求解 Profile 排除仅影响执行/生产的 Retime / Intensity；有效窗口在捕获时过滤，Profile 保存窗口来源元数据，冻结 RuleContext 不会在本地重新启动窗口时钟。

### 已落地的 Space 装配主链切片
- 新增 `SpaceHangarBoardAssignment`，Assignment entry 固定保存 slotId、componentKind、ownedInstanceId、exact definitionId/version。
- 新增验证：槽位存在、元件类别匹配、Owned Instance 必须在玩家库存、Definition/version 精确匹配、同一实例不能重复安装、必需槽必须填充。
- Assignment 快照包含 exact Board Definition 和规范化排序的 Assignment 数组，并计算独立 SHA-256 assignment fingerprint。
- `SpaceHangarSelectionDraft` 现在将 Assignment Map 作为可安装组件的主数据源。原 parser/master/slave/device/skill 字段由 Assignment Map 重新生成，作为当前 Core runtime adapter 的兼容投影，不再由它们分别保存选择状态。
- 现有 Parser、主/从工作台、生产设备、技能 picker 的选择事件已改为写入 Assignment Map；新增通用 `assign_instance` / `unassign_slot` / `get_assignment` API，并由 Controller 暴露。
- Owned Loadout Assembler 冻结 Assignment snapshot，重新对照玩家 Owned Instance、exact Definition/version、角色槽和当前旧 runtime projection；Loadout fingerprint 因而包含安装身份。
- `SpaceBattleLoadoutSnapshot` 验证 Board Assignment schema、required slots、slot-kind、重复身份、fingerprint，以及它与 `assemblyBoard` 的 Board Definition 版本一致性。
- Board Definition 本身仍只定义槽位；玩家安装关系仍单独保存在 Assignment Map。

### 当前仍未完成 / 不得误报
- Space UI 仍保留专用 Parser/Master/Slave 控件布局；虽然这些事件已经写入统一 Assignment Map，但尚未完成完全由 Board Slot 数据动态生成的统一槽位面板与通用替换交互。
- Space -> Core 仍未传输每个真实 Owned Instance 的精确 RuleContributionDefinition、Definition/version、progression revision 和 slot provenance。当前 Core 的 `WorkshopSolveSnapshot` 使用 `legacy.runtime` / `legacy-workshop-runtime` 合成来源身份，目的是先让既有工作台规则经统一 Profile 路径执行；不能将其误认为跨仓库 Owned Assignment 已贯通。
- 当前 Core Profile Adapter 只把现有 WorkshopCapability / ModifierStack 迁移到贡献契约；尚未把所有类型的安装元件（Parser、Skill/Order、Production Device、World component）声明的规则合并成完整 Board Profile。
- `WorldOrderRuleCandidateCatalog` 仍枚举固定四种基础 Jar 操作与 MATCH；这轮只是让它消费统一 Profile-backed RuleContext，尚未移除固定候选生成器。
- A/B 订单岗位/资源阶段策略保留；它们仍是资源流和阶段约束，不应被误解为最终通用算子搜索。
- 多 Parser、通用 Slot UI、Space-to-Core exact provenance、完全数据驱动的任意 Rule Contribution 候选枚举尚未完成。

### 本轮验证状态
- 新增 Core 测试：Profile canonical fingerprint、同 Definition 不同 Owned Instance、重复贡献身份拒绝、成长 revision 影响 fingerprint、Rule Scope 隔离、WorkshopCapability/ModifierStack 适配、opaque Require 保持 Undetermined。
- 新增 Space authored Probe：Assignment exact instance、同 Definition 两份独立实例、重复 Owned Instance 拒绝、component kind 不匹配拒绝、Assignment 顺序规范化。
- 测试与 Probe 代码已提交，但本轮没有执行 `dotnet test`、Godot 4.7.1 .NET、GDScript parse 或 runtime Probe。不能宣称运行通过。


## 12. Space 侧精确贡献快照已开始产出（2026-11-24）

- Space Demo 新增 `SpaceRuleContributionDefinition`，允许 Workshop / Parser / Production Device / Skill Definition 声明稳定 RuleId/version、Effect、Scope、Target、Priority、参数与成长属性绑定。
- Space 的 `SpaceRuleContributionResolver` 会从 Board Assignment 精确解析 Owned Instance 与 Definition，输出携带 Definition/version、Owned Instance ID、progression revision、slot ID 的贡献记录；Workshop 原有 base/additional marks 与 Ban 也转成同一数据形态。
- Owned Loadout Snapshot 现在冻结 `boardRuleProfile`，并验证其 fingerprint、Board/Assignment fingerprint、来源槽位和 Owned Instance/Definition 身份一致。
- **该 Profile 尚未进入 Jar Core WorldV2Bridge 的正式输入协议。** Core 当前仍通过 `WorkshopRuleContributionAdapter` 从旧 WorkshopCapability/ModifierStack 生成兼容 Profile。不能将 Space 侧产出误称为跨仓库消费已完成。

### 下一项 Core 接入任务
1. 扩展 WorldV2Bridge 的 versioned input DTO，显式接收 Board Definition identity、Assignment fingerprint 和 `boardRuleProfile.contributions`。
2. Core 验证每条贡献来源是否与冻结 Owned Assignment / Definition / progression revision 精确匹配；未知 Effect、Scope 或 RuleVersion 必须拒绝或 Undetermined，不能忽略。
3. 按 scope 构造 EffectiveRuleProfile；Master/Slave 的角色由 Board Slot 投影，两个工作台的贡献分别进入对应 workshop scope。
4. 让 WorldSnapshot 捕获真实 EffectiveRuleProfile；兼容旧输入仅通过显式 legacy adapter，不允许静默混合新旧两种来源。
5. 在旧/新 Profile 上做差分测试，确保基础 Jar 操作、Ban、临时 Modifier、A/B 单向资源语义一致后，再迁移候选枚举器。


## 13. World V2 Board Profile 跨仓库 ingress（2026-11-24）

### 已实现的 Core 输入与执行链

- 新增 WorldV2BoardRuleProfileOptions 版本化输入 DTO。WorldV2Options 显式接收 boardRuleProfile 与 boardAssignmentSnapshot 两个独立字段；Assignment Snapshot 不嵌入 Profile hash payload，避免改变 Space 已冻结的 Profile fingerprint。
- Core 按 Space 的紧凑 UTF-8 JSON 口径重算 Board Assignment SHA-256，并按规范化键序重算 Rule Profile SHA-256。拒绝缺字段、重复 slot/instance、未填 required slot、槽位类别不兼容、Board/Definition 版本不匹配和 fingerprint 不匹配。
- Assignment entry 现在携带 sourceRevision。Core 对每条 Rule Contribution 校验 slot、Owned Instance ID、Definition ID/version、progression revision 均与该 Assignment entry 完全一致。
- Space inventory choice 现在暴露 progressionRevision；Assignment snapshot 将其冻结；Space Loadout Snapshot 再对照 ownedInstanceSnapshots 验证 Assignment 与 Contribution revision 相同。
- WorldV2WorkshopOptions.boardSlotId 将当前 Master/Slave runtime projection 绑定到精确 Board Slot。Core 依据 Slot 的 roleId 验证 Master/Slave，不再从工作台 Definition 推断角色。
- WorldV2Factory 在新协议存在时构造 Board EffectiveRuleProfile，并通过 RuleContext.From(profile, workshop, boardSlotId, mutableStack: true) 为每个工作台创建独立规则上下文。Master 与 Slave 不会互相读取对方工作台槽位贡献。
- WorkshopSolveSnapshot 捕获真实 Board Profile 来源；同时把安装后新增的运行时临时 Modifier 合并为独立 temporary-modifier 贡献，避免快照退回 synthetic legacy.runtime 来源或丢失临时规则。
- Solver 的 CreateRuleContext() 与工作台执行规则都从同一 Profile 解释；Board-owned Workshop 禁止通过旧 ReplaceCapability 或 AttachRuleStack 脱离 Profile。
- Profile fingerprint 归入 WorkshopProfileVersion，不进入 RuleSetVersion；保留 RuleSet / WorkshopProfile / Resource 三类版本的原有语义边界。

### 本阶段明确的能力边界

- 当前 ingress 仅接受 scope = workshop 且来源为 workshop Board Slot 的 Rule Contribution。Parser、Skill、Production Device 等其他 scope 在 Core 尚无执行消费者，因此当前明确拒绝，不会静默忽略或误塞进主/从工作台。
- 当前支持 RuleVersion 1。未知 Effect / Scope / RuleVersion 拒绝；RequireCondition 和尚无执行器的 ModifyCapacity 保守保持 Undetermined。
- 当前 runtime 仍只投影一个 Master、一个 Slave 和一个 Parser。Board 可描述更多槽位不代表多 Parser 输入分配/调度/产出归属已经实现。
- Space root frozen snapshot 与 jarWorldOptions 中的 Profile / Assignment 两份数据必须完全一致；Core 不接收旧字段与新 Profile 混合解释规则。

### 验收状态

- 新增 Core 工厂测试覆盖 Master/Slave Slot 隔离、Ban 隔离、真实来源保留以及 Solver RuleContext 与 Workshop runtime 的一致性。
- 新增/更新的 Space Assignment 验证覆盖 progression revision；SpaceBattleLoadoutSnapshot 对照 owned snapshots、Assignment 与 Contribution 的 revision。
- 本文档提交时 GitHub Actions 验证仍在运行；未将 CI 标记为通过。Space GDScript Probe 仍未运行。


### 13.1 CI 结果更正（2026-11-24）

- GitHub Actions run 45224300866 对应提交 52c2537，Core 主项目与 Godot Host build 阶段通过；测试项目编译因新增工厂测试中两处可空 Rules 访问未处理而失败，测试本身未执行。
- 已在后续提交 4808ea4 将两处访问改为显式 null-forgiving；该修复提交尚无新的 GitHub Actions run，因此不能宣称 CI 或 xUnit 已通过。
- 之前的 RuleSetVersion/ProfileVersion 分界错误也已修正：Board Profile 原始 fingerprint 只进入 WorkshopProfileVersion；临时 Modifier 仍由 RuleSetVersion 跟踪。
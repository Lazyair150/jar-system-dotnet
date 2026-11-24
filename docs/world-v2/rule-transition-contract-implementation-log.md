# World V2 规则转换契约实施记录

> 记录日期：2026-11-24  
> 适用分支：`world-v2/phase-4.11c-order-template-demand-ingress`  
> 记录基线：MATCH Adapter 与暂存区 Ban 语义修正（后续提交以 PR head 为准）  
> PR：[#60](https://github.com/Lazyair150/jar-system-dotnet/pull/60)（Draft / Open；本记录不授权合并）

## 1. 本次状态核对

- PR #60 当前仍为 Draft、Open、未合并，base 为 `world-v2/phase-4.11b-space-produced-jar-import-v2`。
- 本次核对的 PR head 为 `e6f68e0ea21bce0db993a60358e8c8f030c47834`。
- 对应 GitHub Actions run `45360457315` 的 `verify` job 已完成且结论为 **success**。
- 成功步骤覆盖项目既有的 build、测试基线、契约快照、schema 与配置世界等价验证。它证明该提交通过仓库当前 CI，不代表尚未接入的 Godot Probe 或未实现的规则语义已通过验证。

## 2. 已落地的共享契约

代码位置：`src/JarSystem.Core/Domain/RuleTransitionContract.cs`。

### 2.1 判定结果

`RuleEvaluationResult` 使用三态：

- `Allowed`：必须携带一个可执行的 `RuleTransition`；
- `Rejected`：必须没有 Transition；
- `Undetermined`：必须没有 Transition。

每个结果都要求稳定、非空的 `ReasonCode`。因此“暂时无法证明可执行”不能被降级成允许，也不能被误报成确定无解。

### 2.2 转换与状态快照

`RuleTransition` 固定记录 RuleId、RuleVersion、Scope、Target、Before、After 和 ResourceEffects。

- Before / After 使用规范化、只读的键值快照，不把可变领域对象直接塞进转换记录。
- ResourceEffects 在构造时复制并包装为只读集合，避免调用方之后修改原列表而篡改既有转换。
- 契约对象不直接修改任何库存或 Jar；它只描述转换，权威事务负责校验并应用。

### 2.3 资源效果与账户身份

`RuleResourceEffect` 支持 Consume、Produce、Transfer、Discard；资源区分 Jar 与 Liquid。每个账户由 `AccountKind + OwnerId` 定位，OwnerId 必须是精确运行时实例身份，不得使用“主台”“从台”等可变角色名称代替实例身份。

端点约束：

| Effect | From | To |
|---|---|---|
| Consume | 必须 | 必须为空 |
| Produce | 必须为空 | 必须 |
| Transfer | 必须 | 必须，且不能与 From 相同 |
| Discard | 必须 | 必须为空 |

该结构验证端点形态，但还不是完整的账户授权系统。账户是否允许跨所有者转移、是否受 Ban/容量约束、拒收后是否丢弃，仍需由具体规则评估器和事务边界证明。

## 3. 当前 Workshop 适配范围

`WorkshopRuleTransitionAdapter` 将既有 `WorkshopRulebook` 的允许/拒绝证明适配为共享契约，目前覆盖四种化学操作；独立的 `MatchRuleTransitionAdapter` 适配 MATCH 生命周期：

| 操作 | 资源效果 | 必须保持的账户语义 |
|---|---|---|
| PourIn | Consume Liquid | 只消耗当前 Owned Workshop 的 `workshop.free-liquid-staging` |
| PourOut | Produce Liquid | 先指向当前 Owned Workshop 的 `workshop.free-liquid-staging`；接收/溢出结算尚未在本适配器中完成 |
| ReshapeEmpty | Consume Liquid（如旧证明有消耗） | 消耗当前工作台暂存区液体 |
| ReshapeFull | Consume Liquid | 消耗目标 Jar 的 `jar.internal-liquid`，不能伪装成工作台暂存区液体 |

Adapter 从旧 `WorkshopOperationEvaluation` 读取判定和转换，不重新实现化学规则。被旧规则拒绝或判为不确定的操作不会生成可执行 Transition。

当前测试文件：`test/JarSystem.Tests/RuleTransitionContractTests.cs`。覆盖 PourIn 暂存账户、PourOut 当前工作台归属、ReshapeFull Jar 内液体账户、MATCH 精确 Jar 标记、MATCH 拒绝/错误 proof 拒绝、拒绝结果无 Transition、资源效果端点校验。另有 `WorkshopRulebookTests` 与 `WorkshopReshapeTests` 固定暂存区 Ban 不影响 Jar 内液体自耗的语义。

## 4. 明确未完成的语义

以下事项不得因为共享 DTO 已存在而被视为已实现：

1. **MATCH Adapter 已落地**：MATCH 不是 `WorkshopOperationKind` 的普通化学操作，现由 `MatchRuleTransitionAdapter` 单独消费 `EvaluateMatchTransition` 的权威 proof。它只将同一 Jar 的 marked 从 false 变为 true，不产生 Jar/Liquid ResourceEffect。工作台持有权校验仍由 `Workshop.MarkJar` 门面负责，Adapter 要求调用方提供 exact workshop/Jar identity。
2. **Mark 生命周期已厘清一部分**：`Workshop.MarkJar` 先校验 Jar 属于当前 Workshop，再消费 MATCH proof；`ClearMark` 只清指定且归属本台的 Jar，`ClearMarks` 清全台标记，代码注释限定其用于订单生命周期边界，不得在同一订单主从交接时调用。Mark 是 Jar 生命周期状态，不是液体/Jar 数量 ResourceEffect。已审查主要调用点：旧 `OrderEvent.Start` 在订单开始清理旧标记，`Deliver` / `Fail` 在终态清理；World V2 会话只清理本订单锁定参与 Jar。旧 `OrderExecutionSession` 曾在 Master→Slave 阶段调用 `_slave.ClearMarks()`，本轮已移除，防止同一订单交接时清掉 Mark、使 Jar 再次可操作。仍需在后续执行器统一阶段把旧会话与 World V2 的周期语义进一步收敛。
3. **Staging admission**：PourOut 产出到暂存区只是候选资源效果。必须继续表达容量、Ban/接收规则、接收数量和溢出/拒收丢弃；不得把 Produce 到 staging 误认为已成功入账。
4. **Transfer**：必须显式表达来源与目标账户、资源所有权、方向和事务结果。主台 Ban 液体不得通过 Transfer 传给从台；从台使用期间只接受主台提供的液体。
5. **Ban / Capacity**：必须区分规则拒绝、接收拒绝和超限丢弃；不得将 Jar 内液体自耗送入 Workshop staging Ban 检查。
6. **Parser / Skill / Production Device**：分别由其领域消费者解释。消费者路由或 Rule Contribution 数据存在，不等于对应规则已被执行。
7. **执行入口统一**：现有 WorkshopRulebook 仍是权威规则 Oracle。共享契约尚未成为所有求解、准入和执行路径的唯一 evaluator。

## 5. 下一阶段的实施顺序

按以下顺序推进，避免提前建设求解器或编译器：

1. 阅读并记录 MATCH lifecycle proof 的真实输入、输出、状态副作用与失败原因；实现独立 Adapter 和测试。
2. 审查 Mark 的权威实现，补充状态快照字段/生命周期转换，并测试标记不会被错误重置或重复消费。
3. 将 staging admission 独立建模：请求量、可接收量、拒收量、容量前后状态和明确的丢弃效果；测试全收、部分接收、全拒收与超限。
4. 将主从 Transfer 建模为账户到账户的显式转换；测试精确 Owned Instance 归属、方向、Ban 阻断和失败原子性。
5. 对每个 Adapter 保留旧规则实现作为差分 Oracle，逐项比较 Decision、ReasonCode、Before/After 和 ResourceEffects。
6. 只有这些语义可被共享契约完整表达并由现有执行事务正确应用后，才开始把 evaluator 接入求解器与执行器。
7. 更新架构模型与本记录，随后再进入 Circuit Compiler；不提前宣称编译器或通用前驱求解已完成。

当前额外的语义修正已递增 RuleContractVersion / 对应 RuleVersion；所有旧快照、缓存和契约 golden data 必须按版本隔离，不能把旧 Ban 语义的产物当成新语义复用。

## 6. 评审与验收清单

- [ ] Allowed 必有 Transition；Rejected / Undetermined 没有 Transition。
- [ ] 所有 Transition 具有稳定 RuleId/version/scope/target 和规范化 Before/After。
- [ ] 所有资源效果均有明确资源身份、正数量和合法账户端点。
- [ ] 每个账户都绑定精确 Owned Instance 或明确的全局账户身份。
- [ ] Jar 内液体与工作台游离液体使用不同账户。
- [ ] Staging 的接收、拒收、容量和丢弃结果可审计。
- [ ] 主从资源流方向与 Ban 规则无法被绕过。
- [ ] MATCH / Mark 生命周期由权威证明适配，而非猜测。
- [ ] 新旧规则结果差分一致；执行失败不会部分提交。
- [ ] CI 结果绑定到确切提交 SHA；未执行的 Godot Probe 单独标注。

## 7. 设计边界

共享 Transition Contract 是规则评估结果与资源变化的共同表达格式，不是：

- Circuit Compiler；
- 通用规则解释器；
- 搜索算法或完整解集；
- 资源账户数据库；
- 自动事务提交器；
- 所有子系统规则的统一执行器。

它的价值是先让不同领域规则以同一种、可验证、可审计的方式描述状态变化，为后续统一评估与执行建立可靠边界。


## 8. 本轮修正：电流语义与暂存区 Ban（2026-11-24）

### 8.1 电流的领域定义

电路中的“电流”就是 Jar 与液体资源本身，而不是抽象的数值信号：

- Jar 是离散、带状态的流动资源，至少携带 Jar identity、shape、internal liquid、marked lifecycle state。
- 液体是可计量资源，必须区分 Jar 内部液体与工作台游离液体暂存账户。
- Rule Transition 是资源经过电路元件时发生的状态转换；ResourceEffect 是资源账户之间的消耗、产出、转移或丢弃。
- Board/组件定义通路结构和局部规则，Solver 根据订单目标寻找资源流路径；它们不替代资源本身。

### 8.2 Ban 语义纠正

旧实现曾通过 `LiquidRules.CheckJarOperation` 将工作台 Ban 扩展到 Jar 内部液体，导致满罐改形自耗、MATCH 可能因为 Jar 内液体种类而被 Ban 拒绝。这与当前领域契约“Ban 约束工作台游离液体暂存账户”不一致。

本轮修正：

- 删除 Jar 操作的 Ban 检查入口；Ban 仍用于游离液体的取用和接收。
- 满罐改形自耗只消耗 `jar.internal-liquid:<jarInstanceId>`，不读取工作台 staging。
- MATCH 只验证规则条件与 Jar 生命周期状态，不把 Jar 内液体伪装成 staging 液体；它的 Transition 快照同时保存 exact workshopInstanceId 与 jarInstanceId，并校验权威目录中的 RuleId / RuleVersion。化学 Adapter 同样校验请求操作与 proof 的 RuleId / RuleVersion 一致，避免把某个操作的 proof 错贴到另一个操作上。
- PourIn / ReshapeEmpty 仍需从当前工作台游离液体账户取用，受 Ban 和可用余额约束。
- PourOut 进入 staging 的接收仍受 Ban/容量限制；拒收与丢弃的精确统一表达仍待 Staging Admission 阶段。

该语义变化递增 `WorkshopRulebook.RuleContractVersion` 至 3，`RESHAPE_FULL` RuleVersion 至 2，`MATCH` RuleVersion 至 3。此版本变更必须随规则快照/指纹传播，不可与旧语义缓存混用。

### 8.3 MATCH / Mark 证明边界

`MatchRuleTransitionAdapter` 不伪造 `WorkshopOperation`。它消费 `WorkshopRulebook.EvaluateMatchTransition` 的 proof，并要求：

- RuleId 精确为 `jar.workshop.match`；
- Before 是未标记 Jar；
- After 保持 shape 与 internal liquid 不变，只把 marked 置为 true；
- Transition.Target 是 exact Jar instance ID；
- ResourceEffects 为空，因为 MATCH 不搬运、不生产、不消耗 Jar 或液体。

工作台是否实际拥有该 Jar 仍由 `Workshop.MarkJar` 校验；Adapter 本身不是所有权数据库。

### 8.4 本轮测试状态

已新增 MATCH Adapter 正常、拒绝和错误 proof 测试；已调整 `LiquidRulesTests`、`WorkshopRulebookTests` 与 `WorkshopReshapeTests`，固定“被 Ban 的 staging 液体不可取用/接收，但 Jar 内液体自耗与 MATCH 不读取 staging Ban”的边界，并增加 PourOut 普通/订单暂存两条路径在目标 staging Ban 下拒绝的测试。CI 曾发现遗漏的旧测试调用与 PourOut 检查点，已根据失败日志修正；以最新 head 的最终 CI 为准。


## 9. Mark 周期边界审查补充

审查到的主要调用点：

| 入口 | 行为 | 周期解释 |
|---|---|---|
| `OrderEvent.Start` | 清理 Master / Slave 旧 Mark | 新订单开始边界 |
| `OrderEvent.Deliver` | 交付后清理两台 Mark | 旧订单成功终态 |
| `OrderEvent.Fail` | 失败收尾时清理两台 Mark | 旧订单失败终态 |
| `WorldOrderExecutionSession.CompleteSuccess` | 交付前清理交付 Jar；其余锁定参与 Jar 在结算收尾清理 | 当前订单成功终态 |
| `WorldOrderExecutionSession.CleanupTemporaryResources` | 只清理本订单 Manifest 锁定的参与 Jar | 当前订单成功/失败收尾 |
| 旧 `OrderExecutionSession` Master→Slave | 原先会调用 `_slave.ClearMarks()` | **本轮已删除**：同一订单阶段交接不得重置 Mark |

Mark 不是资源数量，也不是液体账户余额。它是 Jar 的订单周期生命周期状态：MATCH/化学操作会置位；订单终态清理后才允许下一个订单周期重新使用。主从交接只改变资源所在工作台，不开启新订单周期，因此必须保留 Mark。

后续测试需要把“同一 Jar 在 Master 已操作后转入 Slave，仍保持 marked”作为跨台不变量，并覆盖成功、失败、取消三种终态清理。


## 12. Jar 三态极性：派生领域状态（2026-11-24）

### 12.1 状态映射

当前 Jar 的内部液体词表只有两种液体，空罐加两种满罐形成三个离散状态：

| 权威内部液体 | 派生极性 |
|---|---|
| `null`（空罐） | `Neutral`（中性） |
| `酒` | `Cathode`（阴极） |
| `蜜` | `Anode`（阳极） |

实现位于 `src/JarSystem.Core/Domain/JarPolarity.cs`：

- `JarPolarity` 是三态枚举；
- `JarPolarityResolver` 是唯一映射入口；
- 未知非空液体不会被猜测映射，`TryFromLiquid` 返回 false，`Resolve` 明确失败；
- `Jar.Polarity` 与 `JarFacts.Polarity` 都是从当前权威 `Liquid` 派生的只读属性，不保存第二份可变状态。

因此不存在可独立写入的 Polarity 字段，也不会出现“Cathode + 蜜液”这类互相矛盾的数据。未来若改变液体词汇或极性映射，必须显式修改唯一 Resolver 与对应测试。

### 12.2 对共享规则转换的接入

Workshop 化学操作与 MATCH 的 Before / After 快照现在都包含规范化的 `polarity` 字段。求解/转换模型可以同时读取：
- `liquid`：实际液体身份；
- `polarity`：三态分类；
- ResourceEffects：资源数量与账户流动。

极性不替代液体身份、数量或资源账户，也不改变现有操作语义。特别是：
- Cathode / Anode 不是游离液体账户；
- 极性不能表示液体数量；
- Jar 内液体自耗仍消耗 `jar.internal-liquid:<jarId>`；
- PourOut 仍需经过目标工作台 staging 的 Ban / 容量准入；
- MATCH 只改变 Mark，不改变极性；
- 本抽象不是电化学模拟，不引入电压、电流强度、电阻或真实阴阳极反应。

### 12.3 已添加的测试与验证边界

`RuleTransitionContractTests` 新增：
- null / 酒 / 蜜 到 Neutral / Cathode / Anode 的映射；
- Jar 实例的只读派生 Polarity；
- 未知液体 fail-closed；
- 化学操作与 MATCH Transition 快照包含派生极性。

当前最新已知 CI（提交 `a1cf2bbf07b1d3c23ac92145bf49cc3f2e3cb43f`，run `45364836852`）失败：xUnit 有一个 `AJarCarryingABannedLiquidCanBeReshapedBySelfConsumption` 用例失败，Solver differential 阶段报告两处 mismatch。该 CI 早于本节代码，不能用它判断本次新增代码是否通过；本次新增提交尚待新 CI 验证。未运行的本地/Godot Probe 不记作通过。


## 13. 三态极性接入求解器与转换边（2026-11-24）

### 13.1 状态分类已进入求解器事实模型

- `Requirement.Polarity` 从需求液体派生；
- `JarState.Polarity` 从 Jar 内部液体派生；
- `SolverState.IsEmpty`、`StateSpaceSolver.GenOps`、`MatchingSolver.BuildOptions`、`SolverPrecheck` 的空/满分类使用 Neutral 判定；
- 求解器仍保留实际液体 ID 参与匹配、数量核算和账户记账；极性没有替代液体身份。

极性不作为第二份可写状态，也不额外加入状态键：液体 ID 已经是状态键的一部分，且唯一决定当前三态，重复序列化极性会制造无必要的双重事实。

### 13.2 转换契约显式表达极性边

`JarPolarityTransition` 从不可变 Before / After Jar snapshots 派生：
- PourIn：Neutral → Cathode / Anode；
- PourOut：Cathode / Anode → Neutral；
- ReshapeFull：Cathode / Anode → Neutral；
- ReshapeEmpty：Neutral → Neutral（内部状态仍为空，液体成本来自 staging）；
- MATCH：极性保持不变。

转换构造时必须能解析 Before / After 极性，且快照构造器会验证极性与实际液体 ID 一致。极性边不是资源效果；液体数量仍由 ResourceEffects 描述。

### 13.3 Ban 语义回归修复

上一轮 CI 的失败不是三态映射问题，而是旧求解器优化路径仍将 staging Ban 错误套用到 Jar 内液体自耗。已修正：
- `SolverPrecheck.CanServe` / `CanServeFree`：满罐改形自耗不读取 staging Ban；只有 PourOut 进入 staging 时才受 Ban；
- `SolverPrecheck.LowerBound`：满罐改形不再因 staging Ban 被排除；
- `MatchingSolver.BuildOptions` / `Apply(Role.ReshapeFull)`：允许使用被 Ban 的 Jar 内部液体自耗；
- PourIn、空罐改形成本、PourOut 的 staging admission 仍保留 Ban。

### 13.4 验证边界

已添加求解器事实模型的三态映射测试、显式转换边测试，并保留 `AJarCarryingABannedLiquidCanBeReshapedBySelfConsumption` 对三个求解器实现的回归覆盖。新提交 CI 尚未完成时，不把代码编译或测试结果记为通过。


## 14. 本轮推进：差分 Oracle 语义校正与 staging admission（2026-11-24）

### 14.1 两处 Solver Differential 反例的真实原因

此前 CI 的 Solver Differential 反例来自生产求解器与独立参考实现对 Ban 语义的解释不一致。旧参考实现把 Ban 当成只约束 staging 液体的过滤条件；生产侧当时也曾出现过相同误读。正确关系不是“Jar 内液体绕过 Ban”，而是：

- 工作台 Ban 派生其专注极性；任何对 Jar 的实际操作（MATCH、PourOut、满罐改形等）都必须先满足当前工作台与 Jar 的极性兼容；
- 游离液体从 staging 取用或进入 staging 时，还要独立满足该账户的 Ban 与容量；
- 满罐改形的成本从 Jar 内部账户扣除，但操作许可仍由工作台极性决定；
- Jar 在工作台之间转移所有权不执行化学操作，不检查来源或目标工作台极性。

参考实现已按这组独立边界修正，仍保留独立 BFS / 精确状态键，不复用生产求解器的操作生成逻辑。差分测试必须覆盖同极可操作、异极不可操作、Jar 身份跨台可移动，以及被 Ban 的游离液体不可跨台转移。

### 14.2 非 Jar 转换与 Staging Admission

共享 `RuleTransition` 不应强制每种规则都伪造 Jar 极性边。本轮将 `PolarityTransition` 改为可空：

- Before / After 同时包含 exact `jarInstanceId` 时，必须具有液体与派生极性，并继续校验极性边；
- 两个快照都没有 Jar identity 时，表示非 Jar 状态转换，不产生虚构极性；
- 只有一个快照含 Jar identity，或非 Jar 快照单独携带 polarity 字段，均拒绝。

新增 `StagingAdmissionTransitionAdapter`，把权威 `WorkshopLiquidStaging.Receive` 返回的 `LiquidInventoryReceipt` 转成可审计转换：

1. PourOut 的 Jar 操作 Transition 先把 Jar 内液体 Transfer 到精确订单的 `workshop.order-output-boundary`；
2. Staging Admission Transition 再把实际接收量从该边界 Transfer 到当前工作台 `workshop.free-liquid-staging`；
3. Ban 拒收或容量溢出的部分从该边界 Discard；
4. Before / After 同时记录工作台实例、订单、液种、总暂存占用与订单子账余额；
5. Adapter 验证 requested = accepted + discarded，且总占用和订单余额的变化都必须等于 accepted。

该 Adapter 只适配 staging receipt，不直接修改账户。新增 `WorkshopLiquidStaging.ReceiveWithTransition` 作为成对入口：它在同一次同步调用中执行权威 `Receive`，捕获前后余额并返回 `StagingAdmissionSettlement`（原始 receipt + Transition）；若 proof 构造失败，会回滚本次已接收量。订单 `Workshop.PourOutWithSettlement` 已接入该入口：Jar 操作与 staging settlement 分别生成相邻 Transition，`WorldPlanStepApplier` 校验实际接收增量并把两份 proof 交给执行会话；Ban/容量拒绝不会回滚已完成的 Jar 化学操作，而是形成 Discard。旧 `Receive` 仍保留兼容；其他非 PourOut 生产输出入口仍需继续审计，不能宣称全系统所有生产源已统一接线。

### 14.3 Mark 周期回归

扩展 `WorldOrderExecutionSessionTests`：

- 已有 B 类测试在主台操作 Jar 并交接后，断言该 Jar 在从台仍保持 Mark；另一只未被主台操作的 Jar 保持未标记，可继续执行从台步骤；
- B 类成功交付后，断言跨台保留的 Mark 在交付前清理；
- B 类从台执行中途失败并回流后，断言本订单参与 Jar 的 Mark 被清理；
- 原有无关 Jar Mark 不被当前会话清理的测试继续保留。

### 14.4 验证边界

`4f9f98a52d920ba04226eb007ea010850a2087f3` 的 GitHub Actions verify 步骤已成功，覆盖了参考 Oracle 修正与 T4 定向场景。随后追加的 Mark 终态断言及 staging adapter 代码仍在更新的 head 上等待 CI；不能把前一个 SHA 的结果外推到新 head。Godot Probe 未运行。

### 14.5 下一项工作

1. 检查当前 head 的 CI，修复真实编译/测试/差分问题。
2. 审计 PourOut 以外所有订单产液源，逐一接入同一 staging receipt/Transition 边界。
3. 主从 Transfer 已接入 exact OwnedInstanceId 与提交后状态核对；继续覆盖失败补偿、checkpoint 与账本回放的一致性。
4. 为 Transfer 与 Staging 的 ResourceEffects 增加账户守恒检查，再评估是否进入 evaluator 的统一接线。


### 14.6 主从 Transfer 的账户级适配器

新增 `WorkshopRoleTransferTransitionAdapter`，把一次已完成只读预检的主从交接描述为单向资源转换。Jar 身份转移与工作台极性无关；极性只约束工作台对 Jar 执行化学操作：

- Jar 使用 `workshop.jar-inventory:<exact workshop instance>` 账户，按每个 exact Jar ID 产生一条 Transfer；
- 游离液体使用 `workshop.free-liquid-staging:<exact workshop instance>` 账户，按液种和数量产生 Transfer；
- 只允许 source → destination，不生成反向效果；Jar 库存归属移动不检查来源或目标工作台极性；来源 staging Ban 阻断游离液体出站，目标 staging Ban/容量阻断液体入站；
- 预检检查 Jar 是否属于 source、Jar ID 是否重复、destination Jar 容量、source 液体余额、destination Ban 与 destination staging 总容量；
- Before / After 记录来源/目标实例、Jar 数量和目标 staging 占用；不把 Jar 液体/极性混入转移准入，因为这是跨账户所有权移动，不是 Jar 化学状态转换。

`WorldV2Options` 现在从冻结的 `boardAssignmentSnapshot` 按 `BoardSlotId` 把 exact `OwnedInstanceId` 注入 Core `Workshop`；`OrderLiquidScope` 也保留该身份。`OrderTransferService` 在提交前使用两端 exact Owned Instance ID 构造此 Transition，继续执行既有 staging Ban/容量与 Jar 所有权/罐位预检；提交后核对真实来源/目标 Jar 数量和目标 staging 余额增量与 Transition.After 一致，只有核对通过才发布 `CommittedTransition`，`WorldOrderExecutionSession.TransferTransition` 可读取该不可变回执。该回执与实际提交处于同一同步事务边界；当前资源账本仍记录 Jar/Liquid Transfer，Transition 本身尚未进入执行进度快照/持久化回放。


## 15. Ban 与工作台极性统一（2026-11-24，语义纠正）

此前第 8、13、14 节把 Ban 错误收窄为“仅约束游离液体暂存账户”，并由此推出满罐 Jar 自耗与 MATCH 可绕过 Ban。这一结论错误，相关实现、测试、参考求解器和说明均按本节更正；不得继续沿用“staging-only Ban”口径。第 8、13、14 节中与本节冲突的说法均已失效。

### 15.1 正确领域关系

- Jar 极性由内部液体派生：空 = Neutral，酒 = Cathode，蜜 = Anode。
- 工作台的 Ban 配置同时表达其专注极性与液体暂存类别限制。
- Ban 酒意味着工作台专注 Anode：Anode Jar 可操作，Cathode Jar 不可操作；Ban 蜜则相反。
- Neutral Jar 对 Cathode / Anode 工作台均可操作。
- 无 Ban 的旧配置保持双极性兼容；同时 Ban 酒与蜜时只允许 Neutral Jar。
- Jar 内液体是否属于 Jar 自有账户，只决定资源从哪里扣除；它不会授予工作台操作异极 Jar 的能力。
- 因此 MATCH、PourOut、满罐改形都必须先通过工作台-Jar 极性兼容检查。PourIn 与空罐改形还必须满足暂存液体类别/库存准入。PourOut 在极性兼容后完成 Jar 内液体到 output boundary 的转移；随后 staging 按 Ban/容量接收可容纳部分，其余明确 Discard，不因 staging 拒收而回滚 Jar 化学操作。

### 15.2 实现方向

`WorkshopCapability` 派生 `WorkshopPolarityMode`，并提供 `CanOperateJar(JarPolarity)`；`RuleContext` 暴露同一判定。 `WorkshopRulebook.EvaluateMatch` 与化学算子的公共入口统一检查当前 Jar 极性，`ReshapeRules.SelfConsume` 也不得绕过该检查。每个操作 proof 与 RuleTransition 快照都冻结 `WorkshopPolarityMode`，并校验该模式与 Jar 极性兼容。Solver 的预检、MatchingSolver 和独立 ReferenceSolver 使用相同领域关系，但 ReferenceSolver 继续保持独立搜索实现。

规则语义版本递增：`RuleContractVersion=4`、`POUR_OUT=2`、`RESHAPE_FULL=3`、`MATCH=4`。旧版“staging-only Ban”快照、差分用例和缓存不得与新规则混用。

### 15.3 回归验收

- 阳极工作台（Ban 酒）：阳极 Jar 可操作，阴极 Jar 的 MATCH / PourOut / 满罐改形均拒绝；Neutral Jar 允许。
- 阴极工作台（Ban 蜜）：对称验证。
- 同时 Ban 两类液体：仅 Neutral Jar 允许。
- 无 Ban：保留双极性兼容。
- 暂存区取用按 Ban/库存校验；产液进入 staging 时按 Ban/容量部分接收，其余 Discard。Jar 内部液体自耗只改变 Jar 内部账户，但仍受工作台极性操作权限约束。
- 主从交接不能改变 Jar 的极性，也不能清除一次订单操作 Mark；Jar 可以跨不同极性的工作台角色转移。只有后续某个工作台实际对该 Jar 执行 MATCH/化学操作时，才检查该工作台的极性兼容。


## 2026-11-24 — Clarify polarity vs physical Jar handoff; B-class solver limitation

- **Jar transfer is not a workshop operation.** Master→Slave physical Jar movement does not consult source/target workshop polarity, staging Ban, or liquid staging admission. It preserves exact Jar identity, shape, internal liquid, and Mark state; it changes only the owning resource account.
- **Workshop polarity controls operations, not possession.** MATCH, POUR_IN, POUR_OUT, and RESHAPE are checked against the operating Workshop's polarity compatibility. A workshop may hold a Jar it cannot currently operate. This is not an invalid inventory state and must not be rejected at transfer admission.
- **Staging Ban is separate from operation polarity.** Ban determines staging liquid category/admission. Jar polarity is derived from its internal liquid; workshop polarity is derived from the workshop's Ban configuration. Their compatibility governs operation permission. Ban does not directly prohibit transferring a Jar containing that liquid.
- Historical finding, superseded by the role-assignment implementation below: the former B-class enumerator required all demand steps to belong to Slave. This was a solver limitation, not a transfer contract.
- The current enumerator combines both role profiles, keeps a Slave-first compatibility preference, and falls back to Master candidates when needed. A B-class plan may contain no Slave chemical operation when Master completes the demand; Slave still receives/delivers the handed-off Jar.
- Do not weaken polarity operation checks to enable cross-role plans. Conversely, never add polarity checks to Jar handoff.
- Regression coverage keeps these cases distinct: (a) cross-polarity Jar handoff is allowed and preserves identity/state; (b) operation by the receiving Workshop is independently rejected when incompatible; (c) order feasibility is determined by a valid role/operation schedule, not a blanket transfer rejection.


## 2026-11-24：B 类订单需求角色分配修正

此前 `WorldOrderBClassBranchEnumerator` 将 `WorldOrderSymbolicSolutionBuilder.Build` 限定为仅接收 Slave Profile，并进一步要求每个需求步骤都属于 Slave。该限制不是 Jar 转移规则，却会把“需求必须由从台完成”错误固化为求解器的可行性前提。

本轮修正：

- 主台与从台两个 Profile 一起进入符号解集构建；每项订单需求可以选择任一角色的规则候选。为兼容既有 B 类路径，当前枚举采用 Slave-first，只有从台没有候选或从台分支资源绑定不可行时才回退尝试 Master。
- 从台使用事件可以由接收/交付主台已完成 Jar 构成；不得强制要求从台对交接 Jar 再执行一次化学操作。
- 主台需求步骤在 Master→Slave handoff 之前执行；从台需求步骤在 handoff 之后执行。
- 主台完成的需求 Jar 仍进入 B 类订单的 handoff，保留形状、内部液体和 Mark；从台不需要再次操作它。
- Master/Slave 的游离液体准备需求分别统计；主台准备资源可以供主台步骤使用，也可以通过合法的 staging transfer 供从台步骤使用。
- Jar handoff 不经过 `CanOperateJar`，也不检查接收工作台极性。接收后只有实际执行规则时才检查该台操作权限。
- 增加异极主从回归：阴极主台完成酒 Jar 的 MATCH，阳极从台无法 MATCH 该 Jar；B 类订单仍可完成，Jar 仍按 B 类交接边界转移到从台。

这项修正改变的是 B 类求解器的角色分配模型，不是把异极 Jar 强行允许从台操作，也不是放宽任何工作台的化学操作权限。

验证状态：本轮提交后的 GitHub Actions 正在执行；在对应最新 head 的 verify job 完成前，不宣称测试通过。


## 2026-11-24：接收型从台与主台完成 Jar 的交接

进一步审查后明确：Slave-first 只是搜索偏好，不是 B 类可行性的硬门槛。若 Master 能完成全部需求操作，Slave 可以只承担 Jar 接收/交付边界，不必为了制造一条 Slave 化学操作而重复操作已经完成的 Jar。

- 移除 B 类符号解集上“必须至少有一条 Slave 需求操作”的临时过滤。
- 真实求解器 + Admission + ExecutionSession 回归覆盖：Cathode Master 对酒 Jar 执行 MATCH，Anode Slave 没有该 Jar 的 MATCH 候选；Master-only 操作计划仍可将带 Mark 的 Jar 转入 Slave，最终交付清理本订单 Mark。
- 角色 Profile 为 Undetermined 时，只有该角色实际需要执行化学操作才阻断物化；若它只接收/交付资源，不透明规则不构成操作权限判断。
- 明确区分 Slave staging free-liquid rejection 与 Jar-internal liquid：若 Slave 拒绝游离液体，但 Master 能合法使用自己的 staging 完成 POUR_IN，则可选择 Master 操作并仅交接 Jar；若 Master staging 也 Ban 该液体，则不能走这条路径。
- 修正 DirectCandidateProjection：B 类 handoff 必须包含所有订单需求 Jar，而不只是 Slave 操作 Jar；Master 已完成 Jar 以 post-operation revision/Mark 进入 handoff，并建立 Master operation→handoff JarState dependency。
- 新增对应回归：receiver-only handoff、opaque Slave receiver、Master-side POUR_IN fallback、Master staging Ban + Slave reject 的负例、direct projection Master-completed Jar transfer。

验证状态：这些提交后的 exact-head GitHub verify 尚在执行；此前绿色 head 不能替代本轮验证。

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
- `Jar.Polarity` 是从当前权威 `Liquid` 派生的只读属性，不保存第二份可变状态。

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

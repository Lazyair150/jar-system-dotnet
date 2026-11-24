# World V2 规则转换契约实施记录

> 记录日期：2026-11-24  
> 适用分支：`world-v2/phase-4.11c-order-template-demand-ingress`  
> 记录基线：`e6f68e0ea21bce0db993a60358e8c8f030c47834`  
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

`WorkshopRuleTransitionAdapter` 将既有 `WorkshopRulebook` 的允许/拒绝证明适配为共享契约，目前覆盖四种化学操作：

| 操作 | 资源效果 | 必须保持的账户语义 |
|---|---|---|
| PourIn | Consume Liquid | 只消耗当前 Owned Workshop 的 `workshop.free-liquid-staging` |
| PourOut | Produce Liquid | 先指向当前 Owned Workshop 的 `workshop.free-liquid-staging`；接收/溢出结算尚未在本适配器中完成 |
| ReshapeEmpty | Consume Liquid（如旧证明有消耗） | 消耗当前工作台暂存区液体 |
| ReshapeFull | Consume Liquid | 消耗目标 Jar 的 `jar.internal-liquid`，不能伪装成工作台暂存区液体 |

Adapter 从旧 `WorkshopOperationEvaluation` 读取判定和转换，不重新实现化学规则。被旧规则拒绝或判为不确定的操作不会生成可执行 Transition。

当前测试文件：`test/JarSystem.Tests/RuleTransitionContractTests.cs`。覆盖 PourIn 暂存账户、PourOut 当前工作台归属、ReshapeFull Jar 内液体账户、拒绝结果无 Transition、资源效果端点校验。

## 4. 明确未完成的语义

以下事项不得因为共享 DTO 已存在而被视为已实现：

1. **MATCH**：它不是 `WorkshopOperationKind` 的普通化学操作。必须使用独立生命周期适配器，从权威 MATCH proof 构造转换；不得伪造 WorkshopOperation 来复用 Adapter。
2. **Mark 生命周期**：需要先审查现有 Mark 的权威产生、消耗、重置和订单阶段证明，再决定它属于 Jar 状态字段、独立生命周期状态，还是资源效果；不得只根据字段名推断。
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

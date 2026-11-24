# World V2 项目状态与未完成事项

更新时间：2026-11-24  
Core 仓库：`Lazyair150/jar-system-dotnet`  
Core 分支：`world-v2/phase-4.11c-order-template-demand-ingress`  
Core PR：[#60](https://github.com/Lazyair150/jar-system-dotnet/pull/60)（Draft / Open / 未合并）  
记录目的：作为后续连续开发的状态基准；每次阶段结束应更新本文件，不得以旧对话摘要覆盖仓库事实。

## 1. 当前 Git 状态

- PR #60 当前 head：`2b795e382a6b2ab45c25ec07ea8f5c6bd372b6a4`。
- PR #60 base：`world-v2/phase-4.11b-space-produced-jar-import-v2`，base SHA：`a52f9465ad4febdcfb821227f7b2e03b2ddc012c`。
- PR #60 有 402 commits、76 changed files；它是 stacked draft PR，禁止在没有明确指示时合并。
- 历史 Actions run `45396773904`（pull_request）与 `45396758020`（push）均已结束并失败；其日志记录了 3 个 xUnit 用例失败（见下列测试名）。
- 后续 run `45397213790` 在测试执行前因 xUnit analyzer `xUnit2031` 构建失败，具体是 `WorldOrderSymbolicOperationProgramTests.cs` 两处对 `Assert.Single(Where(...))` 的用法。已在 commit `b71aa76d3fa183b852fbe1ff50e257907c982573` 改为 `Assert.Single(collection, predicate)`；该修复尚待最新 CI 验证。
- 先前失败运行中，Build 与两个 Godot host builds、StrategyExperiment、contract snapshot/schema、world config equivalence、protocol guard、solver differential、session replay、dependency security 均通过；失败阶段分别为 xUnit 断言和后续 analyzer build，不应混为一类。
- 失败测试：
  1. `WorldOrderSolverTests.BClassEntryPointRejectsExplicitlyRejectedFreeLiquidBoundary`
  2. `WorldOrderBClassBranchEnumeratorTests.ExplicitSlaveRejectBlocksFreeLiquidBoundaryEvenWhenMasterHasSupply`
  3. `WorldOrderBClassBranchEnumeratorTests.OpaqueSlaveRulesDoNotBlockReceiverOnlyHandoff`
- 该次完整测试统计：797 total / 3 failed。Actions 日志只暴露失败名称与源码行号，未暴露 Assert message；需要在后续可运行环境中复现并读取完整 xUnit/TRX 结果。不得猜测断言原因或简单改期望值。

## 2. 已有领域模型与实现边界

### 2.1 电路与资源模型

Jar World V2 的目标模型是有界资源状态转换网络（Bounded Resource Transition Network）：
1. Board Topology：板、槽位、角色位置和连线/资源账户边界。
2. Component Instance：Definition 与 Owned Instance 分离；实例成长属于 Owned Instance。
3. Rule Algebra：规则贡献、规则作用域、条件、操作、资源效果。
4. Circuit Compiler：把 Board Assignment、规则、实例成长和订单目标编译为可搜索的转换网络。
5. Solver：在预算内搜索/枚举可行路径；预算耗尽必须是 Undetermined，不是 Infeasible。
6. Runtime Interpreter：执行获准转换，提交资源效果和状态快照。

Jar 是离散资源，包含形状、内部液体、标记/生命周期状态；液体分为 Jar 内部液体与工作台游离液体暂存账户。工作台暂存账户是液体资源的唯一回收入口。订单步骤产生的游离液体必须先进入当前工作台 staging；拒收或超容量时丢弃。

### 2.2 工作台极性与转移

- Jar 极性由内部液体派生：空=Neutral，酒=Cathode，蜜=Anode。
- 工作台极性权限：Neutral Jar 可被所有允许 Neutral 的模式操作；Cathode 台可操作 Neutral/Cathode；Anode 台可操作 Neutral/Anode。双 Ban 对应 NeutralOnly。
- Ban/staging admission 与 Jar operation permission 是不同层次，不能互相代替。
- Jar 的主台→从台转移是资源所有权移动，不是工作台化学操作；绝不能因从台无法操作某 Jar 而拒绝接收。
- 从台只有在真正执行某项 Jar 操作时才检查其极性权限。
- 游离液体从主台到从台的转移仍须检查主台来源 staging、从台接收 admission、数量和单向性；不能把主台 Ban 的游离液体传给从台。
- 不允许把“接收 Jar”误写成“接收 Jar 内部液体到 staging”。

### 2.3 B 类订单主从流程

- B 类订单的步骤可分配给 Master 或 Slave。
- Master 步骤必须在 Jar handoff 之前；Slave 步骤必须在 handoff 之后。
- 主台完成 MATCH 的 Jar 仍可以按 B 类 handoff 转移给从台，即使从台不能操作该 Jar。
- 从台只执行其自身 profile 允许的剩余步骤。
- 从台不能使用订单开始前自有 Jar 或游离液体补订单；B 类的初始物理 Jar 来自主台。
- 主台可准备供后续 Slave 步骤使用的液体；Slave preparation 只能满足 Slave 自身步骤。
- 当前 `WorldOrderBClassBranchEnumerator` 将 profile 组合顺序设为 Slave-first，再 Master；注释声明保留既有 Slave-first 偏好。应明确该偏好只是搜索排序，不是领域约束。
- 当前禁用旧 `precomputedSolutions` 的复用，因为旧缓存没有绑定完整 Master+Slave Profile 集合 fingerprint。恢复缓存前必须升级 key，纳入角色、Profile identity/version/fingerprint、需求规格版本、相关 assignment/成长修订。
- 当前 B 类实现仍需完整复核 preparatory schedule、主从步骤排序、液体数量分配、handoff inventory、physical Jar binding 和 concrete assembly 的一致性。

## 3. 已完成的架构工作（仍需集成验证）

### Core
- RuleContribution / EffectiveRuleProfile。
- World V2 Board Rule Profile ingress 与强类型 DTO 校验。
- Board Assignment fingerprint、Owned Instance / Definition / version / progression revision 精确校验。
- RuleTransitionContract：Allowed / Rejected / Undetermined，稳定 reason code、state snapshot、resource effects。
- Workshop Rule Transition Adapter；MATCH 独立 adapter。
- JarPolarity / JarPolarityRuleContract 与极性转换验证。
- B 类 symbolic role assignment 已从 Slave-only 扩展为 Master + Slave profile 共同参与。
- B 类 Master-first-before-handoff / Slave-after-handoff 计划建模已接入。
- 已有 solver differential 4,004 比较样本；旧 CI 中通过。
- 已有随机/容量样本：2,000 random + 3,000 capacity + 32 deterministic；旧 CI 中通过。
- 现有 xUnit 797 项中，本次记录的失败运行有 3 项失败，不能标记为全绿。

### Space Demo
- 仓库：`Lazyair150/space-demo`。
- PR #1 已合并；PR #2 Draft / Open，禁止自动合并。
- 分支：`world-v2/phase-4.10b-hangar-ui`；最近已知 head `12c6e69d...`。
- 已实现 Board Assignment Map、Definition/Owned Instance 分离、fingerprint、冻结 Loadout Snapshot、Rule Contribution 解析、Runtime Profile Set 路由、Parser schema 3 checkpoint / resource conservation / outbox identity。
- 多 Parser 只在 Board 结构层支持；当前 runtime projection 明确拒绝多 Parser，不得宣称可运行。
- Space GDScript/Godot 4.7.1 Probe 尚未在本地执行；Core PR #60 与 Space PR #2 的依赖关系仍需核对。

## 4. 必须遵守的设计不变量

1. 不把 Jar transfer 当作 workshop operation。
2. 不在 Jar transfer 路径检查接收台 polarity / ban。
3. 只有实际 Jar 操作才检查 workshop polarity。
4. staging admission 只处理游离液体进入/离开对应 staging 的边界。
5. Master→Slave 方向不可反转；B 类 Slave 不从自身初始库存供给订单。
6. Master 的步骤在 handoff 前，Slave 的步骤在 handoff 后。
7. 规则不完整或无法解释时返回 Undetermined；不静默降级为允许/拒绝。
8. 预算耗尽返回 Undetermined；不能伪造无解。
9. Definition 与 Owned Instance 不合并；成长数据属于实例。
10. Assignment/Runtime Profile 路由不等于 Rule Compiler 或执行器。
11. 多 Parser 未实现完整 runtime 分配、调度、原石消耗、产出归属前必须明确拒绝。
12. 不虚构成长曲线、生产模型、掉落数值和 balance 参数；缺少数据时保持 unconfigured。
13. 不删除差分 Oracle，直到新 Solver 有足够一致性证据。
14. PR #60 / Space PR #2 均不得擅自合并。

## 5. 下一阶段执行清单（按依赖顺序）

### A. 先修复 B 类求解器失败与语义闭环
- [ ] 获取最新 Actions 运行结果；但不等待 CI 才继续开发。
- [ ] 在 Windows/.NET 10 环境复现 3 个失败测试，取得完整 Assert message / TRX。
- [ ] 修复 explicit Slave reject 的两处回归；确认“拒收游离液体”不会错误拒绝 Jar 内液体或不相关资源，也不能绕过 staging admission。
- [ ] 修复 opaque Slave profile 的 receiver-only handoff：当 Master 有已知可行步骤、Slave 仅作为接收方时，Slave 的 opaque operation rule 不应阻断不需要 Slave 操作的已知 witness；若计划需要 Slave 操作则必须 Undetermined。
- [ ] 验证 B 类 role assignment 的 Cartesian product、重复 requirement identity、物理 Jar 唯一绑定、角色排序。
- [ ] 验证 Master prep 可供 Master 与后续 Slave 需求使用；Slave prep 只能供 Slave 使用。
- [ ] 验证所有 Master 操作均发生于 handoff 前，所有 Slave 操作均发生于 handoff 后。
- [ ] 验证 Master 已完成需求的 Jar 仍按 B 类策略 handoff，不做接收端极性 gate。
- [ ] 增加真实 session / transition replay：Master MATCH → Jar transfer → Slave receiver-only hold；从台不执行异极 Jar 操作。
- [ ] 搜索全部 B 类 solver / assembler / materializer / session / transfer 代码，清除隐式 CanOperateJar/polarity gate（只清除 transfer gate，不清除 operation gate）。

### B. 统一资源转换契约
- [ ] Staging Admission 产生精确 ResourceEffect：accepted / rejected / discarded 数量守恒。
- [ ] Jar Transfer 产生 Transfer ResourceEffect，From/To account 使用精确 Owned Workshop identity。
- [ ] 转移效果明确单向、数量、Jar identity、mark/lifecycle 状态。
- [ ] PourOut 的操作许可、staging admission 和拒收丢弃结算分层。
- [ ] Jar 内部液体自耗/改形/倒出统一经过正确 operation permission；不绕过极性权限。
- [ ] A 类订单 solver / concrete plan / adapter 检查同样的角色与账户假设。

### C. 扩大样本与性质测试
- [ ] 极性矩阵：3 种 Jar polarity × 4 种 workshop polarity mode，全部操作入口覆盖。
- [ ] Jar transfer 极性笛卡尔积：Neutral/Cathode/Anode × Neutral/Cathode/Anode 接收端；所有转移都不做操作权限检查。
- [ ] Staging admission：Ban / capacity / zero / exact capacity / overflow / mixed liquid。
- [ ] B 类角色分配：需求数、角色候选、preparation 数量、相同 Jar 状态多实例、相同需求不同 identity。
- [ ] 用固定 seed 生成大样本；输出 seed、case count、passed/failed/undetermined、最小失败样本。
- [ ] 差分测试对照 Oracle，区分模型差异、Undetermined、真正不一致；保留 Oracle。
- [ ] 资源守恒性质：初始 + Produce - Consume - Discard = 最终账户总量；Transfer 只改变账户归属，不改变总量。
- [ ] 预算边界、未知规则、重复命令、checkpoint restore、operation ID 幂等性。

### D. 完成 Board / Compiler / Runtime 边界
- [ ] 明确 Board Topology、Assignment、Component Definition、Owned Instance、Rule Contribution、Effective Profile、Compiled Network、Runtime Account 的正式 DTO/领域契约。
- [ ] 设计完整 cache fingerprint；禁止使用只含 Slave profile 的旧缓存。
- [ ] 规则贡献按 scope 解析；未知 scope、未知规则、未实现执行器保持 Undetermined/拒绝 ingress，不静默忽略。
- [ ] Board Runtime Profile Set 只负责消费者路由，不把它当成编译器。
- [ ] 明确结构支持与 runtime adapter 支持能力矩阵，尤其多 Parser。
- [ ] 将 solver 的资源余额 readiness 与规则网络结构缓存分开。

### E. Space Demo 对齐
- [ ] 检查 PR #2 对 Core PR #60 的精确依赖和 template ingress 调用。
- [ ] 检查 Assignment Map 与兼容 projection 一致性。
- [ ] 检查多 Parser runtime rejection。
- [ ] 检查 Parser checkpoint schema 2→3 migration、资源守恒、outbox 去重和 import receipts。
- [ ] 在 Godot 4.7.1 .NET 环境运行 GDScript/Probe/host integration。
- [ ] 不复制 Core 的订单可行性、cooldown、Jar 规则；Space 只提交请求并消费结果。

### F. 最后整体清理与规范
- [ ] 先生成旧文件/重复模型/过期报告/兼容投影/死代码清单，逐项标注 owner、引用、删除风险。
- [ ] 先迁移调用方，再删除过期实现；不得先删差分 Oracle。
- [ ] 统一目录、命名、文档索引、测试命名与 fixture。
- [ ] 清理临时日志、过期生成物、过时说明；保留可复现验证证据和必要快照。
- [ ] 全量 build、xUnit、Godot host build、random/capacity/property tests、solver differential、schema/config/protocol/session replay/security。
- [ ] 更新本状态文件，记录每项完成证据、commit SHA、测试数量与剩余事项。

## 6. 当前未解决风险

- B 类分支枚举仍有 3 个已知失败测试，必须先取得完整失败原因。
- 当前代码的 profile 搜索排序是 Slave-first；注释与用户模型强调 Master 先执行、handoff 后 Slave 执行。必须区分“求解候选偏好”和“执行时间顺序”，并由具体 program/assembler 保证后者。
- `effectiveSlaveRejects` 将 Slave capability Ban 与额外 reject 集合合并；必须确认其仅约束游离液体转入 Slave staging，不约束 Jar transfer 或 Jar 内部液体。
- 当前 `WorldOrderBClassBranchEnumerator` 将两个 Profile 都按 Master 初始 Jar 状态过滤，并把 Master/Slave preparation candidates 扩展为物理 Jar 数量；需要验证 role profile 的 Jar state 候选与实际 Jar ownership/transfer boundary 不混淆。
- 计划生成器对主从操作顺序做排列枚举，预算复杂度可能快速增长；需分析必要的偏序约束，避免无意义全排列。
- 资源效果适配尚未覆盖所有实际执行消费者；RuleTransitionContract 目前是统一契约基础，不是已完成的通用规则解释器。
- Space PR #2 尚未验证与当前 Core head 的集成。
- 本地容器没有 .NET SDK，不能把本地未执行的 build/test 标为通过；使用 CI 或独立可运行环境作为验证证据。

## 7. 本轮追加修复（2026-11-24）

- 最新已验证 head `1f04b11bc8d85c03cbdc88f851371d9ef1526fdb` 的 PR Actions run `45397779801` 与 push run `45397769223` 均为 success；xUnit analyzer 修复后，B 类 receiver-only replay 相关改动已通过该 head 的整套验证。
- 新增检查发现 `WorkshopRoleTransferTransitionAdapter` 对 transfer Jar IDs 做了唯一性校验，却把来源 Jar inventory 直接转成 HashSet，未拒绝来源快照自身的重复 Jar ID。该快照损坏会使 sourceJarCount 失真，且让转移证明基于非规范库存继续生成。
- 已在 commit `88d1a61ed0f61a36e4e17e10c530b6f7a952b6f7` 修复：来源 inventory 必须自身唯一、正数；transfer IDs 必须唯一、正数且属于 exact source owner。
- 在 commit `0acca11fc8ec67c29fcc046f6638b15a74105f84` 新增重复来源库存 ID 回归测试。
- 在 commit `73c7e5165ae20c150a3b3c5850adc440a28d9a2b` 增加 4×4 工作台 Ban 组合的 Jar handoff 矩阵测试（Neutral / Cathode / Anode / NeutralOnly 对应的 staging ban profile）；16 种来源/目标组合均要求只产生 exact Jar ownership Transfer，不生成 polarity transition。
- 来源库存唯一性修复与重复 ID 回归测试已包含在 head `0acca11fc8ec67c29fcc046f6638b15a74105f84` 的 PR run `45398742181` success 中。后续 4×4 handoff 矩阵测试（`73c7e51`）仍待 CI 验证。
- A 类搜索的预计算解集复用经检查：只有 inventory filtering 未改变 requirements 且候选列表逐项完全相等时才复用；profile versions/specification 和 Master role 另行校验。Preparation 操作独立于 symbolic solution cache，因此目前未发现候选内容错用，但缓存仍缺少显式 fingerprint 类型，未来可进一步收敛为带 Profile fingerprint 的 key。
- `StagingAdmissionTransitionAdapter` 已显式把输出边界拆分为 accepted Transfer 与 discarded Discard，且验证 requested = accepted + discarded 及 staging/order balance delta；这符合“先进入当前订单输出边界，再结算到 staging 或丢弃”的分层。后续需继续检查实际执行事务是否原子提交这两条效果。

### 早期 receiver-only replay 修复

- Commit `7804d2c2ba49cba045a68284c31c7f9addc31209` 修改 `WorldOrderOrderedResourceReplay`：不再因为 Slave 存在 opaque solver rules 就无条件拒绝所有 B 类 replay；只有当前具体 program 确实包含 Slave Rule 节点时才返回 Undetermined。
- 领域理由：receiver-only handoff 不执行 Slave operation；接收 Jar 不检查接收台 polarity，也不解释接收台化学规则。Slave opaque rule 只应阻止需要 Slave 执行操作的分支。
- 该修复已包含在 `1f04b11` 的整套 Actions success 中；后续 transfer inventory 修复是新提交，尚待验证。
- 后续检查确认 `WorldOrderSlaveBoundaryProjector` 只将 Ban/reject 用于 Slave 游离液体消耗、生产与边界需求；JarState handoff requirements 不读取 Jar polarity 或接收端操作能力。仍需用真实 session 集成测试覆盖该路径。

## 8. 下一次会话的第一动作

1. 查询 PR #60 当前 head 与 Actions 状态。
2. 获取最新运行的 xUnit 详细结果；若 Actions 仍无 Assert message，则用可运行的 .NET 10 环境复现。
3. 优先修复三项失败，再做真实 session handoff 集成测试。
4. 按第 5 节 A→B→C→D→E→F 顺序继续；每阶段更新本文件。


## 9. 继续推进记录（2026-11-24，接续会话）

### 当前仓库事实校正

- 本节以 GitHub PR 元数据与分支文件为准，覆盖本文件前文过期的 head 记录：PR #60 当前 head 为 `6a8a3a892f1318488ce2cc2f482e27960cb5a019`，base 仍为 `a52f9465ad4febdcfb821227f7b2e03b2ddc012c`；PR 仍为 Draft / Open / 未合并。
- 精确 head `6a8a3a8` 的 pull_request Actions run `45399153217` 与 push run `45399139586` 均为 success。此前 `73c7e51` 的 4×4 Jar handoff / staging-ban 矩阵已进入这个通过验证的 head；16 个来源/目标 Ban profile 组合均验证只生成 exact Jar ownership Transfer，不产生 polarity transition。
- 上述 success 只证明该 head 的 CI，不代表之后新提交的测试已经通过。后续每次提交均需核对其精确 head 的 Actions。

### 真实 B 类会话边界审查

- 检查了 `WorldOrderExecutionSession`、`WorldOrderConcretePlanAssembler`、`WorldOrderConcreteOperationMaterializer` 与现有 session tests。
- 已有端到端场景 `BTransferAllowsMasterPolarityJarToCrossIntoOppositePolaritySlaveWithoutLiquidHandoff`：阴极主台对酒 Jar 执行 MATCH，阳极从台不执行任何操作步骤；B 类计划仍把 Jar 纳入 TransferJarIds，执行会话提交主台到从台的精确 Jar Transfer Transition，且不将 polarity 写入转移前后状态。
- 本次发现原测试主要检查最终订单完成和最终 TransferTransition，未直接断言 MATCH 完成后的 TransferPending 阶段边界。已在 commit `85405d8d87228357b13ad2f56f730c154290ece1` 加强该测试：会话启动后必须处于 Transfer 阶段、主台步骤数为 1、从台步骤数为 0、TransferTransition 尚未提交、Jar 仍在主台且从台为空；随后单次逻辑 tick 完成 receiver-only handoff。
- 这项增强把“主台先操作、后交接、从台不必操作接收 Jar”的时序不变量固定为真实会话回归测试。该新提交的 Actions 尚待检查，不能标记为已通过。
- 当前真实执行会话在 B 类交接后直接执行从台步骤；若从台步骤为空，则完成交付并移除交付 Jar。因此本测试能验证接收端不需要执行异极 Jar 操作，但不会在完成态保留该 Jar。若未来需要在交付前观察从台持有 Jar 的中间态，应新增显式 runtime handoff checkpoint/event，而不能通过延迟订单完成或伪造 Slave operation 来实现。

### 下一步未完成项（按优先级）

1. 查询 commit `85405d8` 的 push 与 PR Actions；失败时读取 job logs 并修复。
2. 扩大真实 session 测试：覆盖 Master MATCH 后 handoff、Master 多步操作后 handoff、Slave 有后续合法操作、Slave receiver-only、跨台后 checkpoint/restore；重点验证操作资格标记、Jar identity、库存归属与资源租约阶段一致。
3. 审查 `OrderTransferService`、`WorkshopRoleTransferTransitionAdapter`、`StagingAdmissionTransitionAdapter` 的实际状态提交边界，确认 ResourceEffect 与实体库存/暂存余额在同一事务中提交，失败不得只提交一侧。
4. 追加 staging conservation / idempotency / partial capacity 固定 seed 性质测试，并记录 seed 与 case count。
5. 回到 A 类缓存 fingerprint、Board/Compiler/Runtime capability matrix、Space PR #2 对齐和旧文件清理；未验证的 Godot 运行环境继续明确标注未运行。
6. 每一阶段都更新本文件，并记录精确 commit 与 CI run；不得将旧 head 的绿色结果挪用到新 head。


### 资源交接提交路径审计补充

- 已审查 `OrderTransferService`、`WorkshopRoleTransferTransitionAdapter`、`StagingAdmissionTransitionAdapter`、`OrderResourceSettlementService`、`OrderLiquidTransfer`、`WorkshopLiquidStaging` 与 `SlaveRollback`。
- Jar handoff 的 Adapter 只构造证明，不移动实体；真正移动由 `OrderTransferService` 执行。它先完成来源唯一性、精确 Jar 身份、目标罐位、游离液体 Ban/容量、OwnedInstance identity 与账本 tick 的只读预检，再调用 `commitStarted`，之后移动 Jar 和液体。
- 当前 Jar 批次不是单个不可分割的底层集合替换：它逐个执行 `TransferOut/TransferIn`。但 `commitStarted` 在第一次变更前设置会话回滚所有权；提交阶段发生异常会进入失败收尾，`SlaveRollback` 将已进入从台的资源回落/丢弃。因此它属于同步预检 + 提交 + 补偿式回滚，不应在文档中误称为数据库式原子事务。
- 新订单范围下的游离液体批次由 `OrderLiquidTransfer.TryExecute` 执行：先验证目标批次 Ban/容量和来源余额，再同步扣减 Master 子账并增加 Slave 子账；它没有部分接收分支。旧版 LiquidStorage 路径则逐种转移并使用失败补偿，需继续保留兼容路径测试。
- `StagingAdmissionTransitionAdapter` 是纯证明构造器；`WorkshopLiquidStaging.ReceiveWithTransition` 先执行权威 Receive，再根据同一调用的前后余额构造 transition；若证明构造失败，会撤销本次已接收数量。订单操作的 `TryReceiveExact` 则坚持全量接收，不允许部分成功。
- `OrderResourceSettlementService` 先 Drain 订单子账，再执行 Ship inventory Return，记录一次性 settlement receipt，并通过 operation ID 批量写账本；重复结算读取已有 receipt，不再次 Drain。仍需测试 Return/账本写入/租约释放之间的异常恢复边界。
- 重要限制：当前 Adapter 的 Jar Transfer proof 在提交前构造，提交后核对源/目标 Jar 数量和目标 staging 容量；后续应加强为按 exact Jar ID 的提交后账户快照校验，并增加注入式故障测试，证明第 N 个 Jar 转移失败时前 N-1 个已移动 Jar、液体账户和租约都能被补偿到一致状态。


### 精确 Jar 身份交接审计与修复（2026-11-24）

- 在 `OrderTransferService` 中发现一个真实边界风险：交接预检逐个构建 `Dictionary<int, Jar>`，但没有在整个来源库存层面拒绝无效/重复 Jar ID，也没有在进入字典前拒绝 Manifest 中重复的 transfer ID。重复 Manifest ID 可能以 `Dictionary.Add` 异常离开预检路径，而不是稳定的领域拒绝。
- 已在 commit `ca06af670d152f91ee1a9cc58ff4a0fe0ed56510` 修复：预检开始先验证来源 Jar ID 全部为正数且唯一，再验证 transfer 清单全部为正数且唯一；不满足时返回明确拒绝原因，不触碰资源。
- 同一修复还加强提交后核对：不能只用来源/目标 Jar 数量证明交接正确，因为同数量可能掩盖身份替换。现在会确认 transfer 清单中的每个 exact Jar ID 已从 Master 消失，并且 Slave 持有的清单内 ID 集合与 Manifest 完全一致。此校验发生在交接提交后；若不一致则抛出提交异常，交给会话失败收尾补偿。
- commit `7a1c4a7307274645a4d811458d64ef5921347871` 为真实 B 类会话测试补充精确归属断言：Master 两只 Jar 全部移出，Slave 的 Jar ID 集合恰好等于 Manifest 的两只 Jar，不多不少。该测试也同时保留源 Jar 由 Master 操作后标记、工具 Jar 留给 Slave 操作的断言。
- 两个提交都已触发 GitHub Actions；在精确 head 的两条 Actions 完成前，不把它们标记为通过。
- 当前交接实现仍是“预检 → commitStarted → 顺序移动 → 提交后身份核对 → 异常补偿”的同步补偿事务，不是持久化数据库原子事务。下一项必须增加可控故障注入，在第 N 个 Jar 转移或液体账本写入失败时验证资源归属、液体账户、lease、reservation 和 ledger 的补偿一致性。

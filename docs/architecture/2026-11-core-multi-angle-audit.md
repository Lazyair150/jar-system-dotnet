# Jar Core 多角度架构审查与收敛方案

- 日期：2026-11-25
- 审查分支基线：`3c38a3fab85cf7fde8cf943a3e9ea592a7117f5d`（World V2 phase-6 runtime switch-event conservation）
- 当前工作分支：`architecture/core-liquid-identity-consolidation`
- 关联 Space：`Lazyair150/space-demo`，PR #2，当前分支 head `c7451e89a71a65404c6c343419faf7af44ebc6b6`（代码 head `a19a6afcf47d06bf500764407bad63e8f6bd5dde`）
- Space cross-repo workflow 已固定 checkout Core source `5023b38b75ad4cf66d953e3bb3fa4c490076d4d7`；Space run `45588639250` SUCCESS。
- 证据原则：源码与测试优先；文档描述设计意图，不替代实际实现与运行证据。

## 1. 审查目标与边界

本轮不是为 Space 的“燃料/冷却液”添加两个别名，也不是将 Core 规则改成游戏资源语义。目标是使 Jar Core 真正拥有通用、可配置的二元互斥液体模型，并把 Space 所需的 World V2 核心实现收敛进 Jar Core 的主线候选。

保持既有领域约束：
- Jar 是独立个体；Jar 内液体与工作台游离液体暂存账户是不同资源所有权。
- Jar 内液体只可能为空或两种互斥类型之一。
- 工作台 Ban 仅约束工作台游离液体暂存区的取用/接收；不扩张成 Jar 内液体的全局禁令。
- 主从工作台是工作台角色/资源边界，不是 Jar 的额外状态。
- Space 拥有战斗、技能效果、玩家进度与场景；Core 拥有订单、规则、求解、执行、资源账本与生命周期。
- Space 不复制求解或资源守恒逻辑；Core 不引用 Space、Godot Node 或战斗概念。

## 2. 已确认的液体抽象缺陷（P0）

### 2.1 观察到的实现耦合

在审查基线中发现：
- `Domain/JarPolarity.cs` 的 `JarPolarityResolver.TryFromLiquid` 将字符串 `酒` 固定映射到 Cathode、`蜜` 固定映射到 Anode。
- `Domain/WorkshopCapability.cs` 的 `PolarityMode` 直接通过 `IsBanned("酒")` / `IsBanned("蜜")` 推导工作台极性模式。
- `Config/WorldOptions.cs` 虽把 `Liquids` 声明为世界词表，但验证逻辑仍要求其名称与顺序等于 `Constants.LIQUIDS`；默认订单模板和工作台也直接写入 `酒`、`蜜`。
- `Config/Constants.cs` 将液体目录固定为 `["酒", "蜜"]`。
- `Solver/LiquidRequirementCalculator.cs`、`Executor/GreedyPlanBuilder.cs` 与 `Domain/WorkshopRulebook.cs` 仍从静态 `Constants.LIQUIDS` 遍历候选资源或消耗。
- `Domain/Jar.cs` 的 `Polarity` 由静态 resolver 根据 Jar 的液体名称即时推导，未携带世界词表/液体定义上下文。

因此，Space 的 `燃料` 进入 Core 后报 “Liquid has no registered Jar polarity” 是模型耦合暴露出来的症状，不是应通过新增一个字符串 if 来消除的偶发问题。

### 2.2 领域定义（权威）

液体在 Core 中应表达为：
1. 某些操作的消耗品；
2. 世界提供恰好两种液体身份；
3. 两种身份分别属于两个互斥的极性类别；
4. 单个 Jar 只能为空、第一种极性或第二种极性；
5. 名称、展示文本、库存键、生产速率键均是世界内容数据，不构成极性语义本身。

禁止：
- 在求解器、执行器、Jar 或工作台能力中比较 `酒`、`蜜`、`燃料`、`冷却液` 等业务名称；
- 让“液体名的字典序/列表偶然顺序”隐式决定语义；
- 把工作台 Ban 混同为 Jar 内液体互斥规则；
- 通过全局静态可变注册表在多个 World 之间共享液体映射。

### 2.3 目标模型

引入不可变、随 World 配置装配的二元液体目录/极性映射（具体 API 在设计阶段冻结）：
- 两个唯一、非空、稳定的液体 ID；
- 显式的两种极性分配，或由明确、版本化的目录定义建立一一映射；
- 世界装配时验证恰好两个身份、身份唯一、映射完整且互斥；
- Core 算法通过目录枚举身份，通过映射取得极性；Jar 的极性推导必须有其所属 World 的目录上下文，或在创建/变更时保存经验证的领域极性值；
- 快照、事件、订单、配置指纹保存稳定 ID，不依赖展示名或默认序列化属性顺序；
- 无效/未知液体身份 fail-closed，不能静默丢弃或任意映射。

在实现前必须评估 Jar 的对象池回收/重用、跨 World 隔离、快照恢复、旧存档迁移与所有构造入口，避免引入隐式全局上下文。


## 2.4 本轮已实施的核心修正（待整合 head CI 复验）

- 新增 `Config.LiquidPolarity`，`WorldOptions.LiquidPolarities` 以显式字典将两种世界液体身份分别映射到 Cathode/Anode。
- `Vocabulary` 以只读映射保存二元关系；自定义液体身份必须显式给出映射，列表顺序和名称不参与推断。仅旧默认词表保留精确兼容映射。
- `Jar.Polarity` 通过创建该 Jar 的 Vocabulary 解析；context-free 的静态 resolver overload 已移除。Solver facts 不再从任意 liquid 字符串静态推导极性。
- `WorkshopCapability` 不再从 Ban 推导 `WorkshopPolarityMode`；该兼容投影固定为 Unrestricted。Ban 只在 free-liquid staging 的 pay/receive/transfer 入口生效。
- `ReshapeRules.SelfConsume` 不再因工作台 Ban 拒绝 Jar 自耗；`WorkshopRulebook` 不再按 Ban/极性拒绝 MATCH、PourOut 或 Jar 内部液体操作。
- 规则契约版本提升至 5，PourOut/ReshapeFull/MATCH 规则版本分别提升，表示规则语义变化。
- `MatchingSolver`、`SolverPrecheck`、`StateSpaceSolver`、`PartitionSearch`、`WorldOrderRuleCandidateProfile`、`GreedyPlanBuilder`、`SwitchCases`、`ResourceTransferrer` 与 `OrderExecutionSession` 已从静态液体常量迁移到所属 World/Workshop 的液体目录或账户键。
- `LiquidRequirementCalculator` 增加显式 World vocabulary 参数；B 类主从资源需求/容量路径已传入对应工作台词表。无参数重载仅保留旧默认世界兼容。
- `RuleTransitionContract` 只验证空/Neutral 与非空/两种极性之间的三态结构；液体名与极性的对应由拥有该 World 的 Vocabulary 负责。
- 默认 Core `config/world.json` 显式声明旧默认映射 `酒→Cathode`、`蜜→Anode`。
- 新增 Core 测试覆盖自定义名称、顺序无关映射、无映射拒绝、WorldOptions JSON 往返，以及 staging Ban 不限制 Jar 极性/自耗。

Space 侧同时在两个实际战斗世界中明确声明 `冷却液→Cathode`、`燃料→Anode`；Loadout schema 升为 3，并只对精确旧 Space 液体集合提供确定迁移。

当前仍需 CI 证明所有旧测试/协议 fixture 已完成迁移；截至本报告更新时，最新 Core run 仍在执行，不能标记绿色。

## 3. 六个工程审计维度

### 3.1 静态质量与代码设计

检查点：
- 单一职责、命名、可变状态封装、参数/返回值契约；
- 魔法字符串/数字、静态全局状态、隐式默认值；
- 重复规则实现、异常边界、错误码稳定性；
- 方法复杂度、长类、无效抽象与过度拆分；
- Nullable、集合防御复制、不可变快照；
- 依赖方向与架构测试。

已确认的高风险点是液体词表的静态耦合。其他项需要逐目录审查并以测试/源码证据记录，不能仅凭类长度或风格偏好判定缺陷。

### 3.2 生命周期与状态机

检查点：
- World/Host/Session 创建、租约、串行执行、暂停、故障锁存、释放；
- 订单成功/取消/中断及资源预留释放；
- Workshop 主从替换两阶段提交、失败回滚与一次性迁移；
- Jar 对象池借出/归还、引用失效与恢复；
- 事件幂等键、checkpoint/save restore 与重放。

既有 World V2 已有 Host 生命周期、Fault boundary、资源交接与固定 Seed 测试；必须在统一主线后重新跑，不可把某个历史分支的成功当成新整合 head 的成功。

### 3.3 跨仓库契约与依赖方向

目标依赖：
`Space UI → Application Flow → Space Domain → WorldV2 Host/Bridge → Jar Core`

Core 禁止依赖 Space/Godot；Space 只使用 Core 的稳定 DTO、Application Service、Host/Bridge 接口。配置指纹必须明确字段顺序/规范化协议，不能依赖运行时默认序列化。

需要统一：
- Core source/bridge/package 的单一权威版本；
- Space 的 Core 项目引用或依赖 pin；
- World options schema 与验证规则；
- 液体身份/极性、形状词表、订单模板、资源 ingress/export；
- 错误、事件、时间与 checkpoint 契约。

### 3.4 资源、存档与迁移

检查点：
- Jar 内液体 vs 工作台 staging 液体；
- Master/Slave 的来源、目标、Ban、容量、拒收、丢弃和结算；
- 生产 accepted/discarded 账目；
- 资源预留、执行消耗、取消释放；
- 序列化往返、版本升级、旧数据拒绝/迁移；
- 多世界之间不得共享词表或账户状态。

守恒要求始终是按液体身份分别核算；改名只改变身份键/展示内容，不应改变操作结构和守恒结果。

### 3.5 测试体系与证据等级

按层建立测试：
1. Domain 单元测试：二元目录、极性互斥、Ban scope、操作成本；
2. Solver 属性/固定 Seed 测试：不同液体 ID 的结构等价性、确定性、资源守恒；
3. Application/Runtime 集成：Host 串行化、预留、故障、暂停、dispose、换台；
4. Bridge/Host 契约：DTO、指纹、schema、错误投影；
5. Godot Headless 场景测试；
6. 本地 GUI 验收（单独记录，不能被 Headless 替代）。

关键新性质测试：对同一 World 输入只做两种液体身份的双射重命名，且保留相同极性定义，求解是否可行、步骤结构、消耗向量、产出/丢弃账目、主从交接与最终状态应保持同构。另测交换极性映射后，行为按映射变换，而非按名字变换。

### 3.6 工程清理、文档和 GitHub 治理

- 区分生产代码、测试、协议 fixture、审批基线、迁移资产、历史分析文档与生成物。
- 只有确认无引用、无迁移职责、无协议用途且有替代物的文件才删除。
- 不通过 `.gitignore` 隐藏仍被 Git 跟踪的历史垃圾；需要显式删除并记录。
- 旧 PR/分支先分类：已合并、被后续栈覆盖、仍包含唯一实现、仅验证失败/修复副本、待验证。
- 先建立单一整合 PR，再关闭 superseded PR；不得先关闭再丢失唯一代码。
- 历史工作记录不删除；以索引和状态标签替代重复的“当前状态”副本。

## 4. Space Core 收敛现状

已确认：
- Space PR #2 仍 Draft/Open，head 为 `5a03c259f733eb1ef9f06439ff8f0a805d8e42c0`。
- Space 当前交叉集成记录 pin 为 `b342259fe04363386085dc9a4ca995112954ed43`。
- Core 最新可见 World V2 运行时分支为 PR #62 head `3c38a3fab85cf7fde8cf943a3e9ea592a7117f5d`；其 CI run `45443916007` 失败，原因是测试分析器 xUnit2029：`WorkshopReplacementTests.cs:1179` 对过滤集合使用 Assert.Empty。
- 本工作分支已将该断言改为 `Assert.DoesNotContain(...)`，提交 `6a2a954b8fc512c1e7bd26a4f218a4293038fa20`。新 head 必须重新 CI 验证。
- Core 默认 `main` 为 `40e578e6e32472549d647ed903ef98cc98028bb6`；World V2 候选分支与 main 有大量新增提交，且 main 有 2 个候选分支未包含的提交。不能把候选分支称为 main 已经包含。
- Core PR #60/#61/#62 是堆叠/交错的活动 PR；PR #61/#62 描述明确要求不能脱离其基线独立合并。
- Space 项目需要的 Core 不能只复制几个文件；必须收敛完整 Core + World V2 Host/Bridge + 测试和配置，并确保唯一依赖源。

### 整合策略

1. 以当前最新 World V2 候选 head 为基线建立单一整合分支。
2. 把 main 的 2 个独有提交纳入候选分支（先检查内容与冲突）。
3. 先修复候选分支已有 CI 门禁，再做液体抽象。
4. 将 Space 的 Core 引用改为同仓库 Core 项目/正式包的单一来源；移除旧副本或旧 pin 前先查所有项目引用、构建脚本和工作流。
5. 在单一 PR 中承载 Core 主线整合；不直接推 main，不绕过 PR 审查。
6. 整合成功后再关闭已被包含的旧堆叠 PR，并删除只属于已合并旧分支的临时远端分支。保留仍有唯一提交/证据的分支，直到其内容有可验证替代。

## 5. 当前执行状态与验收门槛

- [x] 建立 `architecture/core-liquid-identity-consolidation` 分支。
- [x] 记录六维审计框架与液体抽象缺陷。
- [x] 更新 Core `.gitignore`，只忽略生成缓存/本机诊断，保留规范文档和工作记录。
- [x] 修复已知 xUnit2029 门禁错误。
- [ ] 完成 Core 全部硬编码液体引用盘点（包括测试、默认配置、迁移与展示层）。
- [ ] 冻结二元液体目录与 Jar 极性上下文 API。
- [ ] 实施改造并增加重命名同构/极性映射变换测试。
- [ ] 合并 main 的独有提交并解决冲突。
- [ ] 让 Core 候选整合 head 的全部 CI 通过。
- [ ] Space 指向统一 Core 源并完成跨仓库验证。
- [ ] 建立单一主线 PR，按证据关闭已被取代的历史 PR。
- [ ] 清理已证明无用的文件/分支并留痕。
- [ ] 本地 Godot GUI 验收。

本文件是持续审查报告；每个阶段的实际改动和精确 SHA 记录在 `docs/work-records/`，不能将计划项写成已完成项。


## 2026-11-25 追加审查发现：World V2 配置边界同样必须承载极性映射

二次检查发现，首轮只改造旧版 WorldOptions 还不够：Space 实际运行使用的是 WorldV2Options，原来的 WorldV2Factory 仍调用 `new Vocabulary(options.Shapes, options.Liquids)`，而 WorldV2Options 没有 `liquidPolarities` 字段。这意味着 Space 的 loadout 即使声明映射，也可能在 Core World V2 装配边界丢失映射或退回默认语义。

已在整合分支补齐：
- WorldV2Options 增加 JSON 字段 `liquidPolarities`，默认值只服务旧默认 World（酒→Cathode、蜜→Anode）。
- World V2 配置验证要求两种身份与两条映射完整对应，并且 Cathode/Anode 各一次。
- WorldV2Factory 将该映射传入 Vocabulary，不再仅传液体列表。
- WorldV2ConfigurationFingerprint 通过 canonical JSON 序列化包含该字段；映射变化会改变指纹。
- 新增 WorldV2FactoryTests 覆盖自定义 fuel/coolant 配置装配、JSON 往返、指纹变化和缺少映射拒绝。

对应提交：`9b3f5d3a314932c466a6ab0be899abd882179f51`、`df51c40968402c593569de3512b14c40f4f466b4`。以上待当前整合 head CI 通过后再视为验证完成。


## 2026-11-25 第七轮实施更新：Ban 不得进入 Jar 操作判定

进一步审查将抽象问题缩小到具体执行边界：

- `WorkshopCapability.PolarityMode` 已固定为 `Unrestricted`，`CanOperateJar(JarPolarity)` 也不再依据 Ban 拒绝 Jar；但这还不足以保证全链路正确。
- `SolverPrecheck.CanServe` 曾在 POUR_OUT 分支额外检查 `!IsBanned(jar.Liquid)`，导致被 Ban 的 Jar 内液体无法倒空以满足空罐需求；该判断现已移除。
- `MatchingSolver` 曾在候选应用、产液来源与路径执行处调用 `CanOperateJar`。现已删除 Jar 操作门禁；保留对 POUR_OUT 产液进入 staging 的 Ban 检查，因此被拒收产液不会被计入可用资源。
- `Workshop.PourOut(Jar)` 旧实现先 `AddLiquid`，若 Ban/容量拒收则直接返回、Jar 不发生变化。现已改为先确定操作许可，尝试接收 staging，再提交 Jar 变空；接收失败只丢弃产液，不回滚 Jar 状态。订单范围接口同时保留完整的 Requested/Accepted/Discarded receipt。
- `Workshop.CanReshapeTo` 文档和测试已按 Jar 自耗独立于 staging Ban 修订。

新增回归覆盖：普通与订单 POUR_OUT 的 Ban 丢弃、满 Jar 自耗、三个通用 Solver 的一致结果、World A/B 候选边界、主从交接与替换。当前精确 head 的 CI 仍在运行，只有最新提交全部 workflow 通过后才能关闭本阶段。

## PR 与分支清理状态

- 旧 PR #1–#20（#21 已在补齐/审计其 Host 生命周期后关闭）、#22–#49、#52–#62 已关闭为 superseded；#50/#51 的历史 Ban 语义被明确标记为非权威并关闭。
- PR #63 是唯一保持 Open 的 Core 整合 Draft PR。旧分支和提交历史未删除，避免破坏审计证据。
- 当前整合分支与 `main` 比较：main 是整合分支祖先，整合分支 ahead 2216 / behind 0；main 没有未纳入的独有提交。
- `.gitignore` 已忽略本机 Godot 缓存、IDE/构建/测试输出、临时诊断与 `docs/work-records/_local/`，但保留契约基线、正式工作记录和架构文档。
- 工作记录专区入口：`docs/work-records/README.md`。


## 2026-11-25 第九轮：检查点、序列化与 B 类执行语义闭环

### 检查点数据生命周期
此前的审查已确认 WorldV2Options / WorldV2Factory 显式携带极性映射，但进一步沿活动执行检查点追踪时发现：WorldV2Context 和根级 Wire DTO 仍只保存 Shapes/Liquids，恢复阶段重新构造 Vocabulary 时会丢失映射。这是独立于初始装配的真实 P0 数据生命周期缺陷。

现已补齐：
- WorldV2Context 暴露所属 World 的只读 LiquidPolarities，并把映射写入活动执行检查点。
- WorldV2ExecutionResourceCheckpoint 对映射做完整性、身份集合与 Cathode/Anode 一一互斥验证。
- WorldV2ExecutionCheckpointWireDto 序列化映射，解码时使用该映射构造 Vocabulary。
- Restore 在任何状态提交前核对 checkpoint 与目标 World 的身份目录和映射完全一致。
- checkpoint JSON schema 升为 3；不把 schema 2 缺失字段静默解释成新格式。
- 新增自定义 fuel/coolant 的 checkpoint JSON round-trip + restore 回归。

### B 类测试证据修订
生产枚举器明确使用 Slave-first 的候选策略。此前三项测试将一个可行路径硬编码为 MasterSteps，无法区分“求解器选择了另一个同样合法的角色路径”和“资源/交接不变量失败”。现将这些测试改为验证实际的 Slave-first 路径：先发生 Master→Slave Jar 所有权交接，再执行 Slave MATCH；从台 Ban 不限制 Jar 内部液体操作。多 Jar 注入失败测试仍验证第二次物理移动失败时整个交接回滚。

ProducerSlot 的旧单液体 fixture 也已迁移为合法二元 World；手工 checkpoint 测试构造器已全部补充显式映射。

### 验证状态与下一步
- 初始 PR #63 head 的 853 项 xUnit 有 5 项失败；两项是旧单液体 fixture，两项是角色路径预期陈旧，一项是角色步骤断言陈旧。逐项读取 CI 日志和源码后修正。
- 最新记录中的 Core head `ee37b8595474c228039727fe573d823aad1d3a41` 的 verify runs `45586817186` / `45586828250` 在本轮记录时尚未完成；后续修订 head 仍需以精确 SHA 的最终 Actions 结果验收。
- Space PR #2 的项目引用已是 sibling WorldV2Bridge ProjectReference，但其 cross-repo workflow 仍固定 checkout 旧 Core SHA `b342259fe04363386085dc9a4ca995112954ed43`。Core 绿色后必须更新 Space workflow 与 `CORE_EXPECTED_SHA`，再跑 Godot 4.7.1 全套跨仓库验证。
- PR #63 保持 Draft/Open；旧堆叠 PR 已关闭为 superseded，历史分支不删除。


## 2026-11-25 第十轮：World 单一词表装配约束与精确 CI 记录

- Core head `0ff648ea7f3f05489f04c278f75039de8271985d` 的两个 verify runs `45587335108` / `45587347149` 均 SUCCESS：854 项 xUnit，0 errors、0 failures；构建、World V2 Host、资源随机/容量/确定性样本、契约快照、schema 与配置世界等价验证全部通过。
- 绿色 head 之后继续检查公开的 `WorldV2Context.Create`，新增同一 World 内所有工作台必须共享相同液体身份集合与极性映射的装配约束，避免一个 Jar 在同一 World 的不同岗位被解释为不同极性。Context 的液体目录也必须与工作台词表一致。
- 新增反向映射冲突测试。最新 Core head 为 `fd0b3761babe3af7f7d770483004ef1a454021f8`，对应 runs `45587631031` / `45587642292` 在本节更新时仍执行中；只有这两个精确 head workflow 完成后，才能将其作为 Space pin。
- Space 当前 head `a8daee73df55bf30125960a9a6231ea93e706d4f` 在旧 Core pin `b342259fe04363386085dc9a4ca995112954ed43` 下的 run `45577582987` 成功，但不代表新 Core 整合通过。下一步是更新 Space workflow 的 Core checkout SHA 与 `CORE_EXPECTED_SHA`，重新执行 Godot 4.7.1 全套跨仓库验证。


## 2026-11-25 第十一轮：Core 绿色源版本与 Space 精确集成验证

- Core source commit `5023b38b75ad4cf66d953e3bb3fa4c490076d4d7` 的两个 exact-head verify runs `45588065611` / `45588076128` 均 SUCCESS；854 项 xUnit 与 build / Host / resource samples / contract / schema / configuration equivalence 全部通过。
- 该源版本还明确拒绝不包含 World-owned polarity mapping 的旧 checkpoint schema v2，避免把历史 JSON 缺字段误当成默认世界语义。
- Space PR #2 workflow 已将 checkout 与 `CORE_EXPECTED_SHA` 固定到 Core `5023b38b75ad4cf66d953e3bb3fa4c490076d4d7`。Space code head `a19a6afcf47d06bf500764407bad63e8f6bd5dde` 的联合 run `45588639250` 已 SUCCESS：Core Bridge 与 Space build、Godot 4.7.1 import/parse、HUD 21/21、Hangar/owned inventory/progression/domain、parser ingress、World V2 lifecycle/fault/commands/snapshot isolation、battle pause/navigation 全部通过。最终 marker 为 `CROSS-REPO-WORLD-V2-OK core=5023b38b75ad4cf66d953e3bb3fa4c490076d4d7`。负向 fault injection 日志属于预期测试，不是 CI 失败。
- 目前 GitHub 仅有 Core PR #63 与 Space PR #2 两个 Open Draft PR；历史 superseded PR 已关闭，历史分支保留。

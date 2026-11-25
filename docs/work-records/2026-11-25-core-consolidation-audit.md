# 2026-11-25 Core 多角度审查与 Space 收敛启动记录

## 目标

按既有工程规范完成 Jar Core 全面设计检查；将 Space 所需的 Core 能力收敛到最新 Jar Core；修正液体身份与二元极性硬编码；清理被取代的 GitHub PR/分支与过期文件；建立 ignore 和工作记录专区。

## 起始基线

- Core 默认 main：`40e578e6e32472549d647ed903ef98cc98028bb6`
- Core World V2 最新候选：`3c38a3fab85cf7fde8cf943a3e9ea592a7117f5d`
- Core 工作分支：`architecture/core-liquid-identity-consolidation`
- Space PR #2：`5a03c259f733eb1ef9f06439ff8f0a805d8e42c0`
- Space 集成记录中的 Core pin：`b342259fe04363386085dc9a4ca995112954ed43`
- Core PR #60/#61/#62 均仍处于开放堆叠状态；PR #62 的旧 CI run 失败，不能作为当前分支通过证据。

## 已完成的实际动作

1. 建立 Core 整合分支，起点为 World V2 候选 head。
2. 审查 `JarPolarityResolver`、`WorkshopCapability.PolarityMode`、`WorldOptions.Validate`、`Constants.LIQUIDS`、`WorkshopRulebook`、`LiquidRequirementCalculator`、`GreedyPlanBuilder` 等源码。
3. 确认液体名称与极性类别、默认配置、求解遍历仍存在耦合；详细事实见架构审查报告。
4. 修复 CI 已报告的 xUnit2029：将 `Assert.Empty(newMaster.LiquidStorage.Where(...))` 改为 `Assert.DoesNotContain(newMaster.LiquidStorage, ...)`。
   - 提交：`6a2a954b8fc512c1e7bd26a4f218a4293038fa20`
   - 文件：`test/JarSystem.Tests/WorldV2/WorkshopReplacementTests.cs`
   - 尚待在新 head 上重新执行 CI。
5. 更新 Core 根 `.gitignore`，忽略 Godot 编辑器缓存和本机诊断文件；保留源文件、契约基线、文档和工作记录。
   - 提交：`e61d75fecf92bf4954896797582897556aa18e7d`
6. 新增本审查报告与工作记录专区。

## 重要审查发现

- Core 当前将 `酒` 固定为 Cathode、`蜜` 固定为 Anode。
- 工作台 Ban 的极性模式也直接查询这两个名字。
- `WorldOptions.Liquids` 虽是 List，但校验仍拒绝名称和顺序变化。
- Solver/Executor/Rulebook 多处仍枚举静态液体常量。
- Jar 的极性由无 World 上下文的静态 resolver 从名称推导。
- 这与“液体是某些操作的消耗品，世界定义两种相互排斥类型”的概念不一致。

## 规范映射

审查沿用 Space 工程规范的分层依赖、稳定契约、状态所有权、可观察失败、不可变快照、真实行为测试等原则。阿里巴巴 Java 开发手册只作为通用工程原则映射，不机械套用语言专属规则。

## 验证状态

- 已读取 GitHub 上的源码、PR 元数据、分支元数据和 CI 日志。
- 当前分支的新 CI 尚未取得结果。
- 尚未在本地执行 .NET build/test。
- 尚未做 Godot GUI 验收。
- 不声称本轮整合已经完成或 PR 已合并。

## 下一步

1. 继续全 Core 源码/测试/配置的液体身份引用审计。
2. 冻结显式二元液体目录与极性映射 API，明确 Jar 对 World 词表的上下文归属。
3. 批量替换静态液体常量遍历与默认资源硬编码。
4. 添加液体身份重命名同构、极性映射交换、Ban scope、资源守恒和多 World 隔离测试。
5. 将 main 的独有提交纳入整合分支。
6. 将 Space 的 Core 引用改为统一 Jar Core 源。
7. 建立一个完整整合 PR，待验证后再清理 superseded PR 与分支。

## 2026-11-25 第二轮：二元液体模型与 Ban 语义解耦

### 新确认的领域错误

审查发现旧 Core 还把 `WorkshopCapability.Ban` 推导为 `WorkshopPolarityMode`，并据此拒绝某些 Jar 内部液体操作。该逻辑不符合“Ban 只管工作台游离液体暂存区”的既定规则。已将其从领域决策中拆除：
- Ban 仍限制游离液体 staging 的支付/接收/迁移。
- Ban 不再限制 MATCH、Jar 内液体自耗或 Jar 的物理迁移。
- PourOut 可以完成 Jar→输出边界；输出是否进入暂存区仍由 staging Ban/容量决定，拒收按显式丢弃边界处理。
- RuleContractVersion 升为 5；PourOut v3、ReshapeFull v4、MATCH v5。

### 已实施代码范围

- 新增 `LiquidPolarity` 配置值类型。
- `WorldOptions` 增加 `liquidPolarities` 显式映射、完整性验证、Clone 和 JSON 字符串枚举序列化。
- `Vocabulary` 保存只读映射；自定义两种身份必须显式指定 Cathode/Anode；只对旧默认词表保留兼容映射。
- `Jar` 的液体验证和极性解析绑定其 World Vocabulary。
- `WorkshopCapability` 的 Ban 不再派生 Jar 操作极性；`Workshop.ReplaceCapability` 保留所属 World Vocabulary。
- `JarFacts`/transition snapshot 传递显式极性事实；detached snapshot 验证三态结构，不根据液体名字猜测。
- 移除 SolverContracts 中无 World 上下文的 JarState/Requirement 极性属性；Solver 通过所属 Workshop capability 映射求值。
- Solver、Precheck、StateSpace、PartitionSearch、SwitchCases、ResourceTransferrer、OrderExecutionSession、WorldOrderRuleCandidateProfile、GreedyPlanBuilder 改为使用 World liquid vocabulary/account keys。
- `LiquidRequirementCalculator` 增加 World vocabulary 参数，B 类需求和容量路径传入所属角色的词表。
- 新增 `BinaryLiquidVocabularyTests`；调整 Ban/Polarity/transition 相关既有测试。
- Core 默认 `config/world.json` 增加显式默认映射。

### 验证与失败记录

- Core run `45574305499`：失败，最初只报告编译错误（SolverContracts context-free polarity 和 order-scoped transition 调用签名）；已修复。
- Core run `45575129154`：失败，测试/参考求解器仍依赖旧的 context-free Resolver 与 JarState/Requirement.Polarity；已将测试和 ReferenceSolver 改为显式 World context，并更新 Ban 派生极性测试。
- Core run `45575641509`：提交 `214f472b4083ecbe1b01a4d681199daab19188b8`，执行中后报告失败；已调整 transition snapshot 测试，不再把 liquid name 当作极性语义。
- 当前最新 Core head 在此记录更新时为 `e173cb759fb9f0118388664307c252d1dfdff257`，对应 run `45576014434` 执行中，尚无最终结论。
- 所有上述 CI 失败均已逐条读日志，不把“已修复”写成“CI 已通过”。

### Space 实际世界配置修正

截图中的 `燃料` 故障根因已在 Space 资源中定位：`data/hangar/demo_catalog_bundle.tres` 的两个战斗 World 使用 `冷却液`、`燃料`，却没有显式极性映射。Space 已在两个配置中声明：
- `冷却液 → Cathode`
- `燃料 → Anode`

Space profile 验证要求所有自定义身份都有完整映射；Frozen Loadout schema 升为 3。schema 2 只允许精确的旧 Space 身份集合 `{冷却液, 燃料}` 迁移，其他任意身份不自动猜测。新增 Probe 覆盖成功迁移和未知身份 fail-closed。

Space 相关提交：`851a0c9d20374a99c90bdfcb801dd83181c08813`、`c74c29b96c46b800389bbd0a1d0229f123b7ef53`、`03b8b1dcbee4b3f42058801dd7ec7d5f730f4631`、`4f5a84ebf63b96be0f1f2e9b29f4fcfd87677aab`、`f5fdffe9e958422f0c3c6612b22d75ead3ae5561`、`0a3454a59b22285368cfacea3a08db3b5221ae4a`。

### 当前状态

Core 与 Space 的配置/模型修正均已进入各自分支；Core 最新 CI 和 Space 跨仓库 CI 尚待最终结果。PR #63 保持 Draft。旧堆叠 PR 和远端分支暂不删除，直到 #63 合并并确认其内容完整覆盖。


## 2026-11-25 第三轮：主线基线与固定 Seed 分支收敛

### 已执行的 Git 图谱整合

- 比较 main 与整合分支时发现整合分支落后 main 2 个提交，且有 2 个只存在于 main 的资源模拟文档/测试文件。
- 将这两个文件按 main 当前内容同步到整合分支，并创建双父 merge commit：69cafecd7f6c5c421a73f7028c91d81b4020ca80。第一父提交 59e20847eb90e44055974d4d060db8574c469629；第二父提交 40e578e6e32472549d647ed903ef98cc98028bb6（main）。
- 再比较后，整合分支相对 main 为 ahead 2124 / behind 0，状态 ahead。最新 main 的两个提交已进入整合分支历史。
- PR #61 固定 Seed 扩展包含 2 个提交；已同步新增文档和更新后的 WorldOrderSymbolicResourceSimulatorTests.cs，并创建双父 merge commit 92597214bb0d2019acf9d018d0fc821c849994e2，第二父提交 9d45a9cc618186e3fbf60adb83beea35cc0b0caa。
- PR #62 head 3c38a3fab85cf7fde8cf943a3e9ea592a7117f5d 已是整合分支祖先。PR #60 仍有文档与 ignore 独有差异，暂不关闭，待单独核对。

### 本轮编译反馈与修正

- JarFacts 不再提供 context-free 默认极性推导；构造时必须提供 JarPolarity。
- CI 定位到 StateSpaceSolver 和 WorldOrderRuleCandidateProfile 两处漏传 polarity，现已修正：74f933ea7b16d968d46152c13819f2d0d58bf4d5、449903b1b8779931963eff3fd83856afa8fb03c4。
- 测试中的 JarFacts 断言改为显式提供默认世界 Neutral/Cathode/Anode：81958bb42503184d5703f9efb21092a2f02b4f3a。
- ReferenceSolver 的空罐匹配与操作入口改为使用所属 Workshop capability 的 World polarity：ce899bd17f82ea747a99eec494f672b6e55fdc0d。
- 修正后的 Core CI run 45577267527（head 81958bb42503184d5703f9efb21092a2f02b4f3a）尚在执行；后续提交的 run 也仍在执行，不能宣布 Core 全套通过。

### 当前分支基线

- 整合分支与 main 对齐，behind 0。
- 当前仍是 PR #63 Draft，不合并 main。
- PR #60/#61/#62 保持开放；#61 内容已作为 merge parent 纳入整合分支，但待整合 PR 验证完成后再关闭旧 PR，避免过早丢失审计入口。


## 2026-11-25 第四轮：World V2 真正装配边界与历史 PR 收敛

### World V2 映射链补齐

- 二次检查发现旧版 WorldOptions 已有 LiquidPolarities，但 Space 实际调用的 WorldV2Options / WorldV2Factory 仍只把 Shapes、Liquids 传给 Vocabulary；因此 Space 的自定义映射在真实 World V2 装配边界还没有贯通。
- WorldV2Options 现已增加 liquidPolarities、完整性/互斥校验；WorldV2Factory 已把映射传给 Vocabulary；WorldV2ConfigurationFingerprint 的 canonical JSON 包含该属性。
- 新增 WorldV2FactoryTests：自定义 fuel/coolant 能装配、JSON 往返保留映射、映射交换改变配置指纹、缺少映射时 fail-closed。
- 提交：9b3f5d3a314932c466a6ab0be899abd882179f51、df51c40968402c593569de3512b14c40f4f466b4。

### JSON 枚举序列化纠正

- 发现 JsonStringEnumConverter 被错误标注在 Dictionary<string, LiquidPolarity> 属性上，而不是枚举类型上。已把转换器标注移到 LiquidPolarity enum，移除 WorldOptions 与 WorldV2Options 字典属性上的错误标注，确保字典值以稳定字符串枚举序列化。
- 提交：5fb98b11cf0284969ccfd7e81c2c10190983f7dc、b8a38bba88b6f2557b8ab2438ed192151eeb4c90、17b50dec9764a9437c30f863a77a27befd2bfa29。

### Ban 与自耗回归

- 新增自定义液体身份的回归：工作台 Ban 燃料时，Jar 内燃料仍可执行 POUR_OUT；输出因 staging Ban 被拒绝并明确记为 discarded，不进入 staging。
- 提交：7d5e898b2ed8caaec022b1f1cf903545a1676b9e。

### GitHub 堆叠 PR 历史

- PR #60 的唯一文件差异已按语义吸收：.gitignore 补充 IDE、测试、日志、临时与构建产物规则；旧 2026-11-24 work-status ledger 保留原始历史，并追加 supersession note 指向当前工作记录与 ADR。
- 创建双父 merge commit 87296a754954f931a10394280b128b87098ac4d5，第二父提交为 PR #60 head 4358d19ebb8c583c12e07b9e1d86a716b10c4d45。
- 对照确认整合分支包含 PR #60、#61、#62 的各自 head，且相对 main ahead 2160 / behind 0。PR #60/#61/#62 暂不关闭，等整合 head CI 绿色后统一关闭，避免在验证前清理入口。

### 验证状态

- 当前整合 head 为 87296a754954f931a10394280b128b87098ac4d5。
- 对应 Core verify run 45578694767 与另一个工作流 run 45578686050 均在执行；之前多条 run 仍在执行或失败，不能以其中任何一个旧 SHA 代表当前 head。
- Space 当前分支 head a8daee73df55bf30125960a9a6231ea93e706d4f 的 CI run 45577582987 成功；但其跨仓库 Core pin 仍需在 Core 整合 head 通过后更新并重跑。


## 2026-11-25 第五轮：旧 B 类分支的缺失不变量回收与文档纠偏

- PR #53 的 head 曾有 4 个提交不在整合分支祖先链中。逐项检查后确认其关键缺口是“从台不能携带订单开始前的 Jar/游离液体库存”；生产枚举器当前已有该拒绝条件，但测试和阶段文档未完整保留。
- 补回回归测试 BClassCannotUsePreExistingSlaveInventory，并补充 B 类空从台初始库存约束文档；创建双父 merge commit `9ba16082e1992a76064e3129a8bd0404f455fc64`，第二父为 PR #53 head `8ae1d89f14404682bcb497ac2458e6a979929e4a`。比较确认 PR #53 head 已是整合分支祖先。
- 清理旧文档中的错误语义：Ban 不再被描述为 Jar 操作极性门禁。更新 phase-4.10b-9l-ban-storage-invariant、order-funnel、solver-integration-audit 与 World V2 README；历史阶段结论明确标为被 ADR-0006 修正。
- World V2 README 已加入 ADR-0006 索引。
- 当前 Core 最新相关 verify run `45579355853` 对提交 `37873ee08d2e74e425bf1d490c30468e6f257a6d` 尚在执行；该 SHA 之后仍有文档提交，因此最终必须以最终 head 的精确 CI 结果验收。


## 2026-11-25 第六轮：A 类旧栈收敛与过期 PR 关闭准备

- PR #52 有 11 个 head-only commits。核对后确认其关键实现已在当前树中：A 类具体分支入口、Master 输出 Jar 与 Slave 操作 Jar 的单次 handoff、Jar revision dependency，以及对应的 preparatory program 回归测试均已存在。
- 创建双父 merge commit `61f1da1108c0c32457f9a0947fe32cb254687740`，第二父为 PR #52 head `6fc93a10aee3d0ea682c32f3fc5899bba8a37415`。比较确认 PR #52 head 已进入整合分支祖先链。
- 目前已确认以下旧 PR head 均是整合分支祖先，可标记为被 PR #63 替代：#1–#20（#21 除外）、#22–#49（#50/#51 除外）、#52–#62（不含 #63）。其中 #21、#50、#51 的 head 不在整合分支祖先链中，暂时保留，避免误关仍有独有提交的分支。
- 计划将所有已确认祖先的旧 Draft PR 关闭为 Superseded；不会删除分支、提交或测试文件。历史内容已由 PR #63 的多父提交链保留。
- 关闭动作不代表 PR #63 已通过 CI 或已合并 main；PR #63 仍 Draft/Open，等待精确 head 的全量验证。


## 2026-11-25 第七轮：Ban staging-only 语义落到核心执行与求解路径

- 复查 CI `45581332967` / `45581325843` / `45581319193`，确认旧测试仍大批断言 Ban 派生 Cathode/Anode 工作台权限；同时定位 `SolverPrecheck.CanServe` 将被 Ban 的 Jar 内液体误判为不可 POUR_OUT，及 `MatchingSolver` 的来源/操作检查残留 `CanOperateJar`。
- 修正 `SolverPrecheck`：Jar 操作候选不再调用工作台极性许可；满 Jar RESHAPE 自耗不查 Ban；POUR_OUT 即使产液被 Ban 拒收，也可清空 Jar 并满足空 Jar 需求。修正 `MatchingSolver`：Jar 操作不再依赖 `CanOperateJar`，但只有未被 Ban 的 POUR_OUT 产液才可进入 staging 并作为后续游离液体供给。
- 修正 `Workshop.PourOut(Jar)`：执行 Jar 状态转换后再尝试 AddLiquid；Ban/容量拒收仅丢弃产液，不回滚 Jar 操作。订单范围 PourOut 原有 receipt 继续记录 Requested/Accepted/Discarded。
- 新增/调整回归：普通 PourOut Ban 丢弃；订单 PourOut Ban 丢弃；跨三个求解器验证 Ban Jar 液体可自耗；World A/B solver 与候选枚举器不因 Ban 拒绝 Jar 操作；工作台替换后接收台可操作迁移 Jar；允许重命名/重排两种液体，只要求显式映射；默认世界审批快照增加 liquidPolarities。
- 当前待精确验收的 workflow 已触发于连续提交 `3c6af817...`、`2d2eff4c...` 等；需等最新 branch head 的全部 workflow 完成并继续修复失败项。此轮尚未宣称 CI 通过。


## 2026-11-25 第八轮：StateSpaceSolver 产液路径纠偏与测试夹具迁移

- CI run `45583163015` 在 853 项测试中暴露 18 项失败；除旧测试语义外，定位到 `StateSpaceSolver.Apply` 对 `ProducedFreeLiquid` 无条件 `AddLiquid`，会把被 Ban 的 POUR_OUT 产液重新记入工作台池，违背“拒收即丢弃、Jar 操作仍提交”的规则。现改为仅在目标 staging 未 Ban 时增加游离液体，Jar 状态转换始终继续。
- 更新 `ProducerSlotTests` 与 World V2 WorkshopReplacement 测试夹具：所有非默认 Vocabulary 必须提供恰好两种身份和显式 Cathode/Anode 映射，不再用单液体/无映射构造绕过新世界契约。
- 更新液体身份重命名测试：同步替换库存、流入速率、Ban 与订单模板中的身份键，验证完整 World 配置在显式映射下合法，而不只是修改词表数组。
- 修订 B 类角色测试：Ban 不再让 Slave 缺少 Jar 操作候选；同一 Jar 可在交接后被从台操作。A/B partition differential 的预期已调整为允许 Jar 自耗，具体结果待最新 CI 复验。
- 最新代码提交 `549afcd1efff5b909ad6b581212bc6052506ffff` 已触发 Core verify workflow；仍在执行，尚未标记通过。


## 2026-11-25 第九轮：检查点极性映射闭环与 B 类角色测试对齐

### CI 证据与根因
- PR #63 初始整合 head `2a4b306b83328909c930707fbd2737b4e9012008` 的 verify run `45584186423` / `45584176474` 完成失败：Release build 通过，853 项 xUnit 中 5 项失败。
- 日志确认 2 项失败是旧测试仍构造单液体 World；2 项 B 类执行测试与枚举器明确的 Slave-first 角色优先策略不一致；另 1 项 B 类分支测试错误地强制要求 MasterSteps。
- 逐项读取测试和执行器源码后，未通过删除回归、降低约束或修改生产求解器来消除断言。

### 发现并修复的真实数据生命周期缺陷
- WorldV2Context 捕获检查点时只保存 Shapes/Liquids，恢复时使用 `new Vocabulary(checkpoint.Shapes, checkpoint.Liquids)`，丢失 World-owned 的 LiquidPolarity 映射。
- 根级 World V2 checkpoint wire DTO 也未序列化该映射；自定义液体身份或反序映射可能无法 JSON 往返/恢复。
- 现已贯通 WorkshopCapability → WorldV2Context → WorldV2ExecutionResourceCheckpoint → WorldV2ExecutionCheckpointWireDto → JSON encode/decode → restore 的显式映射。
- 恢复前同时校验词表与映射和当前目标 World 完全一致；不对未知身份、旧自定义映射或缺失映射猜测极性。
- checkpoint JSON schema 从 2 升为 3；旧 schema 2 不被静默解释为新格式。
- 新增 WorldV2FactoryTests：自定义 fuel/coolant 映射经过完整 checkpoint JSON round-trip 后仍保持 fuel→Anode、coolant→Cathode，并可恢复到同配置 Context。
- 更新所有手工构造 checkpoint 的测试夹具，使映射成为显式字段。

### 失败测试修正
- ProducerSlot fixture 改为合法二元液体世界，不再构造单液体 WorldOptions。
- B 类执行测试按生产枚举器既有 Slave-first 策略检查：Jar 先从 Master 转移，再由 Slave 执行 MATCH；Ban 仍只作用于游离液体 staging。
- 多 Jar handoff fault test 现在从 Transfer 阶段验证第二次物理移动失败时会话失败、Jar 身份回到 Master、Slave 为空、Mark 清理、Reservation 释放，且未提交 Transfer/Return 账目。

### 本轮提交与验证边界
- 相关连续提交：`b855198b94478be9bf9de5ef716b36d2a3832db7`、`9e5b34355e1df5a22e25988753f042e3a991aa68`、`2e059b27f8330d4903f9890d1917c1f6bb1cefcd`、`f943bf5591e7107ca51df3373a9bcf0beecd1533`、`5e676e14b969738fa6ec44c24588f2a5cafc2d0f`、`f8167a8430893c4e529e1e981c15d2e02b8bbba5`、`1e42ec26810e2abb551444494ab88dfd40812514`、`ec6218fec00587b8fe79118f76239488b04480cc`、`adcec987d927ef50ed2e0f50fa51b96b6c15b23b`、`ea8af5906af986c26a5ed4f0c9a76a7ac560b97a`、`9afabdefb9d989f91ec57600cbc2fae09c1babc8`、`bd6b1437ba454f367301f1b26699727bd5732e70`、`ee37b8595474c228039727fe573d823aad1d3a41`。
- head `ee37b8595474c228039727fe573d823aad1d3a41` 的 verify runs `45586817186` / `45586828250` 在记录时仍执行中；不得写为通过。
- Space PR #2 当前仍使用 sibling ProjectReference，但 cross-repo workflow 固定 pin 仍是旧 Core SHA `b342259fe04363386085dc9a4ca995112954ed43`。待 Core 精确 head 绿色后，必须将 Space workflow 的 Core checkout 和 `CORE_EXPECTED_SHA` 同步到该精确 head，再跑完整 Godot 4.7.1 cross-repo 验证。
- PR #63 继续 Draft/Open；Core main 不直接修改。旧 PR 仅保留关闭状态与历史分支，不删除历史证据。


## 2026-11-25 第十轮：首个完整绿色 Core head 与 World 单一词表约束

- Core head `0ff648ea7f3f05489f04c278f75039de8271985d` 的两个 verify runs `45587335108`、`45587347149` 均完成 SUCCESS；854 项 xUnit 全部通过，0 errors、0 failed、0 skipped。完整验证回路中的 build、World V2 Host、随机/容量/确定性资源样本、契约快照、schema 与配置世界等价检查均通过。
- 成功 head 之后继续审查公开的 `WorldV2Context.Create` 装配入口，发现它此前允许主/从/备用工作台分别携带不同的液体极性词表；这会导致同一个 Jar 在同一 World 内出现语义分裂。
- 现已在 Context 装配边界强制所有工作台的液体身份集合与极性映射完全一致，并要求 Context 的液体目录与工作台目录一致；新增反向映射冲突测试，验证装配 fail-closed。
- 最新 Core head 为 `fd0b3761babe3af7f7d770483004ef1a454021f8`，对应 verify runs `45587631031` / `45587642292` 在记录时仍执行中。前述 854/854 绿色不能替代此新 head 的精确验收。
- Space PR #2 的当前 head 为 `a8daee73df55bf30125960a9a6231ea93e706d4f`，旧 Core pin `b342259fe04363386085dc9a4ca995112954ed43` 的 cross-repo run `45577582987` 成功；该结果不验证本次新 Core。待 `fd0b...` 精确 Core head 绿色后，更新 Space workflow checkout 与 `CORE_EXPECTED_SHA` 并重跑。


## 2026-11-25 第十一轮：Core 精确源版本绿色并接入 Space 跨仓库验证

- Core 生产代码与测试 head `5023b38b75ad4cf66d953e3bb3fa4c490076d4d7` 的两个 verify runs `45588065611`、`45588076128` 均 SUCCESS。该 head 包含 854 项回归、World V2 单一词表/映射装配约束、checkpoint schema v3、自定义映射 checkpoint round-trip，以及旧 schema v2 缺少映射时的明确拒绝测试。
- Space PR #2 的 cross-repo workflow 已固定到 Core SHA `5023b38b75ad4cf66d953e3bb3fa4c490076d4d7`；checkout ref 与 `CORE_EXPECTED_SHA` 一致。
- Space PR #2 代码 head `a19a6afcf47d06bf500764407bad63e8f6bd5dde` + Core source `5023b38b75ad4cf66d953e3bb3fa4c490076d4d7` 的联合 run `45588639250` 已完成 SUCCESS。
- Core Bridge / Space .NET 两次 build succeeded；Godot 4.7.1 import/parse succeeded；HUD 21 checks / 0 failures；Hangar catalog/owned assembly/progression migration、domain contracts、parser ingress、World V2 runtime/lifecycle/fault/disposed/faulted commands/snapshot isolation、battle pause/navigation 全部 probe passed。
- 最终 marker：`CROSS-REPO-WORLD-V2-OK core=5023b38b75ad4cf66d953e3bb3fa4c490076d4d7`。日志中 overflow fault 与缺失主菜单资源为故意注入的负向测试，相关 probe 均以 passed 结束，非 CI 失败。
- 前一轮 Space run `45588471573` 在 Core source `fd0b3761babe3af7f7d770483004ef1a454021f8` 下也成功；最终验收以当前 `5023...` pin 的 run 为准。
- GitHub 当前仅保留两个 Open Draft PR：Jar Core #63 与 Space #2；所有已确认 superseded 的旧 PR 已关闭，历史分支保留作审计证据。


## 2026-11-25 第十二轮：Core 概念体系与规则正式化

### 起始状态
- Repository：Lazyair150/jar-system-dotnet
- Branch：architecture/core-liquid-identity-consolidation
- 本轮文档前起始 HEAD：4270dd3669a12cbc8cb15bfeaa4b0e41a702ecde
- PR #63：Open / Draft；未合并。
- 前一轮 Core/Space 精确集成证据为 Core 5023b38b75ad4cf66d953e3bb3fa4c490076d4d7 与 Space run 45588639250。本轮文档提交不自动继承旧 head 的 CI 结论。

### 本轮源码审查事实
- 已有：World-owned Vocabulary 与明确液体极性映射；RuleContribution / EffectiveRuleProfile；RuleContext；Allowed/Rejected/Undetermined；资源效果契约；候选 Profile、符号解与资源模拟等结构。
- 缺口 A：WorldOrderRuleCandidateCatalog.Build 仍以 Constants.SHAPES 初始化候选形状空间；候选生成尚未完全使用 World-owned shape vocabulary。
- 缺口 B：WorkshopRulebook 仍以 WorkshopOperationKind + DefinitionFor + 多处 switch 实现固定算子；这是受控内建规则，但还不是统一 Rule Registry/Interpreter 驱动的规则目录。
- 缺口 C：RuleContext.From(EffectiveRuleProfile) 仍以 RuleContributionEffect switch 解释贡献；Effect schema/参数默认值/unsupported handling 需要集中化、强类型化和版本化。
- 缺口 D：WorldOrderAClassBranchEnumerator / WorldOrderBClassBranchEnumerator 直接知道 Master/Slave、订单 A/B 与交接阶段；这些领域策略必须保留语义但从通用搜索算法移出。
- 缺口 E：候选操作字符串、岗位角色字符串和固定预算/上限散布在不同类；需区分协议身份、默认策略、安全护栏和真实玩法硬编码，不能简单删除全部常量。
- 缺口 F：WorldSnapshot 已区分 RuleSetVersion / WorkshopProfileVersion / ResourceVersion，但没有把 World shape vocabulary 作为候选枚举唯一输入显式传递；需补齐快照及其指纹。
- 缺口 G：已有结构候选与资源模拟，但完整 SolveProblem / policy / proof-completeness / dependency footprint 契约尚未成为所有 Solver 的唯一入口。

### 本轮架构裁决
- 新增 docs/architecture/core-conceptual-model-and-formal-rules.md，作为规范性目标架构。
- 新增 docs/decisions/0007-core-rule-system-and-solver-decoupling.md，确定 Rule Registry/Interpreter、SolveProblem、OrderPolicy、结构解与资源绑定分离方向。
- 保留电路板模型、Jar/液体资源语义、Ban staging scope、A/B 单向交接、Mark 生命周期与失败代价；不允许为通用化改写语义。
- 不允许任意动态代码插件成为持久化规则；声明式贡献由 Core 受控解释器执行，未知规则 fail-closed/Undetermined。
- 不允许把算法预算、候选顺序或启发式变成玩法规则。

### 本轮变更文件
- docs/architecture/core-conceptual-model-and-formal-rules.md
- docs/decisions/0007-core-rule-system-and-solver-decoupling.md
- docs/work-records/README.md
- docs/architecture/2026-11-core-multi-angle-audit.md
- docs/work-records/2026-11-25-core-consolidation-audit.md

### 验证
- 本轮为文档/架构决策提交；没有宣称生产代码已完成去硬编码。
- 本轮精确新 commit 的 CI 尚待执行。
- 后续代码阶段必须验证 rename-isomorphism、polarity swap、multi-world isolation、unsupported fail-closed、solver differential、resource conservation、Ban scope、Mark lifecycle、admission atomicity、replay、checkpoint。

### 下一步
1. 将 World shape/liquid vocabulary 作为 SolveProblem / WorldSnapshot 显式输入，移除候选生成对 Constants.SHAPES 的依赖。
2. 盘点固定算子字符串、角色字符串和 RuleContributionEffect 分支，建立唯一注册/解释入口。
3. 将 A/B 与 Master/Slave 迁移为显式订单/交接策略，先保持旧实现对拍，不一次性替换。
4. 将统一规则 evaluator 的结果贯穿候选生成、资源模拟、Admission 与 Executor。
5. 完成后再做多算法对拍与大规模固定 Seed 资源守恒样本。


## 2026-11-25 第十三轮：World Vocabulary 贯穿快照、规则上下文与候选求解

### 起始与结果
- 本轮最终代码 head：b11d9a51f7e8a18df3bb0ff3545d1af949dffa12
- 两条精确 head 验证：45592984647 SUCCESS、45592974637 SUCCESS。
- xUnit：861 total / 0 errors / 0 failed / 0 skipped。
- 资源样本：2,000 random + 3,000 capacity + 32 deterministic，0 bugs。
- Release build、contract/schema、配置等价验证均通过。

### 实施内容
1. Workshop 增加只读 KnownShapes，直接暴露所属不可变 Vocabulary 的形状目录。
2. WorkshopSolveSnapshot 捕获 Shapes；WorldSnapshot 的 WorkshopProfile fingerprint 纳入 shape directory。
3. WorldSnapshotFactory.Capture 检查同一求解 World 内所有 Workshop 的 shape directory、liquid identity set 与 polarity mapping 一致。
4. WorldOrderRuleCandidateCatalog 不再从 Constants.SHAPES 初始化候选空间，改用 workshop snapshot 的 Shapes。
5. WorkshopCapability 暴露其不可变 Vocabulary；RuleContext.From 增加 Vocabulary 注入入口。
6. WorkshopSolveSnapshot.CreateRuleContext / CreateWorkshopInfo 将所属 World Vocabulary 传入 RuleContext，确保 rules.Liquids 与 ResolvePolarity 不会退回 Vocabulary.Default。
7. WorldV2Context.Create 默认从主工作台 World Vocabulary 获取 shapes/liquids，不再从 Producer Generator / LiquidProduction rate key 推断目录；并验证所有工作台共享形状目录、液体身份与映射。
8. 新增测试：自定义 shape snapshot capture、不同 shape vocabulary 装配拒绝、World V2 默认词表来源、World V2 shape vocabulary 不一致拒绝、候选生成使用自定义 shape/liquid/polarity。

### CI 失败与修复留痕
- run 45591775950 / head a2a61d5：860 项中 1 项失败，原因是新增 shape vocabulary 约束后原有液体映射测试的诊断文本断言不再匹配。保留原液体诊断子串并扩展形状说明，修复于 92a72c49df209294dfeae33727f35000af78b788；对应 runs 45592220260 / 45592233519 SUCCESS，860/860。
- run 45592611139 / head d0dc53a：测试代码缺少 Abstractions / World.Solving 命名空间导入，构建失败；不是生产代码错误。依次补齐 using，最终 b11d9a5 精确 head 两条 CI 均通过。
- 以上失败均保留，不以绿色提交覆盖失败历史。

### 本轮完成边界
- 已消除求解候选对默认 shape list 的直接依赖，并修复自定义 World Vocabulary 在 RuleContext 重建时丢失的问题。
- 尚未完成：所有 Core 生产路径对默认 shape/liquid 常量的全量扫描；RuleContribution Effect schema 的严格验证；统一 Rule Registry/Interpreter；候选算子注册化；A/B 与 Master/Slave 策略解耦；通用 SolveProblem。
- 本轮不是“所有硬编码已消除”的声明。

### 下一步
1. 全量盘点 RuleContribution Effect/参数 fallback，未知 Effect 与非法参数必须 fail-closed；ModifyCapacity 等当前不支持的规则必须保持 Undetermined。
2. 将规则候选的固定操作列表从 WorldOrderRuleCandidateCatalog 拆成有稳定身份/版本的内建 Rule Operator Provider Registry。
3. 将 A/B 与 Master/Slave 交接差异抽为 OrderPolicy / WorldTransferPolicy，保留原行为并对拍。
4. 将 StructuralSolution 与 ResourceBinding/Readiness/Admission 的 fingerprint 依赖完整分离。


## 2026-11-25 第十四轮：RuleContribution Schema Registry 与 fail-closed 规则投影

### 当前代码源
- Code head：0989a2bf28ae1e2d106629ade44aef78f6f2cf93
- PR #63：Open / Draft / unmerged。
- 精确代码 head 0989a2bf28ae1e2d106629ade44aef78f6f2cf93 的 runs 45595570171、45595555177 均 SUCCESS。
- 最近已完整绿色的 Vocabulary head：b11d9a51f7e8a18df3bb0ff3545d1af949dffa12，runs 45592984647 / 45592974637，861/861。

### 代码变更
- 新增 RuleContributionSchemaRegistry 与 RuleContributionEffectSchema，集中定义各 Effect 的 required/optional parameter、静态求解支持能力和参数格式验证。
- RuleContribution.Validate 在 Profile 装配时调用 schema registry；未知 Effect、未知参数、缺失必填参数、非法 ticksPerStep/factorPerMille、错误时间窗口均 fail-closed。
- RuleContext 删除对应参数默认值回退；已验证参数缺失时不再静默替换成默认语义。
- RuleContext 对 GrantBaseMark / BanLiquid 明确标记为已投影进 WorkshopCapability 的 Effect；ModifyCapacity 保留但声明为当前静态 Solver 不支持，返回 Undetermined；未知 Effect 有显式异常保护。
- HasUnresolvableRequirements 根据 Effect Schema 的 SupportsStaticSolverProjection 判断，而不是在 Solver 中硬编码单个 Effect 名称。
- 更新 EffectiveRuleProfileTests 的旧 fixture：GrantAdditionalMark 的目标形状属于 Target，不应重复塞入未定义的 shape 参数；SourceRevision 测试只改变 revision，不伪造不符合 Effect schema 的 strength 参数。
- 新增未知 Effect、Retime 缺参/非法数值、Intensity 负值测试。

### 验证结果与失败修复留痕
- head 320b50eb17ea246ed2cb0911c5dcf16a1eedf352：两条 verify runs 45594046566 / 45594033707 失败。最初实现漏掉了 GrantBaseMark / BanLiquid 已投影到 Capability 的显式 switch case，造成 174 项测试失败；在 dad8488997c640c46e2f96875165db1abd03c31c 增加显式 no-op projection case。
- head dad8488997c640c46e2f96875165db1abd03c31c：两条 runs 45594961966 / 45594975018 失败，主要集中在 EffectiveRuleProfileTests 的 6 项 fixture 不符合新 schema；随后移除 helper 中冗余 shape 参数，并让 SourceRevision 测试只改变 revision。
- head 3c3b68a3e9c2c0b096b164da3b3905d1eb30f13c 还修复了 schema collection 返回类型与残留 Parameter fallback 调用；对应 runs 45594448093 / 45594458923 仍失败，原因见前述规则投影/fixture问题。
- head 0989a2bf28ae1e2d106629ade44aef78f6f2cf93 是当前修复后代码源；两条精确 head CI 均 SUCCESS：864 total / 0 errors / 0 failed / 0 skipped，2,000 random + 3,000 capacity + 32 deterministic 资源样本均 0 bugs，Build succeeded，contract/schema 检查通过。

### 尚未完成
- RuleContributionSchemaRegistry 的 schema/interpreter version 尚需纳入 EffectiveRuleProfile 与 World RuleSet fingerprint。
- RuleId/version 尚未形成统一 RuleDefinition Registry；Effect schema 只是参数契约，不等于完整规则目录。
- WorkshopRulebook 固定操作 switch、WorldOrderRuleCandidateCatalog 固定候选操作集合、A/B Branch Enumerator 的岗位/流程耦合仍待拆分。
- 下一步先等待 0989 精确 CI，修复所有失败；随后完成 schema version fingerprint，再建立 RuleOperator Provider Registry 与 OrderPolicy。


### 第十四轮验收补记
- 0989a2bf28ae1e2d106629ade44aef78f6f2cf93 精确代码 head 已由 runs 45595570171 与 45595555177 双绿验证。
- xUnit 864/864；资源守恒样本 5,032 组无错误；Core build 成功。
- 本次记录提交只更新工作记录/架构审查/规范状态，不改变生产代码。后续精确验证以该代码 head 为依据。


## 2026-11-25 第十五轮：规则 Schema 版本进入语义指纹

- 代码 head：d9f9dd4b18666dd9fdb2da75b27e804c6a89e0ed。
- RuleContributionSchemaRegistry 新增 CurrentVersion；该版本表示 Effect 参数 schema 与投影语义契约版本，任何兼容性/解释语义变更必须递增。
- EffectiveRuleProfile fingerprint 同时纳入 Profile schema version 与 RuleContributionSchemaRegistry.CurrentVersion。
- WorldSnapshot RuleSetVersion 纳入 RuleContributionSchemaRegistry.CurrentVersion，确保 schema/interpreter 语义变化使结构候选缓存失效。
- 新增测试：所有 RuleContributionEffect 必须有 schema；ModifyCapacity 明确不支持静态 Solver 投影；该 Effect 必须保留为 unresolvable，而不是被静默忽略。
- 精确代码 head d9f9dd4b18666dd9fdb2da75b27e804c6a89e0ed 的 runs 45596511987 / 45596522515 均 SUCCESS：866/866 xUnit、0 errors / 0 failures / 0 skipped；2,000 random + 3,000 capacity + 32 deterministic 资源样本 0 bugs；Core Build succeeded。
- 下一步：将固定 Jar 候选操作从 Catalog 中抽为版本化 RuleOperator Provider Registry，再将 A/B 与岗位交接流程迁移为 OrderPolicy。


### 第十五轮验收补记
- RuleContribution Schema Version 指纹代码 head d9f9dd4b18666dd9fdb2da75b27e804c6a89e0ed 两条精确验证 45596511987 / 45596522515 均 SUCCESS。
- xUnit 866/866；资源样本 5,032 组无错误；Core build 成功。
- 最新工作记录更新为文档提交；代码验证证据对应 d9f9dd4b18666dd9fdb2da75b27e804c6a89e0ed。


## 2026-11-25 第十六轮：候选算子 Provider Registry 化

### 代码源与验证
- 代码 head：18a0b26407ee51c6df722f093165c3b3770c31a7。
- 精确 runs：45597874582 SUCCESS、45597884119 SUCCESS。
- xUnit：868 total / 0 errors / 0 failed / 0 skipped。
- 资源样本：2,000 random + 3,000 capacity + 32 deterministic，0 bugs；Core build succeeded。

### 实施
- 新增 IWorldOrderRuleCandidateProvider、WorldOrderRuleCandidateProviderRegistry。
- 将 MATCH、POUR_IN、POUR_OUT、RESHAPE_EMPTY、RESHAPE_FULL、PREPARE_POUR_OUT 拆为独立 Provider。
- WorldOrderRuleCandidateCatalog 只负责遍历来源形状、需求和注册的 Providers，不再直接持有每种算子的候选生成分支。
- 新增 WorldOrderRuleCandidateFactory，所有 Provider 仍必须通过统一 WorkshopRulebook 和 staging/admission 规则进行候选评估，不能自行伪造合法转移。
- Provider 的 ProviderId、Version、Order 进入 Registry fingerprint；Registry fingerprint 进入 specification fingerprint 与 CandidateBook cache key。
- WorldOrderRuleCandidateBook 支持注入 immutable registry；默认使用 Core 内建注册表。
- 新增重复 ProviderId 拒绝、Provider 版本/顺序改变会改变候选规格指纹的测试。
- 保留旧候选顺序：来源形状优先，其次 MATCH / POUR_IN / POUR_OUT / RESHAPE_EMPTY / RESHAPE_FULL；预备 POUR_OUT 单独生成。

### 验证失败与修复
- 初版 head d8da461a79eaa3d6f74e99ce24b9089186862d1b 的两条 CI 45597443073 / 45597455088 失败，失败点为新测试构造自定义 Vocabulary 时没有提供非默认液体的极性映射；这是测试夹具错误，不是 Provider 实现错误。
- 为测试补齐 fuel=Anode / coolant=Cathode 映射后，head 18a0b26407ee51c6df722f093165c3b3770c31a7 两条精确 CI 均 SUCCESS。

### 尚未完成
- Provider 是可信 Core 算法扩展，不是可持久化的任意组件代码；组件 RuleContribution 仍只能声明式配置已知 Effect。
- WorkshopRulebook 仍持有基础操作解释逻辑；下一步需把规则本体表达为 versioned RuleDefinition / RuleProgram，Candidate Provider 应逐步从手写操作循环改为消费规则声明的 predecessor projection。
- A/B Branch Enumerator 仍分别绑定 Master/Slave 流程；下一步是 OrderPolicy / RolePlan 解耦。


## 2026-11-25 第十七轮：Jar 个体、区域接口与自机电路通路

### 目标
将用户最新确认的世界模型正式化：工作台作为独立个体拥有液体暂存区和 Jar 存储区；自机电路板包含生产区、工作台区、订单区和独立自机液体存储区；各资源区域通过输入/输出接口连接；固定配置形成从资源源端到订单目标端的通路。

### 起始代码源
- Repository：Lazyair150/jar-system-dotnet
- Branch：architecture/core-liquid-identity-consolidation
- 起始 HEAD：dcb94676ec9c9564440161cb2222753f75046faa
- PR #63：Open / Draft；当前分支不是已合并主线。
- 起始 HEAD 精确 CI：45598397712、45598387846 均 SUCCESS。

### 概念裁决
- Workshop Owned Instance 独立拥有 Jar Storage 与 Liquid Staging 两个区域；主台/从台是 Board Slot Role，不是 Workshop 固有身份。
- Ship/Circuit Board 拥有 Production / Workshop / Order 三类结构区域；Ship Liquid Inventory 是 Ship 自己的资源账户，不属于工作台。
- Area、Port、Connection、Route 分离：Area 定义资源/职责边界；Port 定义有类型输入/输出；Connection 显式连接 Output -> Input；Route 是连接与规则转换构成的有界图路径。
- 订单源边界为 Ship Liquid Inventory Output 和进入 Master Workshop Jar Storage 的 Jar Input；订单需求/交付端为 Route Tail。
- 固定的是冻结结构、实例成长快照、端口连接和规则版本产生的 Circuit Image；订单目标与资源余额用于求具体路径/Readiness，不能将“固定”误解成单条硬编码线性步骤。
- Production Area 只有在生产元件显式声明输入/输出、并存在连接与接收准入后才进入资源通路；不能与工作台账户隐式共享。
- 资源转移必须经过源 Output、目标 Input、规则判定和 ResourceEffect/Receipt；任何账户不允许跨边界直接写入。

### 源码核对
- Workshop.cs：已有独立 _jars、_liquidStorage、只读视图和 AddJar/RemoveJar/TransferOut/TransferIn、AddLiquid/RemoveLiquid/TryReceiveLiquid/DrainLiquid 等领域方法。
- WorldSnapshot.cs：已区分 ShipLiquidInventory、Master/Slave Workshop SolveSnapshot 和 ResourceVersion。
- WorkshopRulebook.cs：现有操作规则仍是明确的 Jar 转换语义，不应为了抽象而删除。
- 当前未完成：统一 Area/Port 类型、端口注册与连接、Board Production/Workshop/Order 通用区域拓扑、Circuit Image Compiler、所有跨区转移统一端口化。

### 改动文件
- 新增 docs/world-v2/jar-individual-area-port-and-circuit-route-model.md。
- 更新 docs/world-v2/circuit-system-model-and-bounded-resource-routing.md，补充基础个体/端口模型入口。
- 更新 docs/world-v2/README.md，加入规范索引。
- 更新 docs/architecture/core-conceptual-model-and-formal-rules.md，新增第 20 节。
- 更新 docs/architecture/2026-11-core-multi-angle-audit.md，追加第十七轮审查。
- 更新本工作记录，保留精确源码起点与验证证据。

### 验证
- 文档/源码映射核对：Workshop 区域与 API、WorldSnapshot Ship Liquid Inventory、Board/Owned Instance 概念已逐项对照。
- 本次仅新增/更新文档，无生产代码改动；文档提交会触发仓库 CI，需以新提交精确 head 的 run 结果作为本轮验证，不以起始 head 绿色代替。
- 尚未执行统一 Area/Port API 生产实现；不宣称接口抽象已落地。

### 下一步
1. 定义 Area/Port/Connection/Transfer Receipt 的 Core 最小类型，不先重写 Workshop。
2. 为 Workshop Jar Storage、Liquid Staging、Ship Liquid Inventory 建三个 Port Adapter，并以旧领域方法做差分测试。
3. 将 Board Production/Workshop/Order 区域及 Slot Assignment 映射到不可变 Circuit Image。
4. 再把订单结构解从现有 A/B 策略中抽出，形成通用多资源端口路径求解。


### 第十七轮源码深挖补记：液体账户不能被抽象合并

进一步核对 LiquidResourceAccounts.cs、ResourceArea.cs、OrderDemandBoard.cs、OrderDemandArea.cs 与 order-funnel contract，发现“工作台液体区”在现有实现中并非单一无差别余额：
- Workshop 普通游离液体库存按液体身份计数，容量语义为每液体 MaxLiquids。
- WorkshopLiquidStaging 是订单范围暂存，容量为工作台聚合容量，内部按 OrderId 隔离，并有独立主台预置余额/容量预留。
- ShipLiquidInventory 是自机账户，生产、扣款、返还与预置转移具有独立收据/事务。
- Jar 内部液体又是 Jar 个体账户。
因此 Area 是所有权与访问边界；Area 内可以有多个具有不同生命周期和容量语义的账户/子作用域。端口不能把这些账户合并为“工作台总液体”。

同时核对到 ResourceArea 已组合 Jar Producer/Warehouse、LiquidProduction 和 ShipLiquidInventory；Jar 生产入仓库后按流率进入主台，液体生产进入自机液库，不自动灌入工作台。OrderDemandBoard/OrderDemandArea 管理主动/被动需求归属、顺序、成功保留策略和冷却，不负责求解/执行。这些既有边界必须被未来 Circuit Image 显式引用，而不是重造第二份资源状态。


### 第十七轮架构裁决补记：ADR 0008

新增 ADR 0008，正式决定 Area/Port/Connection/Route 的职责、资源账户不可合并规则、端口预检与跨账户原子转移协议，以及 Circuit Image / Solver / Binder / Admission / Executor 的依赖边界。

特别明确：CanReceive / CanWithdraw 是纯预检；跨区域提交必须由 Transfer Coordinator 同时验证源 Output 与目标 Input，不能让调用方分别调用两个独立写接口造成半提交。部分接收必须有明确保留/丢弃结果，版本冲突不得重复扣款。



### 第十七轮跨仓库映射补记：Space Board 区域标识待裁决

检查 Space PR #2 分支 world-v2/phase-4.10b-hangar-ui，当前 branch head 8b70979d5f56f559f69f55842790b520236a5aa。SpaceHangarBoardSlotDefinition 的 area_id 枚举包含 workshop、decomposition、orders、production；SpaceHangarBoardDefinition 的 runtime projection 要求一个 ore_parser 槽位，demo_board.tres 将其放在 decomposition，而 production_device 放在 production。

用户最新概念确认的是生产区、工作台区、订单区三个顶层区域。当前实现把 decomposition 与 production 分成两个 area_id，不能擅自认定二者是同一层级，也不能擅自增加第四个领域区域。需由用户裁决：decomposition 是否作为 Production Area 的子区域（建议保留 parser 专属子区域身份，但挂在生产区下），还是维持独立第四区域。裁决前不改 Space schema、不做破坏性重命名。

其他映射事实：
- SpaceHangarBoardDefinition 是结构蓝图；SpaceHangarBoardAssignment 是玩家的安装映射，校验 Owned Instance 唯一性、Definition 版本和 progression revision。
- SpaceBoardRuntimeProfileSet 是按组件类别将冻结 assignment 路由给 Core Workshop、Space Parser、Space Skill 等消费者的 profile manifest；其源码明确不是规则解释器。
- demo_board.tres 当前有 Master/Slave Workshop、Parser、Active/Passive Order slots；未配置 production_device slot。


## 2026-11-25 第十八轮：区域层级裁决与最小拓扑契约

### 用户确认
- 用户明确认可：原石分解区属于生产区的功能子区域，保留独立身份，但不是第四个顶层区域。
- Circuit Board 顶层区域固定为 Production / Workshop / Order。
- 原石解析器与生产设备仍是不同组件类型、独立拥有实例和独立功能；层级归属不意味着合并其规则或库存。

### GitHub 起始状态
- Core PR #63：`architecture/core-liquid-identity-consolidation`，本轮读取时 HEAD 为 `a11f14d36ae2ca68d2f56a93e23600ed9e5e6c22`，Open / Draft。
- Space PR #2：`world-v2/phase-4.10b-hangar-ui`，本轮读取时 HEAD 为 `8b70979d5f56f559f69f55842790b520236a5aa4`，Open / Draft。
- 两个 PR 均未合并；本轮提交均直接进入各自现有工作分支。

### Core 最小结构契约
- 新增 `CircuitTopology.cs`：`CircuitAreaDefinition`、`CircuitPortDefinition`、`CircuitConnectionDefinition`、`CircuitTopology`。
- Area 明确 AreaId、OwnerIndividualId、ParentAreaId、AreaKindId 与是否持有资源。
- Port 明确方向、Jar/Liquid 资源类别、身份约束、策略身份与版本。
- Connection 只允许 Output -> Input，校验资源类别一致，并在双方都有限定身份时要求身份集合存在交集。
- CircuitTopology 校验 Area/Port/Connection 身份唯一、端口 Area 引用有效、嵌套 Area 父级存在且属于同一 Individual、父级无环。
- 该模型是冻结结构元数据，不复制 Workshop、Jar、Warehouse、ShipLiquidInventory 的权威余额；Connection 不表示资源已经转移。
- 新增 `CircuitTopologyTests.cs`，覆盖生产区包含分解子区、父级所有权、父级环、端口方向/资源类别、身份约束不相交。
- 仍未实现运行时 Port Adapter、Transfer Coordinator 或账户原子提交；不能将结构契约误报为资源转移已端口化。

### Space Board 层级迁移
- `SpaceHangarBoardSlotDefinition.gd`：顶层 `area_id` 收敛为 workshop/orders/production，新增 `sub_area_id`（当前 none/decomposition）。
- Parser 槽位改为 production/decomposition；Production Device 为 production/none。
- `SpaceHangarBoardDefinition.gd` 的 slot index 与 frozen snapshot 同时保留 areaId/subAreaId。
- `SpaceHangarBoardAssignment.gd` 将 areaId/subAreaId 纳入精确安装记录与验证，防止 assignment 与 Board 层级不一致。
- `demo_board.tres` 的原石解析器迁移为生产区的分解子区域。
- `SpaceHangarBoardDefinitionProbe.gd` 增加冻结快照层级、生产区槽位归属、错误 Area tampering 和多 Parser 子区域夹具检查。

### 文档同步
- 更新 `docs/world-v2/jar-individual-area-port-and-circuit-route-model.md`，删除“等待用户裁决”的旧文字，正式记录三层级裁决。
- 更新 ADR 0008，补充顶层区域与功能子区域决策。
- 更新 Core 概念模型，记录 Board Area 层级。
- 本工作记录保留本轮起始精确 HEAD 与迁移范围。

### 验证边界
- 本轮代码/文档已写入 GitHub 分支。
- 尚未取得本轮最新 Core/Space 提交的 CI 结果；不得把此前提交的绿色验证套用到本轮。
- Space 侧还需确认所有运行时投影与展示消费者均读取新的 areaId/subAreaId；Probe 已更新但未执行。
- Core 新拓扑契约需在精确新 HEAD 上完成 Release Build 与 xUnit；随后再开始 Workshop/Ship 的只读 Port Adapter 和差分测试。

### 下一步
1. 审查本轮 Space 修改的所有消费者，确保冻结 Board/Assignment 指纹包含 areaId/subAreaId，且旧 decomposition 顶层值不再进入生产投影。
2. 在 Core 新 HEAD 上运行 CI，修复编译/测试问题。
3. 为 Workshop Jar Storage、Workshop Liquid Resource Area、Ship Liquid Inventory 建只读 Port Adapter；每个 Adapter 仅调用现有权威领域方法做预检，不直接暴露容器。
4. 明确 Transfer Request / Preflight / Receipt 的纯值契约，再实现事务协调器；不在本轮把独立 TryReceive/TryWithdraw 伪装成原子跨区转移。


### 第十八轮精确验证补记

#### Core
- 最终代码/文档 HEAD：`8df3de5e8ba06012cadaa2c6288a4b7f3941d8a3`。
- GitHub Actions run `45612299720`：SUCCESS。
- Release/验证构建：Build succeeded，0 Error(s)。
- xUnit：873 total / 0 errors / 0 failed / 0 skipped。
- 本轮新增的 CircuitTopology 测试已包含在该精确 HEAD 的 873 项测试中。

#### Space
- 本轮最终 Space HEAD：`8ad262ab8e42801af1e782c29becb59b5ce65bc6`。
- GitHub Actions cross-repository integration run `45613042211`：SUCCESS。
- Core Bridge 与 Space .NET 构建、Godot 4.7.1 项目导入/GDScript 解析、集成 Probe 均通过。
- SpaceHangarBoardDefinitionProbe 通过：区域投影、多重性、默认装配、Board Assignment、冻结快照、Owned Assembly、篡改拒绝、Parser 规则隔离、Profile 路由。
- 失败修复留痕：
  1. run `45612522311` 发现 OwnedLoadoutAssembler 仍只接受 Board Assignment schema v1；已升级为同时验证 v1 兼容与 v2 层级契约。
  2. run `45612793454` 发现 GDScript 第二个循环作用域错误引用前一循环的 area_id/sub_area_id；改为从当前冻结 Slot 读取后修复。
  3. run `45613042211` 对最终精确 HEAD 完整通过。
- 日志中出现的若干预期 ERROR 来自负向/拒绝路径 Probe（例如无效资源、篡改快照、缺失场景等），工作流结论为 SUCCESS，不能将这些测试日志误报为 CI 失败。

#### 本轮完成边界更新
- 顶层区域/子区域层级已进入 Core 结构契约和 Space 冻结 Board/Assignment/Runtime Profile 路由。
- Area/Port/Connection 是 Core 的结构元数据契约；尚未建立资源账户运行时 Adapter，也尚未建立跨账户原子 Transfer Coordinator。
- 通过的自动化验证不等于真实 GUI 试玩验收；Space PR #2 仍为 Draft/Open，Core PR #63 仍为 Draft/Open，均未合并。


## 2026-11-25 第十九轮：端口预检与资源转移审计契约

### 新增 Core 纯值契约
- 新增 `ResourcePortTransferContracts.cs`：
  - `ResourcePortPreflight`：单端口 Allowed / Rejected / Undetermined 预检结果，携带稳定 ReasonCode、权威账户、资源身份、数量与观察版本。
  - `ResourceTransferRequest`：明确 OperationId、ConnectionId、源/目标端口、资源类别/身份/数量、源/目标权威账户及预期版本。
  - `ResourceTransferReceipt`：明确 Transferred / PartiallyTransferred / Rejected / Undetermined，并记录 Transferred、RetainedAtSource、Discarded 三种数量。
- 强制数量守恒：Requested = Transferred + RetainedAtSource + Discarded。
- Rejected / Undetermined 必须完整保留源资源且两端版本不变。
- 新增 `ResourcePortTransferContractTests.cs`，覆盖跨端口/账户、拒绝保留、部分接收守恒和 Undetermined fail-closed。

### 边界
- 以上为纯值协议契约，不执行资源扣款/收款。
- Transfer Coordinator 尚未实现；不允许通过分别调用两端独立写接口冒充原子转移。
- 下一步必须先实现并验证现有账户的只读 Port Adapter，再设计适配现有事务能力的提交/回滚参与者。

### 文档
- 更新 `jar-individual-area-port-and-circuit-route-model.md`，正式记录 Preflight / Request / Receipt 的三段协议及守恒式。


### 第十九轮版本令牌修订
- 审查现有 Workshop、ShipLiquidInventory 与订单暂存实现后确认：现有权威账户没有统一的单调整数 Area Version。
- 因此新 Transfer Request / Preflight / Receipt 使用不透明版本令牌字符串，而不伪造一个当前账户并不存在的整数版本计数器。
- 令牌后续由账户 Adapter 根据权威账户纯值快照产生，并在提交前重新计算/比较；静态策略版本仍由 Port Definition 单独携带。
- 已同步更新 `ResourcePortTransferContracts.cs` 与测试夹具；账户版本冲突的运行时处理仍待 Adapter/Coordinator 阶段实现。


### 第十九轮端口约束补记
- `CircuitPortDefinition` 增加可选 `MaxAmountPerTransfer` 与 `MaxAmountPerLogicalTimeMilli`，分别表达单次传输上限和逻辑时间吞吐上限；未配置表示不由该端口施加对应上限。
- 负值配置在构造边界拒绝；零值可用于表达当前关闭的端口能力。
- `CircuitTopologyTests` 增加端口吞吐契约测试。
- 当前 Core 最新代码 HEAD：`476956eea99c96dc4fc567ed3073da703acf2d80`；精确 CI 正在执行，结果待补。

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

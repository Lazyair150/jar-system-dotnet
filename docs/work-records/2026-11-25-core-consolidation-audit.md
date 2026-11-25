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

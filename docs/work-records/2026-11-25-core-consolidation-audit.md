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

# ADR 0007：Core 规则系统正式化与求解算法解耦

- 状态：Accepted as target architecture; implementation is incremental
- 日期：2026-11-25
- 范围：Jar Core / World V2

## 背景
Core 已具备 World-owned Vocabulary、RuleContribution、EffectiveRuleProfile、RuleContext、统一三态 RuleEvaluation、符号候选、资源模拟、资源绑定和执行检查点等基础。但实现仍存在固定算子 enum/switch、默认形状目录进入候选生成、A/B Enumerator 直接编码岗位角色和流程、贡献 Effect 解释散落等结构耦合。

简单把 if 换成注册表，或把流程抽象成一个大规则类，都不能解决问题。

## 决策
1. 以 docs/architecture/core-conceptual-model-and-formal-rules.md 作为规范性目标架构。
2. World 是词表、极性、规则板、资源账户、运行状态和版本的唯一隔离边界。
3. Rule Definition/Contribution 必须声明身份、版本、参数 schema、作用域、依赖、前置条件、转换、资源效果、失败策略和求解支持能力。
4. Rule evaluation 统一返回 Allowed / Rejected / Undetermined；允许结果必须携带可执行转换；未知或不支持规则不得静默跳过。
5. Solver 只消费冻结的 SolveProblem 与规则评估接口；算法选择、候选顺序、搜索预算与剪枝策略不得定义领域语义。
6. A/B 订单差异与 Master/Slave 单向交接语义保留，但迁移为显式 OrderPolicy / WorldTransferPolicy 输入，不允许继续把流程固化在通用搜索算法中。
7. Structural Solution 与 Resource Binding/Readiness/Admission 分离；活动执行 Plan/Manifest/Lease/Cursor 仍需持久化。
8. 资源账户、Jar 内液体、staging、订单暂存、Ship/Warehouse 账目必须分离；Ban 只按明确账户作用域生效。
9. 不支持的 RuleId/Effect/Version、opaque 条件、版本不一致均 fail-closed 或返回 Undetermined，不静默忽略。
10. 迁移采用小步可回滚、精确 head CI、差分测试与资源守恒样本，不直接推 main。

## 影响
- 现有四种 Jar 转换与 MATCH 保留为 Core 内建规则，不再让候选枚举、Solver、Workshop、Executor 各自硬编码语义。
- 默认酒/蜜与默认形状仅是 Default World 内容，不是算法输入的权威来源。
- 现有 A/B 规则作为兼容策略实现，直到策略接口与行为等价测试完整后才可移除旧路径。
- 新增规则必须增加契约测试、来源/版本指纹测试、求解支持声明和失败语义测试。

## 拒绝的替代方案
- 所有规则动态执行任意插件代码：不可安全序列化、不可稳定回放、难以验证依赖，拒绝。
- 只建 RuleId 字符串注册表，仍在调用方 switch：只是转移硬编码位置，拒绝。
- 删除 A/B 与 Master/Slave 语义：改变既定玩法，拒绝。
- 合并所有资源余额为一个液体字典：破坏账户所有权与 Ban 语义，拒绝。
- 把搜索预算当作玩法规则：混淆算法资源与领域合法性，拒绝。

## 验收
按目标架构 Phase 0–7 执行。每阶段必须在工作记录中列出精确 commit、改动文件、测试、CI run、未完成项。只有所有强制验证矩阵通过后，才可宣称完成。

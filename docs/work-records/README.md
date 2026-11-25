# 工作记录专区

本目录保存可追溯的工程工作记录，不替代架构规范、系统分析、ADR 或测试报告。

## 规则

- 文件名使用 `YYYY-MM-DD-主题.md`。
- 每条记录必须包含：目标、起始仓库/分支/commit、改动文件、决策与理由、验证命令/CI run、精确结果、未完成事项。
- 区分源码事实、测试证据、推断与待验证事项。
- 记录失败 CI 与修复，不只保留绿色结果。
- 记录不可覆盖旧结论；状态变化通过新记录追加，并在索引中更新最新状态。
- 不记录 token、密钥、私人数据或本机绝对路径中的敏感内容。
- 真实代码/契约/测试文件是事实源，工作记录只是导航与审计轨迹。
- 本目录内容必须纳入版本控制；生成日志、临时 dump 放在 `docs/work-records/_local/` 并由根 `.gitignore` 忽略。

## 当前索引

- [2026-11-25 Core 多角度审查与 Space 收敛启动记录](2026-11-25-core-consolidation-audit.md)
- [Core 多角度架构审查与收敛方案](../architecture/2026-11-core-multi-angle-audit.md)
- [Core 概念模型与形式化规则规范](../architecture/core-conceptual-model-and-formal-rules.md)
- [ADR 0007：规则系统正式化与求解算法解耦](../decisions/0007-core-rule-system-and-solver-decoupling.md)
- [2026-11-25 Core 概念体系与规则正式化工作记录](2026-11-25-core-consolidation-audit.md)

## 当前整合分支

`architecture/core-liquid-identity-consolidation`

不要把 Draft PR 当作已合并主线；不要把 Headless 验证当作 GUI 验收。

# World V2 文档索引与过期文件治理

> 更新：2026-11-24  
> 目的：区分当前规范、实现记录、历史快照与已废弃语义，避免旧文档继续指导新代码。

## 1. 当前规范入口（优先阅读）

以下文件描述当前目标模型和领域边界；代码变更若与其冲突，应先判断代码是否仍承载兼容责任，再修正并更新文档。

| 文档 | 职责 |
|---|---|
| [电路系统模型与有界资源通路](circuit-system-model-and-bounded-resource-routing.md) | 总体模型：Board、Component、Rule Algebra、Compiler、Solver、Runtime；Jar/液体资源流；订单反向求解与换台正向迁移 |
| [规则转换契约实施记录](rule-transition-contract-implementation-log.md) | 当前已实现的共享契约、Adapter、测试、未完成事项和验收清单 |
| [订单漏斗与工作台替换契约](order-funnel-and-workshop-replacement-contract.md) | A/B 订单约束、资源路径、工作台替换边界 |
| [Circuit Board 规则审计与修正指南](circuit-board-rule-world-audit-and-correction-guide.md) | Board Assignment、规则贡献、Core ingress 的身份与作用域约束 |
| [Parser Board Slot Runtime Boundary](parser-board-slot-runtime-boundary.md) | Parser 输入、处理、outbox 与 Core Warehouse 的资源边界 |
| [当前工作状态与未解决问题](2026-11-24-work-status-and-open-issues.txt) | 本轮实施状态、领域不变量、未解决问题清单、样本测试策略与后续执行顺序 |

## 2. 历史实现记录：保留，不作为最新规范

以下文件是阶段性交付记录，保留它们用于追溯提交、测试和设计演变。文件名中的阶段号不是当前完成度，也不意味着所有结论仍有效。

- `phase-1-time-and-resource-area.md`
- `phase-2-workshop-roles.md`
- `phase-3-demand-areas.md`
- `phase-4-*.md`
- `phase-4.10b-*.md`
- `phase-4.10c-*.md`
- `phase-4.11a-*.md`
- `phase-4.11b-*.md`

阅读历史文件时必须同时对照第 1 节的当前规范和当前分支代码。PR 编号、head SHA、CI 状态、旧字段和旧事务顺序都只代表当时的记录。

## 3. 已标记废弃的旧语义

### `docs/资源流入优先级与归属.md`

保留原文作为 2026-11-03 的历史设计记录，但已在文件顶部标记为废弃语义。旧文档中“B 类订单结束后从台资源全部回落主台”等结论不得再作为当前实现依据。当前主从资源流应按订单阶段契约和资源账户所有权处理。

### `phase-4-resource-lifecycle-and-master-prefill.md`

保留 PR #28 阶段的 Master Prefill 事务快照。其 PR 状态和阶段结论为历史信息，不能覆盖当前 Board Assignment、Owned Instance 和 Jar/液体账户模型。

## 4. 尚待逐项审查的旧文档

`docs/` 下其余早期中文设计文档与决策记录暂不批量删除。仅凭文件日期、名称或语言不能证明其内容已失效；其中可能仍包含有效的安全约束、领域决策或 UI 契约。

优先审查候选（目前只做状态标注，不批量删除）：

- `主体清单_工作台写入点.md`：代码路径审计，需复核其中旧模块路径和写入入口是否仍准确。
- `内核全景_表达器接入.md`：旧 Web 表达器/宿主说明，当前 Godot / Space 集成不能直接以此为运行时规范。
- `内核层次_规则与布置.md`：规则与布置分离的历史审计，核心原则仍有价值，但其中旧配置路径/数值需复核。
- `前后端交互协议.md`、`界面组件与交互规范.md`、`表现层框架.md`：旧 Web 表现层契约，保留为 Web 历史规范，不作为 Godot UI 规范。
- `步骤2_禁令语义变更_影响评估.md`：变更前影响评估，不是最终 Ban 语义；当前 Ban 语义以第 5 节和电路系统模型为准。
- `状态机与游戏架构模式吸收评估.md`、`生产模型与分层职责.md`、`系统多学科审查与架构演化_2026-11.md`、`表达器设计原则.md`：历史架构审查，逐项复核其结论是否被后续代码/World V2 替代。

后续按以下顺序治理：

1. 搜索代码、测试和当前规范对该文档的引用；
2. 对照当前领域不变量，标记有效段落、过期段落和未决段落；
3. 若整份文档已被新规范替代，顶部加 Deprecated / Historical 标识并链接替代文档；
4. 只有确认没有当前引用、没有独立历史价值且无恢复/迁移责任时，才删除文件；
5. 删除前先把仍有效的决策迁移到当前规范，避免“清理”造成规则丢失。

## 5. 当前领域语义不变量（优先于历史阶段记录）

- 电路中的“电流”就是 Jar 与液体资源本身，不是第三种资源；Jar 是离散、有身份、带形状/内部液体/Mark 状态的资源。
- 空 Jar = Neutral，酒 Jar = Cathode，蜜 Jar = Anode。工作台 Ban 派生其专注极性：Ban 酒为 Anode，Ban 蜜为 Cathode；无 Ban 保持兼容，两种都 Ban 时仅 Neutral 可操作。
- 工作台极性约束对 Jar 的实际操作（MATCH、POUR_IN、POUR_OUT、RESHAPE），不约束 Jar 的持有或主台→从台所有权转移。接收台可以持有自己暂时无法操作的 Jar。
- 游离液体暂存区另有 Ban/容量准入；Jar 内部液体自耗仍须通过工作台-Jar 极性操作许可。PourOut 的 Jar 操作与随后 staging 接收/丢弃是两个结算边界。
- B 类订单需求步骤可由主台或从台执行：主台步骤在单向 handoff 前，从台步骤在 handoff 后；主台完成的需求 Jar 仍进入 handoff，不要求从台再次操作。
- 主台游离液体可以按 B 类资源边界供从台使用，但主台 Ban 的液体不能跨台；从台初始库存不得作为订单资源来源。订单产出的游离液体先进入当前工作台 staging，拒收/超限部分丢弃。
- MATCH 的 Mark 属于订单生命周期状态；跨台 handoff 保留 Mark，订单成功/失败/取消终态只清理本订单参与的 Jar，不误清无关 Jar。
- 未实现的 RuleKind / Scope / Runtime projection 必须拒绝或 Undetermined，不得静默忽略；预算耗尽不等于无解。

历史文档中与以上不变量冲突的段落只作为设计演变证据，不再是当前规范。

## 6. 已完成的垃圾文件清理

- 删除 `文档归档/04_废弃/回答.txt`：空占位文件。
- 删除 `文档归档/04_废弃/说明.txt`：明确记载该目录只存放已失效的 `_test_diag.csx` 临时诊断脚本和空文件；递归仓库树确认该临时脚本已不存在，目录内无其他文件，因此该废弃目录现已清空。
- 保留 `文档归档/01_系统设计记录`、`02_历史过程`、`03_学习与练习`：它们是有内容的历史资料，不属于垃圾文件。
- 保留 `重构报告/` 中的 `verify.bat`、质量/安全门脚本、契约 baseline 和阶段报告；这些仍被当前验证流程或历史审计使用，不按“旧”删除。

## 7. 清理纪律

禁止按“旧文件多”进行大规模删除。阶段记录、差分 Oracle 依据、存档 schema、恢复契约和安全决策可能仍是必要证据。过期的是旧语义的规范效力，不一定是文件本身的历史价值。

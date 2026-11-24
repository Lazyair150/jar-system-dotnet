# Jar System

Jar System 是一个由规则驱动的资源状态转换内核。它将 Jar 与液体作为实际流动的资源：Jar 是携带形状、内部液体与生命周期状态的离散资源；液体区分 Jar 内部液体与工作台游离液体暂存账户。规则决定资源如何通过工作台、订单和其他元件，以及如何转换、消耗、转移、接收或丢弃。

## 从哪里开始

- [World V2 文档索引](docs/world-v2/README.md)：当前规范、实施记录与历史文档治理入口。
- [电路系统模型与有界资源通路](docs/world-v2/circuit-system-model-and-bounded-resource-routing.md)：Board、元件、规则代数、求解器与运行时的总体模型。
- [规则转换契约实施记录](docs/world-v2/rule-transition-contract-implementation-log.md)：共享 Rule Evaluation / Transition / ResourceEffect 的当前实现与待办。
- [订单漏斗与工作台替换契约](docs/world-v2/order-funnel-and-workshop-replacement-contract.md)：订单阶段约束和资源迁移边界。
- [Parser Board Slot Runtime Boundary](docs/world-v2/parser-board-slot-runtime-boundary.md)：Parser 输入、产出与 Core Warehouse 的所有权边界。

## 验证

完整验证入口与约束见 [AGENTS.md](AGENTS.md) 和 [CONSTRAINTS.md](CONSTRAINTS.md)。

```bat
set CI=1
重构报告\verify.bat
```

必须同时取得 `VERDICT=OK` 与 `QUALITY-OK`。不要静默更新契约快照、回放字节或确定性基线。

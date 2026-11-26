# ADR 0009：资源回执日志保留与零资源结算语义

- 状态：Accepted
- 日期：2026-11-25
- 范围：Jar Core ResourceLedger、资源端口转移、订单资源结算与 World V2 检查点

## 背景

资源账本同时承担两类不同职责：向观察者提供有界的资源变化事件，以及阻止已提交操作被重复执行。端口转移又要求在提交后能返回可恢复的最终 TransferReceipt。订单结算则存在一种合法情况：资源暂存区与自机液库之间没有任何正数量返还或丢弃，但结算生命周期仍必须推进并可从检查点恢复。

如果把事件裁剪、OperationId 去重、TransferReceipt 和 SettlementStage 当成同一种“账本记录”，就会产生两类错误：裁剪历史事件时意外允许旧操作重放；或者为零资源结算制造没有物理资源变化的虚假事件/转移记录。

## 决策

1. ResourceLedger 分离 Event Window、OperationId Tombstones 与 Transfer Receipt Journal。事件窗口有界；OperationId 与成功 Receipt 不随事件裁剪而删除。
2. OperationId 与 Receipt 必须一起进入 ResourceLedgerSnapshot、World V2 JSON Wire DTO 和执行检查点。Restore 必须验证 Receipt 与 OperationId 一一关联、身份唯一且回执自身合法。
3. ResourcePortTransferCommitCoordinator 在物理账户仍处于补偿边界时构造并验证最终 Receipt，再通过 RecordTransferOnce 一次发布事件批次、OperationId 与 Receipt。空批次不能成为真实资源转移。
4. Jar 是离散所有权个体：Jar Receipt 必须使用 canonical 正整数 JarId，且 RequestedAmount 与 TransferredAmount 都为 1；不得将 Jar 数量化为可部分转移的液体余额。
5. 零资源订单结算仍必须持有有效 LiquidBatchReceipt 并推进 SettlementStage，但不创建 ResourceEvent、TransferReceipt 或结算用 OperationId。存在正数量 Accepted/Discarded 时，LedgerRecorded 必须对应实际账本 OperationId。
6. RecordBatchOnce 保留通用空批次 OperationId 语义，因为其他明确的无资源变化生命周期操作可能需要幂等；订单结算服务不得用它为零资源结算制造 phantom operation。RecordTransferOnce 始终拒绝空批次。
7. ResourceLedger 的锁只保护账本自身。物理账户跨端一致性目前依赖 World 单写者命令序列；未来并发化必须按稳定账户 ID 顺序锁定两端并覆盖完整提交与补偿窗口。

## 后果

- 事件窗口可以裁剪而不破坏跨重启幂等性。
- 检查点恢复能够区分“已完成零资源结算”与“有资源变化但账本记录丢失”。
- 转移调用方可以按 OperationId 找回历史成功回执，而不需要重复扣减资源。
- 长期运行仍需解决 tombstone/Receipt 索引增长；在完成安全压缩或外部持久索引前，不得宣称账本内存占用有界。
- 当前账本锁不是源/目标账户联合锁，World 单写者是必须持续强制的运行约束。

## 验证要求

- Event Window 裁剪后 OperationId 与 Receipt 仍可查询，快照恢复后重放不新增事件。
- 相同 OperationId + 相同请求返回历史 Receipt；相同 ID + 不同请求拒绝。
- Jar Receipt 拒绝非 canonical ID、数量不为 1 或多事件/多罐表达。
- 零资源结算经过故障注入后可重试、可恢复，不产生 phantom OperationId。
- 非零资源结算缺少账本 OperationId 时拒绝继续。
- 并发账本发布只能生成一份事件批次和一份 Receipt；物理账户并发仍需单独验证。

# ADR 0010：复合资源转移与资源事实身份

- 状态：Accepted（领域契约）；实现迁移待完成
- 日期：2026-11-25
- 范围：Jar Core Resource Ledger、Resource Port、B 类订单主从交接、World V2 持久化

## 背景

Core 已经具备六条单资源端口转移参与者及 OperationId -> TransferReceipt 日志，但 B 类订单主台→从台交接仍由 OrderTransferService 一次移动多只 Jar 和多种液体，并通过 RecordBatchOnce 发布混合事件批次。Jar Storage 与 Order Liquid Staging 是不同权威账户；它们共享同一交接提交边界，但拥有不同资源身份、Ban、容量和物理补偿规则。

现有账本模型还存在身份表达缺口：ResourceLedgerMutation 只有 Name，没有独立 ResourceIdentity；Jar Receipt 使用 JarId，但 Jar 事件 Name 使用 Shape；ResourceEvent 没有 OperationId 字段；B 类事件 From/To 使用 Workshop.Name。显示属性和机器身份因此没有完全分离，事件也不能直接关联到其幂等提交操作。

## 决策

1. **资源身份与展示属性分离。** JarId 是 Jar 个体身份，Shape 是属性；Liquid identity 属于 World Vocabulary；稳定 AccountId/OwnerId 是账户端点身份；Workshop.Name 只用于展示。
2. **引入 Transfer Leg 概念。** 每条 Leg 必须声明源账户/Output Port、目标账户/Input Port、资源种类、资源身份、数量、预期版本和提交后版本。
3. **引入 Compound Transfer 概念。** 多条 Leg 可以共享一个 OperationId 与最终提交边界。它必须整体预检、整体提交、整体补偿；不能用多个独立单资源 Commit 的串联代替原子批次。
4. **B 类订单交接属于 Compound Transfer。** Jar Storage 与订单液体 Staging 保持独立账户及独立规则；所有 Jar/液体 Leg 作为一个交接批次提交。任意 Leg 失败必须恢复全部已变化账户。
5. **单资源回执与复合回执区分。** 现有 ResourceTransferReceipt 继续表达单 Leg；复合操作使用包含所有 Legs 和相关账户版本的批次回执。OperationId 必须映射到完整提交结果；相同请求重放返回原始回执，不同载荷拒绝。
6. **账本事件具有事务关联与机器身份。** 目标 ResourceEvent/Mutation 明确包含 OperationId、ResourceKind、ResourceIdentity、DisplayName、稳定 From/To AccountId、Amount、Cause 与 Reason。同一批次的多个事件共享 OperationId。
7. **Wire/checkpoint 必须版本化。** 旧事件缺少 ResourceIdentity/OperationId 时只能按显式 legacy 语义读取；不得根据 Shape、Workshop.Name 或显示文案推断并伪造机器身份。
8. **保留 World 单写者边界。** 账本锁只保护账本，不构成多账户联合锁。未来允许并发时，必须按稳定 AccountId 排序锁定全部参与账户，并覆盖版本复核、物理提交、回执发布与补偿窗口。

## 后果

- B 类交接不会被错误拆成多个独立提交，保留原有全量交接和回滚语义。
- Jar 个体身份可以跨转移、持久化和审计保持稳定；Shape 变化不会改变个体身份。
- 账本事件可以从 OperationId 追溯到完整复合事务，也可以区分机器身份与展示属性。
- 需要版本化 ResourceEvent/Mutation/Wire DTO/Checkpoint；迁移前必须保留旧存档读取能力。
- 复合回执、事件关联和 B 类参与者迁移尚未实现；本 ADR 冻结目标契约，不宣称代码已完成。

## 强制验证

- B 类混合 Jar/Liquid 批次成功时，所有 Leg、事件、账户版本与 Receipt 完全一致。
- 在任意 Jar 移动、液体批次、后置 RuleTransition 校验或账本发布处注入失败，所有物理账户精确恢复（包括 Jar 引用和原顺序）。
- 同一 OperationId + 相同 Legs/端点/数量/预期版本返回同一批次回执，不再次移动资源。
- 同 ID 不同批次内容拒绝；缺少 Receipt 的历史 OperationId 不得推断成功。
- JarId 与 Shape 分离验证：改变 Shape 不改变 Jar 身份；同形状不同 JarId 不可混淆。
- ResourceEvent 可按 OperationId 回溯所属批次；按 ResourceIdentity 区分 Jar 个体和 Liquid 身份。
- Workshop 重命名不改变账户 OwnerId 与历史账本端点。
- 旧 Wire/Checkpoint 可按明确兼容规则恢复，不把展示字段升级成机器身份。

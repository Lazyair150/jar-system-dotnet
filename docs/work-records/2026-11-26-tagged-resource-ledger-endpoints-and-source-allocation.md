# Tagged Resource Ledger Endpoints and Per-source Settlement Allocation

## 目标

把 World-scoped OwnerIdentity / AccountId v2 继续贯穿资源事实账本。消除 null 同时代表“外部来源、外部去向、旧数据未知”的歧义；B 类结算不得把主台、从台多个订单子账户伪装成一个汇总来源。

## 起始状态

- Repository: `Lazyair150/jar-system-dotnet`
- Branch: `architecture/core-liquid-identity-consolidation`
- 基线代码已包含 WorldInstanceId、OwnerIdentity、OwnerIdentityMap、AccountId v2 和 schema 7 owner-map checkpoint。
- 当前工作只写入该分支；没有合并 PR。
- 不声称本轮构建或测试已通过。

## 本轮实现

### 1. Tagged endpoint algebra

新增 `ResourceEndpoint` 与 `ResourceEndpointKind`：

- `Account(AccountId)`
- `ExternalSource(SourceId)`
- `ExternalSink(SinkId)`
- `LegacyUnknown`

Account endpoint 只接受明确版本化的 account:v1 / account:v2；v2 会执行完整 canonical parser 校验。LegacyUnknown 是默认值，仅用于旧事件与尚未迁移的 legacy 写入点，不代表外部资源。

`ResourceEvent`、`ResourceLedgerMutation`、`ResourceLedgerSnapshot` 和 `ResourceLedger` 的 Record / RecordBatchOnce / RecordTransferOnce 都已携带并保留 tagged endpoints。旧 FromAccountId / ToAccountId 继续作为兼容投影；若与 tagged Account endpoint 不一致则拒绝。

### 2. Checkpoint schema 8

- `ResourceEventWireDto` 新增 FromEndpoint / ToEndpoint。
- `ResourceEndpointWireDto` 负责 endpoint tag 与 payload 的严格往返。
- checkpoint schema 升至 8；schema 3–7 继续读取。
- schema 7 及更早缺少 endpoint tag 的事件：若有旧 AccountId，则从该明确 ID 还原 Account endpoint；否则保留 LegacyUnknown，不从 From/To 展示文案推断。
- schema 8 execution checkpoint 必须显式携带每条账本事件的两个 endpoint 字段。
- WorldV2Context Restore 对所有 tagged Account v2 endpoint 校验当前 WorldInstanceId；v1 保持旧兼容身份，不推测 World。

### 3. 幂等指纹兼容

ResourceLedger 的新操作指纹包含 tagged endpoint。为避免旧 checkpoint 的 OperationId 在升级后因 JSON payload 形状变化而被误判为冲突，增加旧 payload fingerprint 兼容计算：重复操作先匹配新指纹，再匹配 schema 3–7 旧字段形状；只有两者都不匹配才拒绝。

### 4. B 类交接端点

`OrderTransferService` 现在优先使用 Workshop.OwnerIdentity 构造 World-scoped AccountId v2；订单暂存账户由 Workshop owner + exact demand/order ID 派生。没有 World-scoped owner 的 legacy Workshop 仍使用已有 account:v1 或 LegacyUnknown，不凭空生成 World ID。

### 5. 多来源结算逐账户分配

`OrderLiquidScope` 现在携带精确 Workshop OwnerIdentity。结算前先捕获主台、从台各自的订单余额；资源 Drain 与 Ship Return 完成后，构造不可变 `LiquidBatchSourceAllocation`，分别保存每个真实来源账户的 Requested / Accepted / Discarded，并与聚合 LiquidBatchReceipt 做逐液体守恒校验。

确定性分配顺序为主台订单账户优先，其次从台订单账户；每种液体的 accepted 总量按该顺序分配，剩余数量记为 discarded。该规则只解决不可区分的同类液体在多来源间的确定性归属，不改变总量、Ban、容量或资源移动行为。

- Return 事实按每个来源账户拆成独立 ledger leg，目标为 Ship Account。
- Discard 事实保留真实来源账户，目标显式为 `ExternalSink(ship.liquid-inventory.capacity-overflow)`。
- 不创建虚假的聚合“订单总账户”。
- 每来源分配随 OrderResourceLease settlement receipt 进入 checkpoint wire，恢复后重试仍使用原始分配，不重新猜测来源。
- 旧结算收据没有 source allocations 时继续走 legacy aggregate 读取路径。

## 主要改动文件

- `src/JarSystem.Core/Domain/ResourceEndpoint.cs`
- `src/JarSystem.Core/Domain/LiquidResourceAccounts.cs`
- `src/JarSystem.Core/Production/ResourceLedger.cs`
- `src/JarSystem.Core/World/WorldV2CheckpointJsonCodec.cs`
- `src/JarSystem.Core/World/WorldV2ExecutionCheckpointWireDto.cs`
- `src/JarSystem.Core/World/WorldV2Context.cs`
- `src/JarSystem.Core/World/Execution/OrderTransferService.cs`
- `src/JarSystem.Core/World/Execution/OrderResourceSettlementService.cs`
- `src/JarSystem.Core/World/Solving/OrderResourceLeaseWireDto.cs`
- `src/JarSystem.Core/World/Solving/OrderLiquidAllocationService.cs`
- `src/JarSystem.Core/World/Solving/WorldOrderExecutionAdmission.cs`
- `test/JarSystem.Tests/ResourceLedgerSnapshotTests.cs`
- `test/JarSystem.Tests/World2/WorldV2CheckpointJsonCodecTests.cs`
- `test/JarSystem.Tests/WorldV2/WorldOrderExecutionSessionTests.cs`

## 新增/更新验证用例

- Account / ExternalSource / ExternalSink / LegacyUnknown endpoint 区分。
- tagged endpoint 的 ledger snapshot 与 wire round-trip。
- endpoint 与旧 AccountId 投影不一致时整批拒绝且不留下 OperationId。
- schema 7 缺失 endpoint tag 的历史事件迁移为 LegacyUnknown。
- 旧 OperationId fingerprint 在升级后仍幂等。
- 多来源结算为主台、从台各自产生独立 Account v2 事实；每个来源分别对账。
- per-source allocation 在 OrderResourceLease wire 转换后保持一致。

## 尚未完成

1. `ProductionService` 及初始库存、生产、仓库流入/溢出等传统 Record 写入点仍有 LegacyUnknown endpoint；必须从实际 World/Production/Warehouse owner 注入中迁移，不能以显示字符串冒充账户。
2. 目前 tagged endpoint 的写入 API 仍允许 LegacyUnknown 作为过渡兼容值；待全部新生产点迁移后，新增操作必须 fail-closed，禁止未知端点。
3. `ResourceEvent.Name` 尚未完全迁移到独立 `DisplayName`；液体 ResourceIdentity 与本地化显示仍需完整 DTO/投影审查。
4. 多来源分配采用主台优先的确定性规则；若设计决定其他优先级，必须修改算法、测试和版本化契约，不能悄悄更换。
5. B 类物理资源交接与账本写入仍未形成 durable Prepare/Commit/Compensate 的跨账户复合事务。账本批次原子性不等于整个物理资源转移原子性。
6. Space Host 仍需传入真实 Ship / Board runtime instance identity；本轮未进行 Space GUI 验收。
7. 当前没有本地 build/test 结果；GitHub 当前头部 CI/status 需另行查询。未观察到的 CI 不可描述为通过。

## 验证边界

本轮代码、测试和工作记录写入 GitHub feature branch。尚未运行本地 `dotnet test`，不宣称编译或测试通过。后续应先检查当前分支 CI/编译结果，再修正发现的问题；若 connector 未返回对应 workflow/status，标记为 unconfirmed，而不是绿色。

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
- `test/JarSystem.Tests/WorldV2/WorldV2CheckpointJsonCodecTests.cs`
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


## CI 复核补记（2026-11-26 02:30 +08）

本轮从 Actions job log 直接定位到先前报告中未展开的真实阻断原因：

- `dotnet build JarSystem.slnx -c Release` 失败于 `WorldOrderExecutionSessionTests.cs` 的 xUnit analyzer 规则 xUnit2013：不允许用 `Assert.Equal(expectedCount, collection.Count)` 检查集合长度，要求改用结构性集合断言。
- 这是测试代码质量规则触发的 build error，不是生产代码编译错误；因此当时的 xUnit 测试阶段没有实际执行，后续“focused summary missing”属于 build 失败后的流水线连锁标记，不是第二个独立测试失败。
- 修复提交：`60831839dbde0acfa352a6e4490b86ee82ab383a`，把 per-source allocations 数量断言改为逐来源身份断言。
- 随后提交：`a0126ab1fd348b3f8ebf5b2ea63938b7079ec1da`，移除 wire round-trip 测试中冗余的集合计数断言；已有的完整 endpoint sequence equality 已覆盖数量和顺序。
- 此后 Actions 已为提交 `60831839...` 启动 push 与 PR 两个验证运行；截至本补记时它们仍在运行。提交 `a0126ab...` 的专属运行尚未在 Actions 列表中确认。故当前最新 HEAD 的完整验证仍为 **unconfirmed**，不得标记通过。

后续复核必须查看覆盖 `a0126ab...` 的最新 run，确认 Release build、JarSystem.Tests、Godot host build、契约/schema 与固定 Seed 验证全部结束；如有新失败，按首个真实失败步骤定位，不把下游缺少摘要误判成额外根因。


## CI-driven corrections and verified result

The first verification attempts surfaced real integration issues; they were fixed rather than waived:

1. Checkpoint codec lacked the Domain namespace import for `WorldInstanceId`; added the explicit import.
2. Endpoint validation was initially applied to non-nullable restored `ResourceEvent` values using nullable `.Value`; moved the nullable validation to normalized `ResourceLedgerMutation` inputs and kept restored-event validation on the value itself.
3. The schema-7 migration test lacked Domain/Production imports; added them.
4. xUnit analyzer rejected collection-size `Assert.Equal(..., collection.Count)`; replaced with `Assert.Single` / `Assert.Collection`.
5. Runtime tests exposed:
   - `RuleResourceAccount` had two parameterized constructors and no JSON constructor; added an explicit `[JsonConstructor]` preserving both v1 and v2 fields.
   - a World identity checkpoint test used the legacy Context factory without ShipLiquidInventory; changed the test to construct the full liquid-resource Context.
   - ApplicationService/Bridge no-options restore reused a newly created World identity and correctly failed the Context identity guard. ApplicationService now retains a defensive JSON snapshot of the last successfully assembled options and rebuilds a prepared Context with the checkpoint's persisted WorldInstanceId. Lifecycle updates are serialized under an ApplicationService lock. Legacy checkpoints still require the explicit migration-identity overload.
   - `ResourceEndpoint` received an explicit JSON constructor so direct ResourceLedgerSnapshot JSON round-trips preserve tagged endpoints.

### Verified CI

- Code/test head: `7329b7fa77488b616a222e6c00f14a34583de639`
- GitHub Actions push run **#5132**: **success**.
- xUnit: **994 total, 0 errors, 0 failed, 0 skipped**.
- World config contract: `CONFIG-WORLD-MATCH`.
- Solver differential: **4004 / 4004 agreement**.
- Modifier differential: **400 scenarios / 1600 checks**.
- Session replay: **33 commands**, successful.
- Dependency security: **6 projects, 0 vulnerable packages**.
- Secret scan: **623 files, 8 patterns**, successful.
- The matching PR-triggered run #5133 was still in progress when this record was updated; the successful push run above is the confirmed result.

This is GitHub CI evidence, not a claim that `dotnet test` was run locally. PR #63 remains open and unmerged.


## World V2 传统写入点迁移补记（2026-11-26）

继续盘点旧流程后，将已具备稳定运行时 OwnerIdentity 的 World V2 写入点迁移为 tagged endpoints：

- World V2 初始库存：`ExternalSource(level.initial-stock) -> Ship Account`。
- Jar 生产事实：`ProductionArea Account -> Warehouse Account`。当前 ShapeProductionLine 将多个 Producer 的速率汇总到同一形状生产线，并共享一个产出余数；在不改变生产算法/数量语义的前提下，账本来源只能诚实地标记为 ProductionArea 聚合生产账户，不能伪称某一个 Producer 单独产出。ProducerInstanceId 仍用于运行时实例映射和配置身份，但不伪造逐 Producer 产量。
- Jar 仓库溢出：`Warehouse Account -> ExternalSink(warehouse.jar-capacity-overflow)`。
- 液体生产：`ProductionArea Account -> Ship / Warehouse Account`；溢出指向对应容量 ExternalSink。
- 仓库向主台流入：`Warehouse Account -> Workshop Account`，工作台优先用 OwnerIdentity 生成 account:v2；没有 OwnerIdentity 时保留精确 OwnedInstanceId 对应的 account:v1 兼容路径；没有实例身份则使用 LegacyUnknown。
- 主台预置补液、预置回滚、退役主台返还：分别使用 Ship 与 Workshop 的明确账户，容量丢弃指向 ExternalSink。
- 六类资源端口物理转移（订单液体、Ship Prefill、仓库 Jar/液体、工作台游离液体/Jar）现在同时写入 From/To AccountId 投影和 tagged Account endpoints。
- OrderTransferService 原有主从 Jar、游离液体、订单子账户迁移已经写入 tagged Account endpoints；继续保留其 OwnerIdentity 优先、account:v1 兼容回退。

ResourceLedger.Record 与 RecordBatchOnce 现在统一拒绝：
- 两个 account:v2 端点属于不同 World；
- account:v1 与 account:v2 直接构成账户到账户转移。

ExternalSource / ExternalSink 与 Account 之间的资源边仍允许，因为它们表达外部边界而非另一个 World 内的账户。LegacyUnknown 仍只作为迁移兼容值，不会被解释为外部来源或去向。

新增测试覆盖：
- Host 创建的 World V2 初始库存、生产区、仓库、主台流入账本事实的真实 World-scoped owner endpoint。
- ResourceLedger 直接 Record 与 RecordBatchOnce 对跨 World / 混合版本账户边的拒绝及失败原子性。
- 六类资源端口提交事实都保留 Account endpoint 与旧 AccountId 投影一致。

### 迁移边界再次明确

`ProductionService` 属于旧版生产系统入口，目前没有可靠的 WorldInstanceId / OwnerIdentity 注入契约；它的传统 Record 写入暂时保留 LegacyUnknown，不能拿名称或 Shape/Name 字段冒充 OwnerIdentity。待旧宿主也通过明确的身份装配边界创建 OwnerIdentity 后再迁移。此限制不影响上述 World V2 Runtime 路径。


### 账本边界强化补记

账户到账户的 World 范围校验不仅应用于新写入的 Record / RecordBatchOnce，也应用于 ResourceLedger.Restore 的历史快照事件校验。即使 wire payload 的 endpoint tag 和旧 AccountId 投影各自有效，只要两个账户端点混用 account:v1/account:v2，或两个 v2 账户来自不同 World，恢复阶段也会拒绝；避免通过存档绕过写入边界。新增测试覆盖跨 World snapshot 的拒绝。


### 第二轮 CI 阻断与修复

在 World V2 预置补液回滚端点迁移后，Actions 明确报出 `ResourceWorldRuntime.cs` 中回滚闭包引用了外层不存在的 `targetWorkshop`（CS0103）。这是代码作用域错误，阻断 Release build；其余测试摘要/契约 marker 缺失均是 build 未通过后的连锁结果。修复提交 `d6ef0d6ffaaa88122f9036f77d3cb54db1bd5c1a` 在 BeginReplacement 的局部生命周期中捕获确切的 `prefillTargetWorkshop`，回滚时用原目标工作台的身份构造来源账户，不从岗位或名称重新推导。该提交的 push 验证运行 `45678082511` 已启动，截至本记录更新时仍在执行，结果未确认。


### 第三轮 CI 测试分析器修复

修复预置回滚作用域错误后，Release build 继续推进到测试项目，新的首个阻断是 xUnit2031：`Assert.Single(collection.Where(predicate))` 必须改为 `Assert.Single(collection, predicate)`。三个位置均在新增的 World V2 ledger endpoint 端到端测试中；已在提交 `3828d06dc05fe9c64dfef7550e297c835fd901d3` 统一修正。该提交 push run `45678588961` 正在执行，尚未得到完整 build/test/contract 结果。


### 传统切换与生产写入边界补记

- 传统 `ResourceTransferrer`（主台切换）现按 Workshop.OwnerIdentity 生成 account:v2 端点；没有新身份但有精确 OwnedInstanceId 时保留 account:v1；缺少身份时明确写 LegacyUnknown。容量丢弃和 Ban 拒收分别落到不同 ExternalSink。暂存记录同时保留旧 AccountId 投影与 tagged endpoint，提交时一次性写入。
- 旧版 `ProductionService` 尚未由 World V2 Host 装配，故其生产/流入端点显式标为 LegacyUnknown，不以“生产者”“仓库”等显示名称伪造账户身份；其容量溢出目标使用明确 ExternalSink。待旧宿主注入 WorldInstanceId/OwnerIdentity 后再迁移为 account:v2。
- 增加主台切换溢出端点测试：来源必须是原工作台 account:v2，去向必须是指定 Jar 容量 ExternalSink。

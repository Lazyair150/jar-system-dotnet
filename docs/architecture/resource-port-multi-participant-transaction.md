# Resource-port 多参与者事务：保证边界与后续协议

> 状态：复合账本 Receipt 与 Wire/Checkpoint 已实现；多参与者物理协调器尚未实现  
> 日期：2026-11-26  
> 适用范围：Core Resource Port、资源账户、ResourceLedger、World V2 Runtime

## 1. 当前实现实际保证什么

当前资源端口由一个精确的 `ResourceTransferRequest` 描述。一次请求只包含：

- 一个稳定 `OperationId`；
- 一条明确 `ConnectionId`；
- 一个来源 Port / Account；
- 一个目标 Port / Account；
- 一种 `ResourceKind` 与一个 `ResourceIdentity`；
- 一个正数数量；
- Prepare 时捕获的来源、目标账户版本令牌。

对应的 `Prepared*PortTransfer`（Order Liquid、Ship Prefill、Warehouse/Workshop Jar、Warehouse/Workshop Liquid、Workshop Free Liquid）提供各自的 `Prepare`、`Commit`、`Cancel`。

单参与者提交路径的顺序是：

1. Prepare 只读调用来源与目标 adapter 的 `Preflight`，产生账户版本令牌。
2. Commit 重读两端 Preflight，先检查既有 Transfer receipt / OperationId，再核验两个版本令牌仍等于 Prepare 版本。
3. 只有两端仍 Allowed 才进入对应的领域转移 helper。helper 捕获自己的物理快照，并负责该次账户修改的本地补偿。
4. 物理变化后再次读取两端 Preflight，核验 Port、Account、ResourceKind、ResourceIdentity、Amount 和版本变化。
5. `ResourcePortTransferCommitCoordinator.Finalize` 创建并校验单条 `ResourceTransferReceipt`，最后调用 `ResourceLedger.RecordTransferOnce`。
6. 如果最终校验或账本发布抛错，调用方必须仍处于物理补偿范围内；补偿失败或提交状态不确定时，参与者锁存 fault，禁止自动重试。

`ResourcePortTransferCommitCoordinator` 是共享的**单参与者最终化器**，不是跨多个连接的事务协调器。

## 2. 当前保证矩阵

| 场景 | 当前保证 |
|---|---|
| 单参与者、同一对象重复 Commit | 返回同一已提交 Receipt，不再移动资源 |
| 单参与者、Prepare 后账户版本变化 | 返回 Rejected/Undetermined no-mutation Receipt，不改账户 |
| 单参与者、账本已存在相同 OperationId | 精确匹配旧 Receipt 时返回既有结果；payload 冲突或仅存在无 receipt 的 tombstone 时拒绝 |
| 单参与者、物理提交或账本发布异常 | 领域 helper 尝试恢复该参与者捕获的账户快照；失败则 fault-stop |
| 单参与者、ledger 为 null | 仅有参与者对象自身的内存状态；跨对象/进程的持久幂等不由 ResourceLedger 提供 |
| 多参与者逐个调用 Commit | **没有全批原子性保证**；每个参与者各自发布 OperationId、Receipt 与事件 |
| 多参与者第二条失败，第一条已完成 | 第一条 ledger 事实已经持久发布，不能视为整个事务“从未发生” |
| 多参与者整体 checkpoint/restore | 尚无复合批次 Receipt / 原子 OperationId 集合可用于整组校验 |

无账本模式只适用于调用方明确接受内存生命周期语义、并在外层提供自己的持久事务边界的场景。它不能作为可持久化 World 运行时的跨进程去重机制。若外层没有可验证、与物理状态共同恢复的 durable journal，应视为不具备持久幂等能力。

## 3. 为什么不能把现有 Commit 串起来

假设一个板级资源操作由两条连接构成：

- Leg A：账户 A → 账户 B；
- Leg B：账户 C → 账户 D。

顺序调用 `Commit(A)` 后，A 的 Transfer receipt、OperationId 和 events 已写入账本；之后 `Commit(B)` 失败，即使外层把 A 的物理资源反向搬回，账本仍然保留 A 的原交易和去重 tombstone。若改用新的反向 OperationId，则历史会变成“发生过转移，然后又发生反向补偿”，而不是原子事务未提交；两者语义不同，且进程崩溃仍可能发生在两个边界之间。

更复杂的多腿还可能共享账户：例如 A→B、B→C。独立 Prepare 看到的 B 初始余额不一定等于整个批次按顺序执行时可用的中间余额；容量、禁令、账户版本令牌与净余额必须由同一个批次计划计算，不能简单地对每条腿做互不关联的预检。

因此，禁止将若干现有 `Prepared*PortTransfer.Commit()` 放进 foreach 并声称形成全局原子事务。

## 4. 目标复合协议

后续实现应引入批次层，而不是把每个参与者的单腿 Commit 直接复用为全局 Commit。

### 4.1 纯值计划与批次身份

建议的核心值对象：

- `ResourceTransferBatchRequest`
  - 稳定的 `TransactionId` / Batch OperationId；
  - 明确的 Connection / Leg 清单；
  - 每条腿自己的稳定 Leg OperationId；
  - 资源身份、数量、来源/目标 Account、Port 和账户版本约束；
  - 不允许依赖名称、列表位置或显示文本作为机器身份。
- `ResourceTransferBatchReceipt`
  - TransactionId、整体 Outcome；
  - 有序 Leg Receipt 集合；
  - 每条腿的来源/目标版本前后令牌、请求量、转移/保留/丢弃数量；
  - 能证明所有腿都属于同一 World，且最终资源守恒。
- `PreparedResourceTransferBatch`
  - 所有参与者在 Prepare 阶段建立的只读计划；
  - 仅在批次统一 Commit/Cancel；
  - 不暴露可以独立提前发布 ledger 的单腿 Commit 入口。

### 4.2 Prepare-all

Prepare 阶段必须只读：

1. 校验 TransactionId、Leg OperationId 的唯一性和稳定性。
2. 校验所有 Account v2 属于同一 World；不允许 v1/v2 混接或跨 World。
3. 校验 Port 方向、Connection 归属、资源身份 allow-list、数量与策略版本。
4. 为所有涉及账户构造统一的账户快照/版本图。
5. 若多个 Leg 共享同一账户，按批次计划计算净余额、容量占用和依赖顺序；不得让每条腿分别以原始余额做互相矛盾的判断。
6. 任一 Leg 无法 Prepare，整个批次保持无物理变化；已准备的参与者只允许 Cancel，不写账本。

### 4.3 Commit-all 与补偿

进入物理提交前，批次必须持有/锁定全部权威参与账户，并在同一边界重新验证版本令牌。执行时：

1. 捕获所有权威账户的一个批次前快照，保存原 Jar 实体引用与容器位置、液体余额、容量预留等必要状态。
2. 按确定性计划执行每条物理腿，但不发布任何 Transfer OperationId / ledger events。
3. 逐腿校验精确 Jar 身份、液体数量、来源/目标账户终态和账户版本变化。
4. 构造全部 Leg Receipt 与账本 Mutations。
5. 任一物理步骤、后置校验或 receipt 构造失败时，按确定的逆序对**整个批次**补偿。
6. 补偿全部成功：批次回到初态且不发布任何 Transfer ledger batch。
7. 任一补偿失败：抛出专用批次不确定异常，冻结 Runtime、保留全部现场，不释放仍关联的租约，不允许健康 checkpoint capture。

### 4.4 单次持久账本发布

这是实现复合事务的硬性前置，不得用多次 `RecordTransferOnce` 代替。

ResourceLedger 需要一个在单一内部锁与单次发布边界内完成的复合 API（命名待实现，例如 `RecordTransferBatchOnce`）：

- 验证 Batch OperationId / TransactionId 没有冲突；
- 校验所有 Leg Receipt 与对应 mutations 的身份、数量、账户和端点完全匹配；
- 为所有事件、批次索引、Leg OperationId、receipt/fingerprint 存储预分配容量；
- 只有全部校验及预分配成功后才同时发布批次幂等标记、每条腿的 receipt/fingerprint 与全部 events；
- 重放同一 TransactionId 且 payload 相同，整体 no-op；同 ID 不同 payload 整批拒绝；
- 不允许只留下若干 Leg OperationId、没有完整批次 Receipt，或反向的半个批次；
- Event window trim 不得移除批次去重凭证。

现有 `ResourceLedger.RecordBatchOnce` 原子化的是“单个 OperationId + 一组普通 mutations”；`RecordTransferOnce` 原子化的是“单个 Transfer OperationId + 一组 mutations + 一条 ResourceTransferReceipt”。复合批次需要新的领域/持久化契约，不应误认为现有方法已经支持。

Wire/Checkpoint 必须同步保存复合 Batch Receipt 与全部 Leg Receipt 的精确 payload/fingerprint。新增持久化字段必须 bump checkpoint schema，并保留 schema 3–9 的兼容读取；不能只在内存里保留批次关联。

### 4.5 发布后的投影

批次 ledger 发布成功之后的日志、HUD、统计和观察者回调属于投影，不得再次触发物理资源操作：

- 发布后投影回调失败：向 Host 报告 committed-but-projection-faulted，锁存 Runtime/Batch 状态；
- 不允许把它当作普通失败重新 Commit；
- 重放同一 Batch OperationId 不能重复调用外部投影回调；
- checkpoint 恢复时以 durable Batch Receipt 与事件指纹为准，不能只看某个 Leg 的 OperationId。

## 5. 必需故障与恢复测试

开始接入多连接前，至少需要以下测试类别：

1. Prepare-all 期间任一腿拒绝：所有账户不变、无 OperationId、无 ledger events。
2. 所有腿 Prepare 后某个账户版本变化：整批在第一笔物理变更前拒绝。
3. 第二/后续 Leg 发生物理失败：之前已执行的腿全部逆序补偿；物理账户精确恢复；账本完全无批次记录。
4. 中途补偿失败：Runtime fail-stop、活动参与者/租约保留、禁止推进和健康 checkpoint capture。
5. Ledger 复合发布中构造/验证/容量分配故障：不得只留下部分 Leg receipt、OperationId 或 events。
6. 同 TransactionId 同 payload 重放：物理状态、Ledger Seq、统计和回调次数全部不增加。
7. 同 TransactionId 不同任意一条 Leg 的资源/数量/端点：整批冲突拒绝。
8. 多腿共享账户与净余额/容量测试；明确执行依赖与顺序，不依赖独立预检的偶然结果。
9. 复合 Batch Receipt 与所有账户快照的 JSON encode/decode/restore 往返；恢复后继续推进不能重放任何资源腿。
10. schema 旧版兼容与复合字段损坏拒绝。

## 6. 当前实施状态与迁移顺序

- 已完成：单条连接参与者的 Prepare、版本重验、物理局部快照补偿、后置端点验证、单 Transfer Receipt 与单账本批次原子发布。
- 已测试：Order Liquid、Ship Prefill、Warehouse↔Workshop Jar/Liquid、Workshop↔Workshop Free Liquid/Jar 的容量/禁令/version change/duplicate OperationId/ledger publication failure 恢复。
- 已完成：B 类订单三条领域边界（Master→Slave、Slave→Master/Delivery、order staging→Ship）的专用事务与 fail-stop/Checkpoint 验证；它们各自是独立领域事务，不应被误称为多连接 Resource Port 复合事务。
- 未完成：多 Resource Port Leg 的全局 Prepare-all、物理 Commit-all、逆序 Compensate-all，以及 ResourceLedger 单次复合 Receipt/OperationId/event 发布。
- 推荐顺序：先实现复合 ledger 数据结构与 wire/schema 测试，再实现批次 Prepare 与账户计划，最后实现物理批量提交/补偿和 Runtime fail-stop；完成后才把 Circuit Board 多条连接接入生产/订单路径。

在这套协议完成前，对外只能宣称“单连接端口转移具备本地事务边界”，不能宣称“多连接/多账户批次原子性”。


## 7. 复合账本凭证第一阶段已实现

本节只记录**持久化账本层**的落地，不代表多参与者物理协调器已经完成。

### 7.1 复合凭证契约

- `ResourceLedgerTransferBatchReceipt` 使用独立的 `SchemaVersion`（当前为 1），以父级 `OperationId` 绑定有序 `LegReceipts`。
- 每条 Leg 仍保留自己的 `ResourceTransferReceipt`、`OperationId` 和操作指纹；父级凭证另有批次指纹，指纹覆盖复合回执及有序 Leg 指纹。
- Jar Leg 仍要求一个规范正整数 JarId 与恰好一只 Jar；所有账户必须属于同一 World scope，禁止 legacy/v2 混接或跨 World。
- 同一父 OperationId、完全相同的批次回执和 Leg 指纹重放时整体 no-op；任意 Leg、端点、数量或回执冲突时整批拒绝。
- 父标记、全部子 OperationId、子 Receipt、父 Batch Receipt、全部事件和 `BatchOperationId` 在同一个账本锁内一次发布。容量裁剪与 Clear 不会清除任何幂等凭证。

### 7.2 Snapshot / Wire / Checkpoint

- `ResourceLedgerSnapshot` 捕获并恢复复合回执；`RestoreFrom` 将父/子索引与事件一并替换。
- `ResourceLedgerWireDto` 增加 `TransferBatchReceipts`；`ResourceEventWireDto` 增加 `BatchOperationId`。
- World V2 checkpoint schema 升至 10。Schema 3–9 继续按旧格式读取；Schema 10 必须显式提供批次回执数组，且每个批次回执必须显式给出受支持的 `SchemaVersion`。
- Restore/Wire 校验父 ID 与 Leg ID 不冲突、Leg Receipt 与独立 Receipt 索引一致、父指纹可复算、事件的批次关联不可缺失或错绑。

### 7.3 证据与未完成项

目前测试覆盖双 Leg 原子发布、父/子 OperationId 与事件绑定、相同批次重放、错误 Leg 不产生任何局部账本状态、快照和 JSON 往返、事件裁剪/清空后幂等凭证仍可恢复，以及旧检查点 schema 兼容。

**仍未实现** Prepare-all、共享账户净余额与容量计划、物理 Commit-all、逆序 Compensate-all、补偿失败时 Runtime fail-stop，以及批次级外部投影 outbox。不能将当前账本原子发布称为整个多账户资源操作已经原子。

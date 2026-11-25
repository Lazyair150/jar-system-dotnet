# ADR 0011：World-scoped Resource Owner 与 Account Identity

- 状态：Accepted（身份语义与迁移契约）；WorldInstanceId 生命周期、OwnerIdentity/AccountId v2、OwnerIdentityMap 持久化及主要运行时 Owner 绑定已落地；External endpoint 类型代数和全量账本生产点迁移仍待完成
- 日期：2026-11-26
- 范围：Jar Core、World V2、Resource Ledger、Production Area、Ship Inventory、Warehouse、Producer、Checkpoint/Wire

## 背景与源码事实

上一阶段已将 ResourceIdentity、OperationId、FromAccountId、ToAccountId 贯通 Resource Ledger、Snapshot 与 World V2 Wire DTO。Workshop 的 Board-owned 账户可以从精确 OwnedInstanceId 构造稳定端点。

本次重新检查当前整合分支的实现，确认世界级资源所有者仍未形成完整身份链：

- WorldV2Context.Create(...) 没有接收或持有 WorldInstanceId。
- ShipLiquidInventory 只有容量与余额，没有 OwnerId。
- ResourceArea 组合 ProducerModule、LiquidProduction、Warehouse 与可选 ShipLiquidInventory，但没有自己的稳定实例身份。
- ProducerModule 的 Producer 注册与查找仍以 Producer.Name 为键；Producer 没有独立的 ProducerInstanceId。
- Warehouse 持有 Jar/Liquid 库存和 JarId 分配器，但没有持久化 OwnerId。
- 账本的空 FromAccountId / ToAccountId 目前同时可能表示外部来源/去向，也可能表示旧数据或身份未知，语义尚未区分。
- ResourceLedgerMutation.Name 仍是兼容字段与展示名称，没有独立的 DisplayName 契约。
- 订单结算将多个订单子账户的资源合并返还时，尚未保留完整的逐来源分配事实。

以上是源码审查结论，不代表本 ADR 所描述的目标已经实现。


### 当前实现进度（2026-11-26）

- `WorldV2OwnerIdentityMap` 已由 Host 显式注入，包含 Ship、CircuitBoard、ProductionArea、Warehouse、Producer、Workshop 的精确实例身份；Producer/Workshop 身份与实际运行对象逐一核对，禁止按名称、岗位或集合位置推导。
- OwnerIdentityMap 已进入执行检查点 wire，schema 升至 7；schema 6 及更早版本仍兼容读取。Restore 使用检查点内原映射并校验当前对象图一致。
- ProducerInstanceId / Workshop OwnedInstanceId 不进入 World definition fingerprint；实例身份与配置定义分离。
- ShipLiquidInventory、ResourceArea、Warehouse、Producer、Workshop 已可持有对应 OwnerIdentity；Warehouse/Workshop/Ship 资源端口有 v2 构造入口。
- 恢复检查点时，所有 account:v2 账本端点必须属于当前 WorldInstanceId；account:v1 仍作为旧数据兼容身份，不推断所属 World。


- `WorldInstanceId` 已进入 Host 创建、Context、执行检查点、JSON schema 6、Restore 和 Godot Bridge；旧 schema 3/4/5 必须显式提供迁移身份。
- `OwnerIdentity` / `AccountIdentity` 类型已实现；AccountId v2 使用 UTF-8 字节长度前缀，保留两参数 `RuleResourceAccount` 的 v1 兼容路径。
- 资源 Port Adapter 已提供 typed-owner overload；v2 transfer request/receipt 拒绝跨 World 与 v1/v2 混合端点。
- 具体 Ship/ProductionArea/Warehouse/Producer/Workshop Owner 来源的 Host 注入、检查点 owner mapping、账户事件全量迁移尚未完成。不得据此宣称所有生产写入都已使用 v2。

## 决策

### 1. WorldInstanceId 是资源身份命名空间

1. 每个运行中的 World 必须拥有一个稳定、非空的 WorldInstanceId。
2. WorldInstanceId 与 World Definition ID、Board Definition ID、Configuration Fingerprint 分离。相同配置创建的两个 World 仍是两个不同实例。
3. WorldInstanceId 不得从显示名称、配置指纹、对象地址、列表位置或进程级静态计数器推导。
4. World 创建时由装配边界显式提供；若宿主生成随机 ID，必须在第一次持久化前固定，并在 Restore、Replay 和重试时注入同一个 ID。
5. 测试夹具必须显式提供固定 ID；不得让随机身份污染确定性 Replay。
6. 旧检查点没有 WorldInstanceId 时，不得从配置指纹推断。迁移必须由宿主提供并持久化一次性的迁移身份；在迁移身份未固定前，不得伪造新的稳定账户端点。

### 2. OwnerId 与 DisplayName 永久分离

OwnerId 是机器身份，DisplayName 是可变展示属性。名称变更、语言切换、UI 文案调整不得改变 OwnerId、AccountId、事件指纹或历史账本端点。

Owner 的稳定身份由显式身份元组构成：

OwnerIdentity = (WorldInstanceId, OwnerKind, LocalOwnerId)

其中 LocalOwnerId 必须来自该个体/实例的稳定身份，不得使用展示名称。

### 3. 账户身份是 Owner + AccountKind 的组合

AccountIdentity = (WorldInstanceId, OwnerKind, LocalOwnerId, AccountKind)

规范化编码必须采用带版本前缀的无歧义长度前缀编码；禁止使用简单字符串拼接、分隔符拼接或哈希展示名。

新账户身份使用新的显式版本（目标 account:v2）。现有 account:v1 仅作为兼容身份读取；不得静默把 v1 字符串重新解释为 v2。已知 Owner 与 WorldInstanceId 的迁移必须产生显式映射并进入检查点迁移记录。

同一 World 内：
- 相同 OwnerIdentity + AccountKind 必须得到相同 AccountId；
- 不同 AccountKind 必须得到不同 AccountId；
- 不同 LocalOwnerId 必须得到不同 AccountId。

不同 World 之间，即使 OwnerKind、LocalOwnerId、AccountKind 完全相同，也必须得到不同 AccountId。

### 4. 各领域个体的 Owner 来源

| 领域对象 | Owner 身份来源 | 禁止的替代身份 |
|---|---|---|
| Ship | 玩家拥有的精确 Ship OwnedInstanceId | ship、船名、显示标签 |
| Circuit Board | Board/Circuit 的运行实例 ID | BoardDefinitionId、板名称 |
| Production Area | 所属 Circuit 实例 + 稳定 Area/SubArea ID，或独立 OwnedInstanceId | production 文案 |
| Warehouse | 精确 Warehouse 实例 ID；若其生命周期等同 ResourceArea，可由该 ResourceArea 的稳定 Owner 身份派生 | 仓库显示名称、对象引用 |
| Producer | 独立 ProducerInstanceId | Producer.Name |
| Workshop | 精确 OwnedInstanceId | Master/Slave、Workshop.Name |
| Order staging 子账户 | Workshop Owner + Order/Demand 实例身份 + AccountKind | 仅使用订单标题或列表索引 |

岗位角色（Master/Slave）是关系，不是个体身份。移动工作台到另一个岗位不改变其 OwnerId；同一 Definition 的两个实例必须保持不同 OwnerId。

### 5. 账本端点必须区分 Account、External 与 Legacy Unknown

空 AccountId 不再允许同时承载多种语义。目标事件端点必须可表达：

- Account：由稳定 AccountId 标识的权威资源账户；
- ExternalSource：世界初始化、生产输出等不持有内部库存的外部来源，具有稳定 Source/Port ID；
- ExternalSink：容量溢出、明确丢弃等外部去向，具有稳定 Sink ID；
- LegacyUnknown：旧检查点中确实缺失身份的历史端点，只用于兼容读取。

新写入事件不得使用 LegacyUnknown。生产、初始库存、流入、转移、返还和丢弃必须明确选择端点种类。

- 初始库存：ExternalSource(WorldInitialization) -> Ship Account。
- 生产入库：Producer Output Port -> Warehouse Account；若未来生产设备有真实输出库存账户，则必须显式改为 Account -> Account。
- 仓库流入工作台：Warehouse Account -> Workshop Account。
- 账户间转移：Account -> Account。
- 丢弃：实际来源 Account -> ExternalSink(Discard)。
- 无法确认的旧事件：保留 LegacyUnknown，不从 From/To 文案推断。

### 6. ResourceIdentity、DisplayName、Attributes 分离

目标资源事实：

ResourceFact = (OperationId, ResourceKind, ResourceIdentity, DisplayName, Attributes, SourceEndpoint, TargetEndpoint, Amount, Cause, Reason)

- Jar：ResourceIdentity = canonical JarId；Shape 是 DisplayName 或结构化 Attribute。
- Liquid：ResourceIdentity 来自 World Vocabulary；本地化名称只属于 DisplayName。
- Account/Owner/Port：只用稳定机器身份。
- Name 保留为旧 API/Wire 兼容入口，迁移期明确标记为兼容字段；新代码写入 DisplayName，不得让 Name 再承担资源身份。
- Wire/checkpoint 结构变化必须递增 Schema，并保留既有 schema 3/4/5 的读取路径。旧 Name 可映射到 DisplayName；旧身份缺失仍保持 Unknown。

### 7. 多来源结算不得伪造聚合 Owner

若一次结算从多个订单子账户返还资源，必须保留每个来源账户的实际分配。允许多个 Leg 共用一个 Compound OperationId，但每条事实都要有自己的 Source AccountId、Target AccountId、ResourceIdentity 和 Amount。

不得为了减少事件数量而把多个真实来源合并成一个不存在的“订单总账户”。不得只记录汇总总量而丢失来源归属。

### 8. 身份必须进入指纹与检查点

- AccountId 的规范化编码版本进入账户身份契约版本。
- Resource mutation fingerprint 必须包含端点类型、WorldInstanceId/AccountId、ResourceIdentity、数量、原因和完整请求语义。
- World checkpoint 必须保存 WorldInstanceId、Owner identity mapping、Account identity version 与必要的迁移 provenance。
- Restore 必须验证所有账户端点属于当前 World；跨 World 账户引用必须拒绝。
- Replay 必须注入原 WorldInstanceId；不得为同一 Replay 重新生成身份。

### 9. 迁移顺序

1. 引入 WorldInstanceId 与稳定 OwnerIdentity value contract，不改变资源移动行为。
2. 为 Ship、Circuit/Production Area、Warehouse、Producer 建立显式身份来源。
3. 将 WorldInstanceId 与 OwnerIdentity 接入 AccountId v2；保留 v1 legacy reader。
4. 区分 Account / ExternalSource / ExternalSink / LegacyUnknown 端点。
5. 将 ResourceLedgerMutation/ResourceEvent 的 Name 迁移到 DisplayName，并升级 Wire/checkpoint schema。
6. 迁移初始库存、生产、仓库流入、丢弃、主台预置与结算的事实生产点。
7. 保留多来源 settlement 的逐账户分配。
8. 最后再实现 ResourceTransferBatchRequest/Receipt 与 B 类复合事务，不把身份迁移和事务协调器混成一个无法定位的改动。

### 当前实现进度补充（2026-11-26）

- Tagged ResourceEndpoint 已进入 ResourceEvent / ResourceLedgerMutation / Snapshot / Wire：Account、ExternalSource、ExternalSink、LegacyUnknown。
- Checkpoint schema 已升至 8；schema 3–7 保留兼容读取，旧 endpoint 缺失时只使用明确的旧 AccountId 或 LegacyUnknown。
- WorldV2Context Restore 对 tagged Account v2 endpoint 校验 WorldInstanceId。
- B 类 Workshop handoff 在 OwnerIdentity 可用时写入 account:v2 端点；订单暂存账户按 Workshop owner + exact order ID 派生。
- 订单结算回执现在保留每个来源账户的 Requested/Accepted/Discarded 分配，逐来源写账本，不创建虚假聚合账户；分配顺序暂定主台优先、从台其次。
- 旧 OperationId 指纹保留旧字段形状兼容计算，避免 schema 升级改变历史操作的重放结果。
- 尚未完成：传统 ProductionService 写入点全量 owner endpoint 迁移、DisplayName 分离、LegacyUnknown 新写入禁令、durable B 类复合事务。

## 强制验证

- 相同配置创建的两个 World：AccountId 不同且资源隔离。
- 同一 World Restore/Replay：WorldInstanceId、OwnerId、AccountId、事件指纹完全稳定。
- 同一 Owner 的 DisplayName 改名：AccountId、历史事件身份与幂等指纹不变。
- 同 Definition 的两个 Producer/Ship/Workshop 实例：OwnerId 不同。
- 同一 LocalOwnerId 在不同 World：AccountId 不同。
- Producer.Name 改名不改变 ProducerInstanceId；重名显示名不能造成身份冲突。
- 初始库存、生产、流入、返还、丢弃均使用正确 EndpointKind。
- Account 端点必须属于当前 World；跨 World 引用拒绝。
- Schema 3/4/5 旧检查点读取后保留 Unknown，不从 Shape/Name 推断。
- 新 schema 往返保存 WorldInstanceId、OwnerIdentity、AccountId、DisplayName 和端点类型。
- 多来源结算的每个来源账户分别对账，账户总守恒且不出现虚构聚合账户。
- Replay、资源守恒、架构依赖和契约快照门禁全部通过。

## 后果

此决策先冻结身份语义，再进行生产代码迁移。它不宣称当前所有账户已经拥有稳定 OwnerId，也不宣称 B 类复合事务已实现。旧兼容字段与旧检查点必须保留可读性，但新写入路径必须 fail-closed，不能继续制造无法追溯的身份。

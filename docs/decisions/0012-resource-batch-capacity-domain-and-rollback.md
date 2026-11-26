# ADR 0012：复合资源批次的 Capacity Domain 与物理补偿边界

- 状态：Accepted（领域约束）；Batch Planner 与多账户物理协调器待实现
- 日期：2026-11-26
- 范围：Jar Core Resource Port、Workshop/Ship/Warehouse 账户、Resource Ledger、World V2 执行事务

## 背景：账户与容量不是同一个身份

对 ResourcePortAdapters.cs、LiquidResourceAccounts.cs、Workshop.cs、Warehouse.cs 与六条单连接事务路径进行源码审查后，确认仅按 AccountId 汇总复合批次是不正确的：

1. WorkshopOrderLiquidPortAdapter 的每个订单子账有独立 AccountId；WorkshopPrefillLiquidPortAdapter 也有独立 AccountId，但它们引用同一个 WorkshopLiquidStaging 对象，并共享暂存区容量和 Ban。
2. WorkshopLiquidStaging.OccupiedCapacity = Prefill 余额总量 + 对每个订单取 max(实际订单余额总量, 该订单容量预留) 后求和；没有实际余额但仍有预留的订单也计入占用。
3. 对已有容量预留的订单，AvailableCapacityFor(orderId) = max(0, quota - 当前订单余额)；无预留订单则使用 staging 全局 AvailableCapacity。这是保留路径峰值的领域语义。
4. ShipLiquidInventory 对所有液种共享总容量，并维护可回滚转移的 return-capacity reservation。
5. Workshop 游离液体按液种使用 MaxLiquids；Workshop Jar 使用 MaxJars；Warehouse Jar 使用全局罐数上限；Warehouse Liquid 按液种容量检查。
6. 同一个 Workshop/Warehouse 对象可承载多个独立账户；对象相同不意味着账户相同，AccountId 不同也不意味着不存在共享容量。

必须区分：
- AccountId：权威资源账户身份。
- CapacityDomainId：一个或多个账户共同受其约束的容量/预留身份。
- Runtime Binding：Participant 当前绑定的真实运行时对象，只用于本 World 内引用一致性验证，不持久化为地址/哈希。
- Capacity Constraint：总量、逐资源身份、离散槽位、峰值预留、Jar 唯一归属等计算维度。

## 决策

### 1. Account 与 Capacity Domain 独立

CapacityDomainId = (WorldInstanceId, OwnerIdentity, DomainKind, DomainLocalId)。

不得用显示名称、对象地址、哈希码、列表索引或 Master/Slave 岗位生成 DomainId。一个 Capacity Domain 可以约束多个 AccountId；一个账户也可以受多个策略/容量约束。

| 领域账户 | Capacity Domain / 约束 |
|---|---|
| Workshop Free Liquid | 逐液种 MaxLiquids + 自身 Ban |
| Workshop Order Staging | 每个 OrderId 独立账户；共同享有 Workshop Liquid-Staging 总容量、峰值预留与 Ban |
| Workshop Prefill | 独立账户；与该 Workshop 的 Order Staging 共享 Liquid-Staging Domain |
| Workshop Jar Storage | MaxJars + JarId 唯一归属 |
| Ship Liquid Inventory | 跨液种总容量 + return-capacity reservation |
| Warehouse Liquid Storage | 逐液种容量 |
| Warehouse Jar Storage | 全局 Jar 数量上限 + JarId 唯一归属 |

这只是现有模型映射，不代表通用 Capacity Domain API 已实现。未来容量计算必须由领域对象明确提供，Coordinator 不得猜测。

### 2. Runtime Binding 校验

每条 Leg 必须声明 AccountId、CapacityDomainId 并绑定能执行该账户物理操作的 Participant：

- 同一批次中，一个 AccountId 只能映射到同一个权威账户对象。
- 同一 CapacityDomainId 只能映射到同一个权威 Domain 对象。
- 同一 WorkshopLiquidStaging 的 Order 子账与 Prefill 必须体现“不同账户、共享 Domain”。
- Workshop Free Liquid 与 Jar Storage 必须体现“不同账户、不同 Domain”。
- 相同 AccountId/CapacityDomainId 对应不同对象时 fail-closed 或 Undetermined，不得任选其一。
- 内存引用只用于当前运行时绑定验证；持久化使用 World-scoped Owner/Account/CapacityDomain 身份。

### 3. Batch Planner 按有序 Leg 投影中间状态

批次包含父 OperationId、稳定顺序的 Legs、Connection/Port、资源类型/身份/数量和 Prepare 版本。

- Leg 顺序必须由 Circuit 执行计划或显式领域策略给出，不允许按字典枚举或线程完成顺序决定。
- 按稳定 AccountId 分组权威账户，按 CapacityDomainId 分组共享约束。
- 每条 Leg 依次投影源/目标余额、预留、容量占用和所有权，再评估下一条 Leg。
- 禁止只按最终 net delta 判定：先流出后流入与先流入后流出可能有相同净额，但中间状态合法性不同。
- WorkshopLiquidStaging 必须遵循 Prefill + 每订单 max(actual,reserved) 的占用规则。
- ShipLiquidInventory 必须保留跨液种总容量和 return-capacity reservation。
- Jar 为离散实体，JarId 必须唯一，数量恒为 1，同一批次不得重复接收、重复归属。
- 顺序/容量语义无法确定时返回 Undetermined，不得试探性重排。

### 4. 唯一状态快照与逆序补偿

- Prepare 完成后，按稳定身份收集唯一状态参与者。
- 同一物理对象承载多个账户时，只能按其完整状态边界捕获一次。WorkshopLiquidStaging.Capture() 已同时包含订单子账、Prefill、容量预留与共享容量；不能每条 Leg 分别捕获并重复 Restore。
- 若完整对象快照覆盖本批次无关状态，必须定义更小的事务快照边界，不能覆盖其它子系统。
- 物理操作复用领域事务：Jar 引用与 Warehouse 原索引、Workshop Snapshot、Ship reservation、订单 Lease/Manifest 等原语义必须保留。
- 失败按物理提交逆序恢复唯一状态对象；同一对象不得按 Leg 次数重复还原。
- 任一补偿失败，World 必须 fail-stop，记录父 OperationId、已执行 Leg、失败账户和恢复异常；不得自动重试或继续接受命令。

### 5. 账本是最终发布点

完整协议：

1. Prepare-all 与版本复验。
2. 捕获唯一账户/领域状态快照。
3. 在 World 单写者边界内按顺序执行物理变更，禁止先发布单 Leg Ledger Receipt。
4. 读取全部 Leg 提交后版本，构造并验证全部 Leg Receipt 与父 Batch Receipt。
5. 一次调用 RecordTransferBatchOnce，原子发布父/子 OperationId、指纹、回执和带 BatchOperationId 的事实。
6. 账本发布后只允许已验证为不会失败的内存封口，不再执行业务步骤。
7. 任一前置或账本发布失败，逆序恢复唯一快照；补偿失败则 World fail-stop。

禁止循环调用六条当前 Prepared Participant 的 Commit() 来模拟批次；它们各自调用 RecordTransferOnce，第一条 Leg 发布后不能再回滚其独立账本事实。

### 6. B 类交接边界

B 类交接还包含 Order Lease、Manifest、阶段标记、Jar 所有权顺序和订单结算语义。复合回执不能取代这些领域规则；批次规划器不得猜测 Master/Slave 岗位或订单阶段。

## 实施顺序

1. 引入 CapacityDomainId 与 ResourceBatchAccountBinding 契约，不改物理行为。
2. 为 Adapter 登记 AccountId、CapacityDomainId、资源维度与引用绑定，优先覆盖共享 WorkshopLiquidStaging 和 Ship Liquid Domain。
3. 建立有序 ResourceTransferBatchPlan，完成 Account/Domain 去重、逐步容量投影、Jar 唯一性与 World scope 校验。
4. 建立唯一快照参与者集合、逆序补偿和 World fail-stop。
5. 审计并迁移一条混合 Jar/Liquid 的 B 类复合路径，同时保留 Lease/Manifest/阶段约束。
6. 对每条 Leg、容量域、快照恢复、回执和 Ledger 发布注入故障。
7. 通过后才把 Circuit Image 多边路径接入批次执行入口。

## 强制验证

- 多个订单子账 + Prefill 同批次输入，验证共享 staging 容量与峰值预留。
- 同 AccountId 多 Leg 仅捕获一次；不同 AccountId 同 CapacityDomainId 正确共享容量。
- 同 ID 不同对象、同 Domain ID 不同对象拒绝绑定。
- Ship 多液种输入/输出考虑总容量与回滚预留。
- Jar 同一 ID 重复来源/接收/归属全部拒绝。
- 中间 Leg 容量边界、库存不足、Ban、领域异常、Receipt 校验、Ledger 发布失败。
- 任意失败精确恢复所有唯一状态与顺序；补偿失败后 World fail-stop。
- 父/子 OperationId、BatchOperationId 和检查点恢复一致。
- 单 Leg 旧路径和旧检查点兼容测试不回归。

## 后果

Account、CapacityDomain、Runtime Binding、Resource Fact、Transfer Leg 是不同概念。该决策防止错误拆分共享容量或错误合并不同账户。每种资源账户必须显式声明容量域与快照边界；无法表达的组合返回 Undetermined。

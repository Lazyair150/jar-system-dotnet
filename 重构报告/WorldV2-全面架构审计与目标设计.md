# World V2 全面架构审计与目标设计

> 文档性质：架构审计与后续实施基线。本文把当前代码事实、风险判断、目标设计分开描述。目标设计不是已完成实现。
> 审计范围：World V2 Context、Demand Board、Solver、Admission、Reservation、Execution Session、Liquid Accounts、Checkpoint、JSON Envelope 与 Godot Bridge。

## 1. 结论摘要

当前系统已经具备一条可运行的订单执行主链：需求登记 → 求解 → 资源清单 → 准入/预留 → 会话执行 → 资源结算。但模块边界仍以逐步迁移形成的对象集合为主，尚未完全收敛为一套可持久化、可验证、可恢复的统一领域协议。

反复出现的问题集中在五处：

1. **同一业务事实在多个对象中重复表达**：Plan、Manifest、Reservation、Admission Scope、Workshop/Jar 状态、Session Cursor 都保存部分重叠的身份/资源信息。每次跨模块时都要再做一轮一致性核对，且这些核对分散在不同类中。
2. **运行时模型与持久化模型没有完整分层**：当前外层 JSON Envelope 已存在，活动会话也有初步 Wire DTO，但整个 WorldV2ExecutionResourceCheckpoint 尚未拥有完整的根级 Wire DTO / 双向映射协议。不能把某个 DTO 可以 JSON roundtrip 误当成整个检查点可以稳定存档。
3. **恢复验证与恢复提交不是一个显式事务**：Context Restore 先做大量 staged preflight，再逐对象写入真实 Context；最终提交仍由多个可抛异常的 RestoreFrom 顺序组成。需要将验证完毕的恢复计划作为独立对象，并确保提交阶段无业务拒绝、无外部回调、无重新求解。
4. **旧液体路径与新液体路径并存**：Admission / Session 仍保留旧 Workshop.LiquidStorage 路径与 ShipLiquidInventory + WorkshopLiquidStaging 新路径。迁移兼容是合理的，但长期并行会使业务规则出现双实现、双测试、双故障面。
5. **关键不变量没有唯一权威定义**：A/B 阶段游标规则同时存在于 Wire DTO 和 Session Restore；主从资源一致性分散在 Manifest、Admission、TransferToSlave、Checkpoint Preflight。缺少单一的领域契约验证器和端到端状态机测试矩阵。

这些问题比单个 Jar 算法 bug 更重要。它们解释了为什么修改一个 DTO、Manifest 或 Restore 方法，容易在后续 CI 才暴露重复定义、契约不匹配或恢复路径不完整。

## 2. 当前架构事实与边界

### 2.1 已有的主要对象

| 领域 | 当前主要对象 | 主要责任 |
|---|---|---|
| 世界组合根 | WorldV2Context、WorldV2Factory、WorldV2HostService | 创建对象图、持有世界服务、Host 独占执行 |
| 需求 | OrderDemandBoard、OrderDemand、OrderDemandArea | 订单登记、区域顺序、冷却与成功履约策略 |
| 求解 | WorldOrderSolveBook、WorldOrderScheduler、WorldOrderSolvePlan | 求解缓存/失效、调度、不可变操作计划 |
| 资源声明 | WorldOrderResourceManifest、WorldResourceReservationBook | 计算订单资源需求、独占罐子/液体/从台岗位 |
| 资源账户 | ShipLiquidInventory、WorkshopLiquidStaging、OrderLiquidScope | 自机库存、工作台订单隔离暂存、主从单向液体转移 |
| 执行 | WorldOrderExecutionSession、WorldPlanStepApplier | 按固定计划推进、主从交接、交付、失败回滚 |
| 世界运行时 | ResourceWorldRuntime、WorldClock、ResourceArea | 世界时间、生产、运行会话推进 |
| 审计 | ResourceLedger | 有界资源事实记录与增量读取 |
| 持久化 | WorldV2ExecutionResourceCheckpoint、WorldV2CheckpointJsonCodec、Session Wire DTO | 运行时快照与 JSON Envelope；尚未覆盖完整根级 Wire 模型 |
| 宿主 | WorldV2ApplicationService、Godot WorldV2BridgeSession | 命令入口、Host 边界、Godot 适配 |

### 2.2 已经明确且必须继续保持的领域规则

- A 类订单只执行主台路径：不创建从台订单液体账户、不预留从台容量、不占用从台岗位、不产生从台使用事件。
- B 类订单才使用从台；从台使用期间唯一液体来源是主台向从台的订单交接。
- 主台 → 从台是单向交接；只转移从台路径所需液体，不把主台全部剩余液体强行塞入从台。
- 主台最终余额与从台实际接收批次是两个不同事实；主台多余液体保留在主台订单账户，终结时按结算规则返还自机液库。
- 从台容量不足以容纳主台全部剩余液体，不得导致订单失败；只有从台真实路径所需资源无法接收/执行才是失败条件。
- B 类交付罐归属按已执行计划确定；液体交接不等于所有需求罐都必须由主台交付。当前 Session 完成路径按 Partitioned 选择从台作为交付工作台，必须继续由真实计划/集成测试固定这一行为。
- 任何订单执行产生的游离液体先进入当前工作台订单暂存账户；ban 或总容量拒绝时丢弃；唯一回收路径是订单终结时返还自机液库。
- 生产区生产液体直接进入自机液库，不自动流入主台/从台订单暂存账户。

## 3. 反复出现问题的根因

### R1. 契约重复，但没有单一权威

同一状态约束目前可能出现在：
- Wire DTO 的 Validate()
- WorldOrderExecutionSession.RestoreProgress()
- WorldOrderExecutionSession.ValidateProgressAgainstWorkshopSnapshots()
- WorldOrderResourceManifest.Create()
- WorldV2Context.RestoreExecutionResourceCheckpoint()
- Admission 的 Restore()

例如会话阶段、主从游标、交接提交标记的组合，DTO 若只验证数值范围，就可能接受运行时恢复拒绝的对象。反过来，运行时增加新阶段条件而 DTO 没有同步，也会造成 JSON 能读但不能恢复。

**设计修正**：建立领域级 ExecutionProgressInvariants / CheckpointInvariants 纯验证器。DTO 负责结构合法性；领域验证器负责业务不变量；Runtime Restore 不再复制另一套规则。

### R2. 资源所有权有多个投影

同一订单资源分别出现在：
- Manifest：计划所需资源声明
- Reservation：资源独占权
- Admission Scope：液体账户和暂存容量
- Workshop：罐子的物理所在位置与标记
- Session：当前执行游标和交接标记

这些对象并非错误地重复：它们分别是需求、锁、实际账户、物理实体、执行进度。问题在于缺少一个明确的资源所有权生命周期协议，导致每个阶段都要人工推断谁有权操作/回滚。

**设计修正**：定义 OrderResourceLease（订单资源租约）作为生命周期聚合，统一关联：
- DemandId / WorldVersion
- LockedJarIds
- 初始液体供给声明
- Master/Slave 暂存配额
- Slave 岗位独占权
- 当前资源阶段（Admitted / Master / TransferCommitted / Slave / Settling / Released）
- 资源结算状态

ReservationBook 与 Admission 不应各自维护可互相矛盾的活动订单集合；它们应由租约协调器维护，底层账户仍负责具体余额。

### R3. Checkpoint 还不是完整的持久化协议

现状：
- WorldV2CheckpointJsonCodec 只处理版本化 Envelope。
- WorldOrderExecutionSessionWireDto 是活动会话的专用 DTO。
- WorldV2ExecutionResourceCheckpoint 仍包含运行时快照对象集合，没有完整 WorldV2CheckpointWireDto 根 DTO 和全量双向映射。
- 配置指纹目前来自 WorldV2Options；求解器注册表/自定义求解器版本没有被完整纳入可重建契约。
- 动态 ModifierStack 当前不由 World V2 Context 持有，因此不能声称已保存动态修饰器实例。

**设计修正**：将 Runtime Snapshot 与 Wire DTO 完全分离；根 DTO 必须能独立 JSON 序列化/反序列化。所有运行时对象通过显式 Mapper 重建，禁止默认序列化运行时对象图。

### R4. Restore 的预检与提交边界还不够强

当前 Context Restore 已先构建 staged Ship、DemandBoard、SolveBook、Reservation、Admission、Ledger，并验证 Session 进度。这是正确方向。但真实对象提交仍按多个对象顺序执行，任何后续 RestoreFrom 若因新校验或边界条件抛异常，前面对象已经被修改。

**设计修正**：拆成三个阶段：
1. Decode：JSON → Wire DTO，严格 schema / 版本 / 完整性检查。
2. PrepareRestore：Wire DTO → 完整 staged World 对象图，执行所有校验，生成不可变 PreparedWorldRestore。
3. CommitRestore：Host 独占区内一次性替换整个 Context 引用，而不是对现有 Context 的每个子对象逐个写入。

长期应采用“新 Context 完整构建成功 → Host 原子交换当前 Context → Dispose 旧 Context”的恢复方式。不要把复杂恢复做成对活对象图的逐字段覆盖。

### R5. 旧 API 兼容路径扩大了分支复杂度

当前 Admission / Session 对 ShipLiquidInventory == null 保留旧 Workshop.LiquidStorage 行为。过渡期可以保留，但必须明确迁移期限与测试隔离，否则新旧规则可能在同一 Session 中被不同分支执行。

**设计修正**：
- 新 World V2 工厂只允许新液体资源路径。
- 旧 API 移入 Legacy Adapter / Compatibility Facade。
- 新领域核心不再判断“是否有 ShipLiquidInventory”来决定领域语义。
- 迁移完成后移除旧路径，旧测试集中在兼容层。

### R6. CI 验证缺少分层失败归因

CI #1022 的实际根因是一个编译错误，后续 build、host build、xUnit、契约快照、solver differential、replay 等步骤因核心项目编译失败而连锁失败。日志最终产生多个缺失标记，容易造成“很多问题同时坏掉”的错觉。

此外，本轮重复定义来自对同一文件连续写入时第一次工具调用已经成功提交，但脚本随后报错；第二次操作又重复插入同一方法。这说明工具调用结果与脚本自身异常必须分别处理，不能以“脚本报错”推断“远端写入没发生”。

**设计修正**：
- CI 按 Build → Unit → Contract → Differential → Replay → Quality 分层，前层失败时后层标记为 Blocked，而不是再制造大量失败项。
- 每次写文件后重新读取目标分支文件和 HEAD，确认实际内容再继续。
- 多文件修改先形成补丁计划；提交后按同一 HEAD 做完整审查。
- PR 只认最新 HEAD 对应的 CI，不认旧 SHA 的绿灯。

## 4. 目标架构：按职责分层，而不是继续堆在 Context

\`\`\`text
Godot Host / Other Host
        |
        v
WorldV2Host (exclusive command boundary, context swap)
        |
        v
WorldV2Application (commands, queries, DTO mapping)
        |
        v
WorldKernel (composition root; owns one WorldContext)
        |
        +-- WorldState
        |     Clock / Production / Warehouse / WorkshopRoster
        |
        +-- DemandDomain
        |     DemandBoard / DemandIdSequence / FulfillmentPolicy
        |
        +-- SolverDomain
        |     SolverRegistry / SolveBook / Precheck / Search / Plan
        |
        +-- ResourceDomain
        |     ShipInventory / WorkshopStaging / ResourceLeaseBook
        |
        +-- ExecutionDomain
        |     OrderSession / StateMachine / StepApplier / Settlement
        |
        +-- AuditDomain
        |     ResourceLedger / ExecutionFacts
        |
        +-- Persistence
              RuntimeSnapshot <-> WireMapper <-> Versioned JSON
              PreparedRestore -> New WorldContext
\`\`\`

### 4.1 依赖方向

允许：
- Application 依赖 WorldKernel 的公开用例接口。
- Execution 依赖 Plan、Resource Lease、Workshop Domain Ports。
- Admission/Lease Coordinator 依赖 Solver Result 与资源端口。
- Persistence Mapper 依赖所有 Snapshot/Wire 类型；领域对象不依赖 JSON。
- Host 依赖 Application 与 Context 工厂。

禁止：
- Solver 直接修改 Workshop、液库、Reservation 或 DemandBoard。
- Plan 保存真实 Jar / Workshop / Context 引用。
- Session 自己重新求解或挑选替代 Jar。
- Wire DTO 依赖 Godot 或运行时服务。
- ResourceLedger 反向成为资源库存的事实来源。
- 从台直接接受生产区普通液体流入。
- 通过 Context 的公共属性绕过 Application/Host 命令边界修改状态。

### 4.2 各模块最终契约

| 模块 | 输入 | 输出 | 不应做的事 |
|---|---|---|---|
| DemandDomain | 用户订单命令、逻辑时间 | 有序需求、冷却/履约事件 | 不求解、不锁资源 |
| SolverDomain | Demand + WorldSnapshot + SolverProfile | Feasible/Impossible/Undetermined + Immutable Plan | 不改世界、不预留资源 |
| ManifestBuilder | Plan + Snapshot | 精确资源需求清单 | 不扣库存、不锁罐 |
| ResourceLeaseCoordinator | Manifest + 当前资源 | 原子租约 / 拒绝原因 | 不执行 Jar 操作 |
| ExecutionSession | Lease + Immutable Plan + Clock | 执行状态变化与领域事实 | 不重新求解、不绕过工作台 API |
| TransferService | B 类租约 + Master/Slave scope | 原子 Jar/液体交接结果 | 不把主台剩余全部灌入从台 |
| SettlementService | Lease + 账户余额 | 返还/丢弃收据、释放租约 | 不改变订单需求语义 |
| CheckpointCapture | 一致性边界内的 WorldContext | Runtime Snapshot | 不暴露可变内部集合 |
| CheckpointMapper | Runtime Snapshot | Wire DTO | 不自行修复/猜测非法状态 |
| CheckpointDecoder | JSON + SchemaVersion | 已验证 Wire DTO | 不创建活跃世界 |
| RestorePlanner | Wire DTO + WorldProfile/Registry | PreparedRestore 或完整错误列表 | 不修改当前世界 |
| HostCommit | PreparedRestore | 原子替换 Context | 不执行订单步骤或重新泵送 |

## 5. 订单执行状态机的唯一权威

目标状态：

| 阶段 | A 类 | B 类 | 主台订单容量预留 | 从台订单容量预留 | 从台岗位租约 |
|---|---|---|---|---|---|
| Admitted / MasterSteps | 有 | 有 | 有 | B 有 | B 有 |
| Transfer | 不存在 | 有 | 不存在 | 有 | 有 |
| SlaveSteps | 不存在 | 有 | 释放 | 有 | 有 |
| Settling | 有 | 有 | 按结算协议保留/释放 | 按结算协议保留/释放 | 有 |
| Released | 终态 | 终态 | 无 | 无 | 无 |

要求：
- A 类绝不创建 Slave Scope。
- B 类从台容量按 Slave 路径峰值预留，不按主台全部剩余液体预留。
- Transfer 只有一个不可分割提交点：预检成功之前没有任何实体/余额变化；提交后标记已提交。
- Checkpoint 只允许在 Host 独占边界捕获，因此不会捕获 Transfer 提交过程中的半状态。
- 若提交阶段违反预检结果，世界进入 Faulted；不能把部分提交伪装成普通业务拒绝。
- SlaveSteps 恢复必须满足交接已提交；Transfer 恢复必须满足尚未提交。
- 成功交付、失败回滚、取消、中断统一经过 Settlement / Lease Release 协议。

## 6. 持久化目标：完整的根级 Wire 协议

建议新增：

- WorldV2CheckpointWireDto：根 Envelope Payload。
- WorldV2CheckpointMetadataWireDto：schema、world profile id、configuration fingerprint、solver profile fingerprint、capture logical time、runtime tick。
- WorldV2WorldStateWireDto：生产器累积器、仓库、液库、工作台、时钟。
- WorldV2DemandBoardWireDto：策略、区域顺序、需求、冷却、ID 高水位。
- WorldV2ResourceLeaseWireDto：活动订单资源租约、罐子所有权、主从配额。
- WorldV2ExecutionSessionWireDto[]：计划、Manifest、阶段、游标、交接提交标记。
- WorldV2LedgerWireDto：保留事件、序号高水位、裁剪计数。
- WorldV2CheckpointMapper：Runtime Snapshot ↔ Wire DTO。
- WorldV2RestorePlanner：Wire DTO → PreparedRestore。

Envelope 建议：
- schemaVersion：持久化结构版本。
- kind：固定类型名。
- configurationFingerprint：World 配置指纹。
- solverProfileFingerprint：求解器注册表及其语义版本指纹。
- payload：完整根 Wire DTO。
- 可选 contentHash：对 canonical payload 计算 SHA-256，用于检测损坏；它不是签名，不能防恶意伪造。

兼容策略：
- 只允许显式迁移：schema N → N+1。
- 未知版本拒绝，不尝试“尽量反序列化”。
- 缺失必需字段拒绝；可选字段必须有明确默认语义。
- 不允许 Mapper 静默丢弃未知业务状态。
- DTO 结构校验与领域不变量校验分离。

## 7. 重点风险清单与处置优先级

| 优先级 | 风险 | 现状 | 必须完成的处置 |
|---|---|---|---|
| P0 | Wire DTO 与 Runtime Restore 契约分叉 | 已出现阶段/游标约束重复 | 抽取统一 ExecutionProgressInvariants |
| P0 | 整个 checkpoint 无根级 Wire DTO | 目前只有 Envelope + Session DTO | 完成所有子 DTO 与根 Mapper |
| P0 | Restore 提交非原子 | staged preflight 后仍逐对象修改 | 新 Context PreparedRestore + Host 原子交换 |
| P0 | B 类跨 Context 恢复缺少完整 JSON 端到端证据 | 只有局部 DTO 测试和对象快照恢复路径 | 真实 B 类订单 JSON → 新 Context → 继续执行 |
| P1 | Solver Registry 不进入配置指纹 | 当前指纹基于 Options | 增加 SolverProfile / 插件版本指纹 |
| P1 | 旧液体兼容分支持续存在 | 新旧两条行为路径并行 | Legacy Adapter 隔离并逐步删除 |
| P1 | 资源租约概念分散 | Reservation / Admission / Staging / Session 各自保存局部事实 | 引入 OrderResourceLease 与统一生命周期 |
| P1 | 错误回滚与 Faulted 边界分散 | Session 负责多项清理与回滚 | Transfer/Settlement 独立服务 + 明确幂等性 |
| P1 | 领域账本与状态的职责可能混淆 | Ledger 是有界记录 | 明确它是审计投影，不作为库存真相 |
| P2 | CI 连锁失败噪声 | 上游 build 失败导致后续标记缺失 | 分层短路并标记 Blocked |
| P2 | 单文件多次自动写入不够安全 | 本轮出现重复插入 | 每次提交后读回、校验 HEAD、避免重放非幂等编辑 |

## 8. 分阶段实施路线

### Phase A — 稳定当前分支（先完成）

1. 修复当前 Manifest 重复方法定义。
2. 让 Session Wire DTO 阶段/游标校验与 Runtime Restore 完全一致。
3. 增加 DTO 对 null 数组元素、重复键/非法嵌套结构的严格校验。
4. 以最新 HEAD 重新跑完整 CI；只认最新 HEAD 的结果。
5. 修复期间不新增大规模业务功能。

验收：Release build、全部 xUnit、Godot Bridge build、solver differential、session replay、quality gate 全通过。

### Phase B — 统一执行契约

1. 抽取 ExecutionProgressInvariants。
2. 抽取 Manifest/Plan/Lease 的一致性验证。
3. 将阶段迁移集中为显式状态机方法，外部不能直接组合非法 Stage + Cursor。
4. 为 A/B 两类分别建立完整状态转换矩阵测试。
5. 建立 Transfer 的失败注入测试：预检拒绝、Jar 提交异常、液体提交异常、账本写入异常。

验收：所有阶段组合都有正向/反向测试；A 类没有任何 Slave 资源痕迹；B 类交接不可重复。

### Phase C — 完整 Wire Checkpoint

1. 为 ResourceArea、ShipLiquidInventory、Workshop、DemandBoard、Admission/Lease、Reservation、Clock、Ledger、Session 建立纯 DTO。
2. 实现根级 Capture Mapper 和 Restore Mapper。
3. 加入 solver profile 指纹、schema migration 与 payload hash。
4. 先把 Wire DTO 转为完整 PreparedRestore；失败时不触碰当前 Host。
5. 用 Host 原子交换 Context，替代对活 Context 逐个对象 RestoreFrom。

验收：真实 A 类/B 类订单都能 JSON roundtrip；B 类分别在 MasterSteps、Transfer 前、SlaveSteps 中断点恢复；恢复后不重复消耗、泵送、交付或返还。

### Phase D — 资源租约与执行服务拆分

1. 定义 OrderResourceLease。
2. ReservationBook 改为租约索引/并发冲突协调器。
3. Admission 只负责创建租约，不再同时管理多个隐式生命周期事实。
4. TransferService 独立负责 B 类主从交接。
5. SettlementService 独立负责回滚、返还、丢弃与释放。
6. Session 只负责状态机与步骤游标。

验收：订单资源从准入到终结有一条可审计、可恢复、可幂等的生命周期。

### Phase E — 清理兼容层与宿主边界

1. 将旧 Workshop.LiquidStorage 兼容路径隔离到 Legacy Adapter。
2. World V2 Factory 不再产生 Legacy 模式。
3. Godot 只通过 Bridge/Application 命令和查询 DTO 访问世界。
4. 任何 UI/脚本不能直接改写 Core 内部库存或会话游标。
5. 最终移除不再需要的旧路径和重复测试。

验收：World V2 新路径没有 legacy 分支；Godot Bridge 的端到端命令测试通过。

## 9. 必须长期保留的测试矩阵

### 订单类别

- A 类：只主台；没有 Slave Scope、Slave quota、Slave reservation、Transfer event。
- B 类：主台完成 → 单向交接 → 从台完成；从台只接收主台交接的液体。
- B 类主台有额外剩余液体：额外液体留主台，不进入从台容量计算。
- B 类从台容量不足：只按从台实际所需批次与路径峰值判定，不按主台全部余额判失败。

### 恢复中断点

- A 类：主台执行中、最后一个耗时步骤后。
- B 类：主台执行中、Transfer 尚未提交、SlaveSteps 执行中。
- 每个断点都执行：Capture → JSON Encode → Decode → PrepareRestore → 新 Context Commit → 继续推进到终态。
- 对比恢复前后：DemandId、阶段、游标、罐子位置/状态、各液种余额、容量预留、资源租约、Ledger 序号。
- 验证没有重复：POUR_IN、POUR_OUT、RESHAPE、Jar Transfer、Liquid Transfer、Delivery、Return/Discard。

### 损坏与不兼容

- schema 版本未知、kind 错误、字段缺失/重复/未知、payload null、错误 hash。
- 配置指纹不一致、Solver Profile 不一致、工作台拓扑不一致。
- 重复 Jar ID、跨订单重复锁、多个 B 类租约、从台资源账户属于 A 类。
- 阶段游标不可能、Transfer committed 与阶段矛盾、Manifest/Plan/Lease 不一致。
- 任一失败都必须证明当前 Host 的旧 Context 未变化。

## 10. 本轮审计的明确边界

- 本文基于当前可读取的 PR #28 分支代码和 CI #1022 日志，不代表已经完成全仓每个旧模块的形式化审计。
- 当前 CI #1022 的唯一首个编译根因是 Manifest 的重复 RestoreFromWire；其余步骤因编译失败而连锁失败。
- 本轮已删除重复方法，并进一步收紧 Session Wire DTO 阶段/游标组合验证；这些修改必须由新 HEAD 的 CI 证明。
- 完整根级 Wire Checkpoint、PreparedRestore、原子 Context 交换、OrderResourceLease、Legacy Adapter 隔离均为后续目标，不能标记为已实现。

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


## 11. 2026-11-21 实施增量：执行进度与资源租约

本节记录后续代码提交，不覆盖前文审计时点的历史状态。

### 已落地的执行进度契约
- 新增 `ExecutionProgressInvariants`，统一活动 Session 的阶段、主从步骤游标、B 类标记与终态原因约束。
- Wire DTO 与 Runtime Restore 共用该验证器。
- Manifest/Plan 的 Wire 一致性仍在 DTO 边界检查；不在 Runtime Restore 中额外重建对象图做重复校验，以避免把 DTO 校验副作用带入活动世界恢复。
- A 类只允许 MasterSteps；B 类允许 MasterSteps、Transfer、SlaveSteps。Transfer 仅允许未提交状态，SlaveSteps 必须已提交交接。

### 已落地的 OrderResourceLease 第一阶段
- 新增 `OrderResourceLease`，由 `WorldResourceReservationBook` 持有，成为订单资源所有权的聚合入口。
- Lease 汇总 DemandId、WorldVersion、LockedJarIds、InitialLiquidSupply、TransferLiquidAmounts、主从暂存峰值配额、UsesSlave。
- 原 `WorldResourceReservation` 保留为兼容只读投影；旧调用方仍可通过 `Find` 获取投影，新代码使用 `FindLease`。
- Lease 增加生命周期阶段：Admitted、MasterExecuting、TransferPending、TransferCommitted、SlaveExecuting、Settled。
- Session 在主台执行、进入交接、交接提交、从台执行时驱动 Lease 阶段；Reservation 快照捕获当前 Lease 阶段，Restore 要求 Lease 阶段与 Session 阶段完全一致，不再用 Session 阶段覆盖 Lease 状态。
- ReservationBook 在释放时将 Lease 标记 Settled 后移除占用索引。
- B 类端到端恢复测试检查 SlaveExecuting 阶段；A 类测试检查不能进入 TransferPending。

### 尚未完成
- Lease 阶段目前由 Session 驱动，Admission 仍管理液体 Scope 与暂存区；尚未完成 Admission/Reservation/Scope 的完全收敛。
- ReservationBook 快照仍通过兼容 Reservation 投影承载 Lease 状态，但现在包含阶段、交接液体和主从暂存峰值；完整根级 Wire DTO 尚未纳入 Lease 的独立 DTO。
- TransferService 与 SettlementService 尚未从 Session 抽离。
- CI #1048 曾有两项 xUnit 失败；后续提交修正了 JSON 字段名测试与 Runtime Restore 的过度校验，并增加 Lease 生命周期集成。必须以当前最新 HEAD 的 CI 结果为准。


### TransferService 拆分进度
- 新增 `OrderTransferService`，把 B 类主台→从台交接的预检与提交从 Session 拆出。
- Service 输入明确为订单身份、Manifest、主从工作台、主从订单液体范围、账本与 tick provider。
- 预检先核对从台为空、转移罐子归属/重复 ID/罐位、旧液体兼容路径容量与 ban、新液体订单余额与从台批次容量。
- tick provider 在提交前取得；预检拒绝或 tick provider 失败时不触碰罐子和液体。
- 预检全部通过后调用 CommitStarted 回调，再连续提交 Jar 和液体转移；提交阶段不变量异常继续抛出，由 Session 负责失败清理。
- Session 保留阶段推进、交接提交标记与失败清理，不再直接实现转移算法。
- 该拆分仍保留旧液体兼容分支；后续由 Legacy Adapter 收敛，不在本次直接删除兼容行为。


## 12. 2026-11-21 后续实施增量：租约快照一致性与交接门禁

### 已落地的代码修改
- `WorldResourceReservation` 兼容投影新增 `Stage`、`TransferLiquidAmounts`、`MasterStagingPeakCapacity`、`SlaveStagingPeakCapacity`。
- `WorldResourceReservationBook.Capture()` 不再返回 Lease 构造时缓存的旧投影，而是从当前 Lease 状态即时生成快照；`Find()` 同样返回当前状态投影。
- `OrderResourceLease.Reservation` 改为动态只读投影，避免 Stage 在 Lease 推进后仍停留在 Admitted。
- Lease 从 Manifest 创建时复制主台初始供给、主从交接液体、主从暂存峰值；从 Reservation 快照恢复时恢复并验证生命周期阶段。
- Restore 拒绝 Settled 活动租约、非法阶段、负配额、A 类从台阶段/从台配额/交接液体。
- Session Restore 不再调用 `RestoreExecutionStage()` 覆盖租约状态，而是比较 Session 推导阶段与快照中的 Lease.Stage；同时比较 Manifest 与 Lease 的罐子、初始供给、交接液体及容量配额。
- `OrderTransferService` 现在必须接收对应的 `OrderResourceLease`，且只接受 B 类、阶段为 TransferPending、资源承诺与 Manifest 完全一致的租约。
- 新增/加强 B 类检查点测试：租约阶段、交接液体、从台容量配额跨快照保持一致；伪造 Lease 阶段与 Session 不一致的检查点必须被拒绝，随后仍可恢复合法检查点。
- 修复 `WorkshopWritersTests` 清单：新增的 `OrderTransferService` 被登记为唯一 B 类交接写入主体，保留工作台端口写入路径审计。
- 修复结算 tick 的获取时机：成功交付、失败清理和工作台替换中断现在都在清理/交付变更前预取结算 tick，并把同一 tick 传给从台回滚与液体返还/丢弃账本记录，避免罐子已经交付后才调用可能抛错的外部 tick provider。

### 当前验证事实
- 之前 HEAD `0140b3671dad5da8b00315071be49dc5d124dfd2` 对应 CI #1091 失败，唯一 xUnit 失败为 `WorkshopWritersTests.EveryWorkshopWriterIsOnTheList`；Release build、Godot Host builds、其余验证阶段通过。
- 该失败的根因是新增 `OrderTransferService` 调用 Workshop 端口，但未登记在写入主体清单。本轮已补登记。
- 上述之后又提交了 Lease 快照完整性、Session/Lease 一致性和 TransferService 租约门禁修改；这些改动尚未由最新 HEAD 的 CI 证明。当前最新 HEAD 为 `833c75a96757b17deb7cad6c869932f4d206481d`，CI #1122 正在运行。
- 当前实施仍不是完整 JSON 世界存档：Reservation 快照的 Lease 字段尚未形成根级 Wire DTO；PreparedRestore、Host 原子 Context 交换、SettlementService、Legacy Adapter 仍未完成。

### 下一批实施顺序
1. 把 Session 的终结与资源清理从流程代码中抽成幂等 `SettlementService`，先保持现有行为，再逐步让 Lease 成为结算入口。
2. 将 Admission 的活动订单 Scope/结算收据纳入以 DemandId 为键的 Lease 生命周期记录，减少 ReservationBook 与 Admission 双重活动订单状态。
3. 为 Lease 建立独立 Wire DTO，并纳入根级 Checkpoint DTO；不要继续扩张兼容 Reservation DTO。
4. 建立 PreparedRestore 与新 Context 装配流程，最后再做 Host 原子交换。
5. Legacy 路径迁移到 Adapter 后再删除，不改变用户已确认的 A/B 订单规则。


## 13. 结算入口拆分（2026-11-21）

### 已落地
- 新增 `OrderResourceSettlementService`，统一承接工作台暂存液体 Drain/返还自机液库、ResourceLedger Return/Discard 记录以及 Admission.Release。
- `WorldOrderExecutionSession.ReleaseReservation()` 保留执行会话级幂等保护与结算 tick 预取，但不再自行展开液体返还/丢弃账本循环；它将结算交给服务。
- SettlementService 在执行 Drain 前检查：若账本启用且订单暂存账户仍有余额，必须已经提供 settlement tick。缺少 tick 时在资源变更前拒绝。
- 从台罐子回滚、参与罐子标记清理仍由 Session 在调用结算服务之前完成；本次拆分没有把这部分执行阶段副作用伪装成已经集中化。

### 尚未完成的结算边界
- 当前服务仍以 Admission 的 `SettleLiquidResources` / `Release` 为底层实现；Admission 仍维护 `_scopesByDemand` 与短暂的 `_settlementsByDemand`。
- `OrderResourceLease.Stage=Settled` 目前在 ReservationBook.Release 移除租约前设置，释放后不会保留历史结算状态；没有持久化结算收据/操作 ID。
- 因此该服务是结算入口集中化的第一步，不等于已经完成跨崩溃幂等、可重放的 Settlement Transaction。
- 下一步应让 Lease 持有明确 SettlementState/SettlementReceipt，并把 Admission 的 scope 变为 Lease 持有的订单资源句柄；所有结算副作用必须有稳定 operation ID，避免“液体已 Drain、账本部分写入、进程中断”后重复记账。


## 14. 主工作台切换前置补液规则（2026-11-21，新增领域约束）

### 规则定义
- 主工作台岗位切换时，目标工作台接任主台之前，必须先从自机液库向目标主台的液体暂存资源补液。
- 补液发生在主工作台切换事件的资源转移阶段之前；顺序固定为：目标主台暂存补液 → 原主台迁徙重组/罐子与剩余资源转移 → 岗位指向提交。
- 补液目标是把主工作台暂存液体总量补到该主台配置容量；容量口径是所有合法液种合计，不是每种液体各自填满。
- 补液来源只能是自机液库中由资源生产区生产并入库的液体。该补液不允许从旧主台或从台借液，不改变 B 类订单主→从交接语义。
- 若自机液库库存不足以填满，按实际可用库存部分补入；不得凭空生成缺少的液体。具体接收量、未满足量必须可审计。
- 生产区仍只向自机液库生产；本规则是主台岗位切换时的一次明确资源调拨，不是恢复生产区自动向工作台流入。
- 自机液库存在多个液种时，先按自机液库的生产液种供给优先级分配，再处理其他可用液种；优先级必须由确定性规则给出并进入测试，不能依赖 Dictionary 枚举顺序。
- A/B 订单准入仍按订单自己的 Master/Slave 初始供给与执行峰值独立验证。主台切换补液不能给活动订单追加外来资源；切换开始前仍必须完成订单中断与结算。

### 实施状态（本轮更新）
- `WorkshopLiquidStaging` 已增加独立的 `PrefillBalances` 空闲预置账户；它与 DemandId 订单子账隔离，但与订单账户/峰值预留共同占用工作台总暂存容量。
- `ResourceArea.LiquidProductionPriority` 提供确定性优先级：正产能液种优先、产能高者优先，同速率按液体名称排序；零产能液种排在最后。
- `WorldSnapshot` 将主台预置余额单独纳入求解输入与 SHA-256 版本指纹；Solver 和 Manifest 将自机液库与主台预置余额合并计算可供给量，不把预置余额伪装成自机库存。
- 订单准入先从主台预置账户取满足初始需求的部分，再从自机液库扣除差额；剩余预置余额仍留在空闲账户。订单容量预留仍按真实路径峰值计算，预置余额占用的容量不会被重复计算。
- `WorkshopReplacementProcess` 在订单中断与从台清理后、迁徙重组/资源转移前调用前置补液事务；只有主台替换调用此入口，从台替换不会补液。
- 前置补液是可回滚事务：自机液库同时预留等量回库容量，替换期间的生产不能占用这部分容量；迁徙成功后提交预留，迁徙失败/取消时恢复目标主台预置余额与自机库存，并记一条反向 Return 账本事件。
- Admission 快照包含预置余额；主台切换成功后可随活动执行检查点保存/恢复。活动替换本身仍不属于当前执行资源检查点范围，替换过程中拒绝捕获检查点。
- 旧版 `Workshop.LiquidStorage` 迁徙仍保留兼容语义，不再被误认为新模型的主台预置账户。

### 必须新增的验证
- 切换目标主台前置补液先于旧主台资源转移事件；事件日志和账本顺序一致。
- 主台总暂存容量按所有液种合计；补液后不超过容量。
- 自机液库不足时只部分补液，余额不变为负数；无库存时不制造资源。
- 多液种优先级稳定、可复现，不依赖字典顺序。
- 切换失败/取消时，目标主台暂存余额、自机液库、账本与两台工作台快照全部回滚；提交后不得重复补液。
- 主台切换补液不得给从台补液；A 类不得产生从台范围；B 类从台仍只接受主台交接液体。
- Checkpoint 捕获/恢复必须保留主台预置余额与补液事务阶段，恢复后不得重复扣自机液库或重复记账。


## 15. Lease 资源所有权与主台预置事务落地（2026-11-21）

### 15.1 OrderLiquidScope 归属收敛
- `OrderResourceLease` 现在持有运行时 `MasterScope` 与可选 `SlaveScope`；A 类 Lease 只能绑定 MasterScope，B 类 Lease 必须绑定 MasterScope + SlaveScope。
- `WorldOrderExecutionAdmission` 已删除 `_scopesByDemand` 和 `_settlementsByDemand` 两份并行状态。准入器通过 `WorldResourceReservationBook.FindLease` 获取订单范围、结算收据和生命周期所有者。
- Admission Capture 从 ReservationBook 的活动 Lease 集合生成活动订单声明；Restore 从主/从 staging snapshot 重建 Scope，再 Attach 到对应 Lease。Scope 对象引用不进入 Wire DTO。
- `GetLiquidScopes` 是兼容查询投影，返回 Lease 所持的同一对象引用；A/B 测试分别固定从台 Scope 缺失/存在，以及 checkpoint 恢复后仍由同一 Lease 拥有。
- Reservation 兼容投影按 Lease 当前 Stage 缓存；阶段变化时失效，避免同一阶段反复读取生成不同对象，同时保证 Stage 不会陈旧。
- Lease 已在内存中持有 `SettlementReceipt`，防止同一活动 Lease 在结算入口内重复 Drain。该收据目前尚未进入独立 Lease Wire DTO，Lease 释放后也没有持久历史。

### 15.2 主台预置余额与订单供给
- 主台空闲预置余额和订单专属子账是两类不同所有权；预置余额不属于任何 DemandId，也不能被从台或其他工作台读取。
- Solver snapshot 独立携带 `MasterPrefillLiquidBalances`，版本指纹包含该余额；Manifest 的最低初始供给验证使用“自机液库 + 主台预置余额”。
- Admission 分配时优先消费主台预置余额，缺口才从自机液库扣除；成功后全部初始供给进入该订单的 MasterScope，避免订单执行时同时读取空闲账户与订单账户。
- 预置余额与订单账户共享主台总暂存容量。订单路径峰值仍然完整预留；如果未被当前订单取用的预置余额占据空间，导致路径峰值无法预留，准入应拒绝，而不是超容量或暗中丢弃预置液体。
- B 类仍然只有主台订单 Scope 可以向从台订单 Scope 泵送 `TransferLiquidAmounts`；主台空闲预置余额不会成为从台的补液来源。

### 15.3 可回滚前置补液
- `WorkshopLiquidStaging.BeginPrefillFromShip` 按给定优先级、工作台剩余总容量和自机实际余额计算补液批次；库存不足时部分补入，不制造资源。
- `ShipLiquidInventory.TryWithdrawForTransfer` 在扣款同时预留等量回库容量。替换期间 ResourceArea 仍可生产，但生产只使用未预留的剩余容量，保证取消/失败时能完整回滚。
- `WorkshopReplacementProcess.Begin` 的顺序是：中断订单 → 检查从台已清空 → 目标主台预置补液 → 建立迁徙快照 → 进入重组/转移阶段。
- 成功提交岗位后，补液事务 Commit 并释放自机回库容量预留；初始化失败、迁徙失败或取消时，先回滚工作台迁徙，再回滚预置账户与自机库存。
- 账本记录：补液提交前置阶段写 `flow`（自机液库 → 主台预置）；回滚写 `return`（主台预置 → 自机液库）。tick 在对应库存变化前预取。
- 活动替换期间暂停调度快照与订单启动，避免在 ShipLiquidInventory 存在未提交回滚容量预留时创建不完整求解输入或检查点。

### 15.4 当前边界与下一步
- 已实现的是进程内可回滚事务，不是崩溃后可恢复事务。活动替换仍不进入执行资源检查点；若进程在补液与替换提交之间崩溃，尚无持久化事务日志可恢复。
- Lease 的 SettlementReceipt 仍未进入独立 Wire DTO；ResourceLedger 也还没有稳定 OperationId 去重。因此不能宣称跨崩溃 exactly-once。
- 下一步应建立 Lease Wire DTO，把 Scope 的纯值 staging 状态、SettlementState/Receipt、稳定 operation ID 与 checkpoint 统一；随后建立 PreparedRestore 与 Host 原子 Context Swap。


## 16. 主台预置事务隔离与不确定回滚故障边界（2026-11-21）

- 前置补液事务处于 Pending 时，目标主台 staging 禁止订单容量预留、预置液提取、订单接收/消耗/Drain、操作接收/消耗预检、主从泵送和 staging 快照；避免绕过 Runtime 调度门禁的直接调用在事务窗口内消费或改写预置余额。
- WorldV2Context 装配时把订单准入门禁绑定到 Runtime；即使调用方绕过 Scheduler 直接调用 Admission.TryAdmit，替换活动期或 Runtime 故障期也会被拒绝。
- 生产区仍可继续向自机液库生产。液库的 `ReservedReturnCapacity` 保证预置补液回滚所需容量不会被生产占用；生产只使用扣除预留后的可用容量，超出部分按自机液库既有规则丢弃。
- Commit/rollback 都必须同时结束 staging Pending 锁与 Ship 回库容量预留；无库存可补的空事务可以安全地作为 no-op 提交或回滚。
- 如果账本前置事件写入失败且资源回滚也失败，Runtime 现在显式标记 `PrefillRollbackOutcomeUncertain` 并进入 sticky fault；不会把已中断订单、半笔预置库存和继续运行的世界混在一起。
- 岗位指向与迁徙副作用提交后，前置补液 Commit 若异常，不再调用迁徙 Rollback；它被标记为 `PrefillCommitOutcomeUncertain` 并抛给 Runtime，使 Runtime 进入 sticky fault。这样不会在已经切换岗位后反向迁回旧台。
- 这仍是进程内事务隔离，不是持久化两阶段提交。账本批量写入尚未提供事务性/OperationId 去重；跨进程崩溃恢复仍需后续 wire transaction log。


## 17. 准入与液体资源分配职责拆分（2026-11-22）

本轮将订单液体分配从 `WorldOrderExecutionAdmission` 中抽离到内部领域服务 `OrderLiquidAllocationService`：

- Admission 负责校验需求、求解结果、工作台岗位、罐子身份与资源租约；成功后委托分配服务。
- AllocationService 独立负责主台预置液优先抵扣、主台/从台峰值容量预留、自机液库差额扣款、订单暂存入账，以及失败补偿；可用库存视图也由该服务统一构造。
- 分配服务的可用液体视图同时计入自机液库余额、当前主台预置液、活动订单的逻辑预留；避免预置液在准入阶段被漏算，也避免真实扣款与逻辑预留重复扣减。
- Lease 仍是订单液体范围的唯一所有者；分配完成后把 MasterScope / 可选 SlaveScope 附着到 Lease，Admission 不再维护按 DemandId 索引的 Scope 字典。
- 结算路径进一步明确：`OrderResourceSettlementService` 负责 Drain 订单范围、返还自机液库、写结算账本；Admission 的新内部 `ReleaseReservationOnly` 只释放容量配额与资源租约。Lease 新增单调结算阶段（NotStarted → ResourcesReturned → LedgerRecorded）及稳定 `SettlementOperationId`，同一活动租约重试时不会重复写入结算账本。旧 `Admission.Release` 保留兼容语义，避免一次重构破坏外部旧调用方。
- 这一步是职责分离，不宣称持久化事务或崩溃恢复 exactly-once。Lease 的结算收据目前仍是运行时状态；下一阶段再把 SettlementState/Receipt 纳入正式 checkpoint wire contract，并为账本副作用建立可恢复的稳定操作标识。

工程方向：减少 Admission 的多阶段事务代码，不再用增加更多验证用例替代领域职责收敛。测试主要沿用既有准入、预置液、A/B 资源分配、恢复与结算用例作为回归基线。


## 18. 当前工程完成度盘点（2026-11-22）

这里的“完成”只表示当前 PR 分支内已有实现，不表示已经合并到主干，也不表示整个 Jar 游戏项目已经交付。

| 子系统 | 当前状态 | 主要剩余工作 |
|---|---|---|
| World V2 订单主链 | 已建立 | 清理旧兼容入口，统一领域契约 |
| Solver / Manifest | 已建立 | 完整 solver registry/profile 指纹与运行时规则版本 |
| A/B 资源租约 | 主体已落地 | 租约 Wire DTO 与完整恢复契约 |
| 订单液体 Scope 所有权 | 已迁入 Lease | 将 staging 快照/Scope 句柄映射从 Admission restore 流程进一步收敛 |
| 订单液体分配 | 已拆为 AllocationService | 把补偿路径与资源事务日志整合 |
| 主从交接 | TransferService 已拆出 | 交接收据/OperationId 的持久恢复 |
| 订单结算 | SettlementService 已拆出；Lease 有运行时结算阶段/收据/稳定 ID | 将状态和收据写入根级 checkpoint；账本按 OperationId 批量去重 |
| 主台切换预置补液 | 进程内事务与失败隔离已建立 | 持久化切换事务阶段，支持崩溃恢复 |
| 内存执行 Checkpoint | 已覆盖主要运行时对象 | 补齐所有权快照的一致映射 |
| JSON Checkpoint | Envelope 与 Session Wire DTO 已有 | 根级 World DTO、所有组件双向映射、版本迁移 |
| Restore | 有较完整 preflight | PreparedRestore + 无业务拒绝的原子 Context Swap |
| Host / Godot Bridge | 有独占操作与恢复入口 | 将命令边界、错误恢复和持久化生命周期收敛 |
| 兼容层 | 仍存在 | 在调用方迁移完成后分批删除，不能一次性破坏旧 API |

### 当前阶段判断

- PR #27 是 4.10b 液体资源领域基础，PR #28 是建立在其上的 4.10b-8 checkpoint 与运行时收敛工作；截至本记录，两者都仍为 Draft/Open，均未合并。
- 订单执行链已经不再是纯原型：需求 → 求解 → Manifest → Admission/Lease → Session → Transfer/Settlement → 资源账本有真实实现。
- 但持久化仍是最大结构性缺口：现在能捕获和恢复内存对象图，也能序列化部分专用 DTO，尚不能把整个 World V2 根状态作为稳定、版本化的 JSON 存档完整重建。
- 因此当前应称为“核心领域与运行时基础已形成，正在收敛持久化和恢复架构”，不能称为“Jar 项目整体已经完成”。

### 接下来的工程优先级（按交付价值排序）

1. 完成 Lease / Settlement / Transfer 的根级 Wire DTO 与双向映射，不再在 Admission、Reservation、Session 中复制资源事实。
2. 实现 PreparedRestore：预检阶段生成不可变恢复计划；提交阶段只做无业务拒绝的状态替换。
3. 实现 Host 原子 Context Swap，并用 B 类活动会话完成“序列化 → 销毁旧 Context → 重建 → 恢复 → 继续执行”的完整链路。
4. 将主台预置补液与订单结算的 operation log 纳入同一恢复协议，明确哪些副作用可重放、哪些必须拒绝恢复。
5. 最后才清理兼容层、统一 Godot 端 API，并转向完整游戏流程集成。

工程节奏调整：优先交付完整模块与跨模块链路；除非新边界引入风险，否则不为单一方法增加大量重复测试。每个阶段保留少量关键回归和 CI 基线即可。


## 19. Lease 完整 Wire 状态进入执行检查点（2026-11-22）

本轮不再只把 Lease 投影成 Reservation，而是增加独立的 JSON 可构造 wire contract：

- `OrderResourceLeaseWireDto` 保存订单身份、锁定罐子、初始供给、B 类交接液体、主从暂存峰值、执行阶段、结算阶段、稳定 SettlementOperationId，以及逐液种 Requested/Accepted/Discarded 收据。
- `WorldResourceReservationBookWireDto` 保存活动 Lease 集合；不保存罐子所有者索引和从台所有者索引，恢复时由 ReservationBook 从 Lease 重建派生索引。
- Wire 校验拒绝重复订单、重复罐子、多个从台占用者、A 类从台字段、非法阶段、错误 OperationId，以及不守恒的结算收据。
- `WorldResourceReservationBook.CaptureWire / RestoreWire` 提供完整捕获与重建入口。
- `WorldV2ExecutionResourceCheckpoint` 新增 `ReservationLeases`；Context 捕获时保存完整 Lease wire，恢复预检和提交都改用该 wire 数据，因此 SettlementReceipt/SettlementStage 不再在 Checkpoint → ReservationBook 之间丢失。
- `ResourceLedger.RecordBatchOnce` 以稳定 OperationId 原子提交一组资源事实，去重标识随 `ResourceLedgerSnapshot` 捕获/恢复；订单结算将返还与丢弃事件作为同一批提交。即使事件因有界账本容量被裁剪，操作去重标识仍保留。
- 旧 `Reservations` 快照暂时保留为兼容投影，不再作为完整恢复的事实来源。

边界说明：这让 Lease/Reservation 子图具备明确的 JSON wire DTO 和检查点往返入口，但还不代表整个 World V2 根检查点已可独立 JSON 序列化；Admission 的 staging 快照、Workshop/ResourceArea、DemandBoard、Ledger、Clock、Session 等组件仍需各自的 wire mapping。账本现在能在其快照生命周期内按 SettlementOperationId 去重；这解决了同一已恢复账本上的重复写入，但并不等于外部持久化存储的跨系统 exactly-once。

下一步直接扩展根检查点 DTO，并把 Prepare/Commit 恢复边界显式化；避免再在多个对象上分别维护同一份 Lease 结算事实。

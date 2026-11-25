# ADR 0008：个体区域、端口与自机电路通路

- 状态：Accepted（领域模型与迁移方向已确定；代码迁移分阶段进行）
- 日期：2026-11-25
- 关联规范：
  - ../world-v2/jar-individual-area-port-and-circuit-route-model.md
  - ../world-v2/circuit-system-model-and-bounded-resource-routing.md
  - 0007-core-rule-system-and-solver-decoupling.md

## 背景

Jar Core 的领域语义已包含独立 Workshop、Jar 账户、游离液体账户、自机液库、生产资源区、订单需求区和资源转移事务，但这些概念尚未统一成 Area / Port / Connection / Route 的结构模型。

若继续直接在 Solver、Board Adapter、Workshop 门面中编写资源移动，会产生重复的所有权、Ban、容量、接收、丢弃和回滚逻辑。若将所有液体余额合并，又会破坏普通工作台库存、订单范围暂存、自机液库与 Jar 内部液体的不同生命周期。

## 决策

### 1. 个体与区域所有权

- Workshop Owned Instance 是独立个体，拥有 Jar Storage Area 与 Liquid Resource Area。
- Ship 是独立个体，拥有 Ship Liquid Inventory。
- Circuit Board 定义 Production Area、Workshop Area、Order Area 的结构、槽位和显式连接。
- Board Assignment 绑定精确 Owned Instance；安装只建立结构关系，不转移实例内部账户所有权。
- Master / Slave 是 Board Slot Role，不是 Workshop Definition 或 Owned Instance 的固有类型。

### 1.1 顶层区域与功能子区域

- Circuit Board 只有 Production、Workshop、Order 三个顶层区域。
- Decomposition 是 Production 的功能子区域，保留独立 AreaId、端口、局部策略和实例类型；它不得作为第四个顶层 Board Area。
- Space Board Slot 必须分别记录 `area_id` 与 `sub_area_id`。原石解析器使用 Production/Decomposition；生产设备使用 Production/None。
- Board Assignment、冻结快照与指纹必须保留两个层级；迁移不得仅通过组件类别推断或丢弃子区域身份。

### 2. 账户分离

下列账户/范围不可合并：

- Workshop 普通游离液体库存（按液体身份与 MaxLiquids 语义）；
- Workshop 订单范围暂存（聚合容量、按订单隔离、含预置余额/容量预留）；
- Ship Liquid Inventory；
- Jar Internal Liquid；
- Workshop Jar Inventory；
- Production Warehouse / Parser Outbox；
- Order Lease / Reservation。

Area 是所有权与访问边界；同一 Area 可以管理多个不同生命周期的子账户，但不能把它们投影成无差别总余额。

### 3. 端口与连接

- 每个资源 Area 必须声明 typed Input Port 与 Output Port。
- Input 负责接收预检与准入；Output 负责取出预检与准出。
- Port 声明 ResourceKind、Identity Constraint、Policy/Version、Capacity/Rate Contract 与稳定身份。
- Connection 必须明确连接 Output -> Input，资源类型和身份约束兼容，并在冻结的 Board Definition/Assignment 中存在。
- Connection 只代表可尝试的结构通路，不代表资源已经转移。

### 4. 原子转移协议

跨区域转移不得通过调用方先扣源余额、再直接写目标集合来实现。统一协议为：

1. Transfer Request 指明 ResourceIdentity、Quantity、SourcePort、TargetPort、Correlation/Operation Id 与预期版本。
2. Source Output 与 Target Input 分别执行只读 Preflight，返回 Allowed / Rejected / Undetermined 与稳定 ReasonCode。
3. Transfer Coordinator 对两端账户版本和容量做原子复验，构造 ResourceEffect / Transfer Receipt。
4. 在一个明确事务边界内扣除源账户、接收目标账户，并记录显式 Consume / Transfer / Discard 结果。
5. 若目标只部分接收，未接收部分必须按端口契约留在源端或显式 Discard；不得无记录消失。
6. 并发版本冲突返回可重试的明确失败，不得重复扣款或重复接收。

CanReceive / CanWithdraw 是纯预检；TryTransfer 是跨账户提交入口。不能允许调用方通过单独 TryReceive 绕过源端准出约束，或通过单独 TryWithdraw 绕过目标端准入约束。

### 5. 自机电路与订单路径

- Circuit Image 是冻结 Board Definition、Assignment、实例成长、Area/Port/Connection、Vocabulary、Rule Registry 与 Policy 版本的不可变结构图。
- 当前订单路径源边界由 Ship Liquid Inventory Output 与进入 Master Workshop Jar Storage 的 Jar Input 共同组成。
- 路径目标边界是 Order Area 的需求/交付端。
- 生产区只有通过明确输入、处理规则、输出端口和接收连接后，才能成为某条路径的资源来源。
- 路径是有向多类型转换网络，可分支、汇合；不得将“固定拓扑”误解为硬编码单线流程。
- Order Target 与动态 Resource Binding 不属于静态 Circuit Image 的可变状态内容。

### 6. 算法边界

Solver 只读取 Circuit Image、目标、策略和纯值资源快照；不得持有 Workshop/Jar 实体引用，不得直接读写区域内部容器。Solver 输出符号路径/证明；Binder 绑定真实 Jar 和账户；Admission 建立 Lease；Executor 执行获准的 ResourceEffect；Settlement 记录结果。

## 不采纳的方案

- **单一全局库存池**：会破坏所有权、Ban scope、容量和订单隔离。
- **把工作台当作电路节点且直接共享其字段**：无法表达区域内部多个账户和端口权限。
- **每个端口独立扣款/收款**：跨账户事务可能半提交，造成资源丢失或复制。
- **把整条路线写成固定操作列表**：Board 拓扑、规则与订单目标变化后算法必须改代码，违反结构数据驱动原则。
- **一次性重写 Workshop**：风险高且难以证明资源语义等价；采用 Adapter + Differential Tests 逐步迁移。

## 后果

正面：
- 所有权、区域职责和跨区资源流可审计；
- 生产、工作台、订单与自机资源账户能在同一图模型中组合；
- 规则与搜索策略可独立演化；
- 资源守恒和事务边界有统一验证位置。

成本：
- 需要增加 Area/Port/Connection/Circuit Image 纯值契约；
- 现有 Workshop 直接领域方法需要逐步适配；
- 需要明确结构指纹与资源绑定指纹；
- 必须补充端口级并发、拒收、部分接收、回滚和重放测试。

## 迁移顺序

1. 冻结 Area/Port/Connection/Transfer Receipt 术语与身份契约。
2. 为 Workshop Jar Storage、Workshop Liquid Resource Area、Ship Liquid Inventory 建 Port Adapter。
3. 通过差分测试证明 Adapter 与旧领域操作语义等价。
4. 将 ResourceArea / Warehouse / OrderDemandBoard 映射到 Board 区域边界，但不复制它们的权威状态。
5. 建立 Circuit Image Compiler 与结构指纹。
6. 把订单路径求解从 Master/Slave 专用枚举逐步迁移到通用图模型；A/B 策略保留为显式 Policy。
7. 在新旧路径对拍、资源守恒和运行回放全部通过后，才清理兼容层。

## 验收

- 任一资源有唯一权威账户；
- 任一跨区域流动有源端、目标端、数量、规则版本和 Transfer Receipt；
- 拒收、部分接收、并发冲突和回滚不会导致资源丢失或复制；
- 普通库存、订单暂存、自机液库、Jar 内液体严格隔离；
- 同一 Circuit Image 与同一目标输入产生稳定结构路径指纹；
- 仅资源余额变化时可重新绑定，不错误重编译结构图；
- Solver 不依赖 Workshop/Jar 可变实体和 UI/宿主实现；
- 已有 Ban scope、Jar 极性、主从单向转移、Mark 生命周期和资源守恒语义全部通过差分测试。

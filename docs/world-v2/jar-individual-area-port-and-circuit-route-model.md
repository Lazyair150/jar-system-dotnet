# Jar 个体、区域接口与自机电路通路模型

> 状态：规范性领域模型（2026-11-25）。
> 本文定义实体、资源区域、端口和路径；它是电路系统总体模型的基础概念补充，不宣称所有端口抽象已经在生产代码中完成。

## 1. 核心裁决

Jar 系统不是“若干工作台功能 + 一个订单算法”，而是由**个体（Individual）、区域（Area）、接口/端口（Port）、连接（Connection）、通路（Route）、规则（Rule）和资源（Resource）**构成的有界资源状态转换网络。

- 个体拥有区域；区域拥有资源账户或功能职责。
- 区域通过有方向、有类型的接口暴露资源输入和输出。
- 通路连接接口，不直接越过个体/区域边界写入内部状态。
- 规则定义资源或 Jar 能否通过接口、经过什么转换、付出什么成本以及产生什么结果。
- 自机电路板的结构和已安装实例决定当前网络拓扑；订单定义目标端，不定义算法步骤。
- 求解器只在已定义的世界和规则上寻找可行路径，不拥有领域语义。

**工作台是独立个体；自机是容纳区域与安装实例的结构个体。** 安装关系不合并所有权，不把工作台内部账户变成自机账户。

## 2. 两类个体与所有权

### 2.1 工作台个体 Workshop Individual

每个工作台实例至少拥有两个独立区域：

| 区域 | 资源/职责 | 权威所有者 |
|---|---|---|
| 液体暂存区（Liquid Staging Area） | 工作台游离液体账户、容量、液体接收策略、操作可用余额 | 该工作台实例 |
| Jar 存储区（Jar Storage Area） | Jar 个体集合、Jar 容量、Jar 接收/取出策略 | 该工作台实例 |

工作台还拥有自己的能力/规则上下文：基础与附加标记、Ban、容量、有效修饰器及其版本。规则上下文约束工作台操作，但不是第三个资源账户。

主台/从台是自机安装槽位的**角色关系**，不是工作台 Definition 或 Owned Instance 的固有身份。同一个工作台个体在不同 Board Assignment 中可以承担不同角色；当前运行中的精确安装关系必须冻结。

### 2.2 自机个体与电路板结构

自机（Ship）是一个独立个体，其电路板（Circuit Board）定义结构区域、槽位和连线拓扑。当前概念区域为：

1. **生产区（Production Area）**：安装生产类元件，提供其声明的输入、处理和输出接口。
2. **工作台区（Workshop Area）**：安装一个或多个独立 Workshop Owned Instance；区域本身不吞并工作台内部资源账户。
3. **订单区（Order Area）**：承载订单目标、需求入口和完成/交付出口。

自机另拥有**自机液体存储区（Ship Liquid Inventory Area）**，它是自机自己的液体资源账户，不属于任何工作台的液体暂存区。Board Definition 描述结构；Board Assignment 绑定玩家拥有的确切实例；Ship Inventory 持有自机资源。三者不可混为一体。

生产区的产出只有在存在明确的输出端口、接收端口和连接通路时，才能进入其他区域。不能因为生产区与工作台区都在同一块板上，就推定它们天然共享库存或自动连通。

## 3. 区域与接口/端口是不同概念

### 3.1 Area

Area 是资源账户或功能职责的边界。每个 Area 至少具有：

- AreaId：稳定身份；
- OwnerId：所属个体；
- AreaKind：LiquidStaging、JarStorage、ShipLiquidInventory、Production、Workshop、Order 等；
- ResourceKinds：可承载资源类别；
- CapacityPolicy：容量和溢出策略；
- AccessPolicy：读取、接收、取出与转移约束；
- StateVersion：该区域权威状态的版本。

区域可以持有资源，也可以仅承担功能职责。不要为了统一类型而让订单区持有 Jar 余额，或让工作台区复制工作台的 Jar/液体余额。

### 3.2 Port

Port 是 Area 对外公开的有方向接口。资源区域必须明确暴露输入和输出能力；不允许宿主直接修改其内部集合/余额。

端口契约至少包含：

- PortId、OwnerAreaId；
- Direction：Input 或 Output；
- ResourceKind：Jar、Liquid 或明确的其他资源类型；
- AcceptedIdentity/ProducedIdentity：允许的具体身份或身份约束；
- Capacity/Rate：可接收量、可输出量或速率限制；
- PolicyRef + Version：规则与访问策略；
- State/Topology Dependency：端口可用性所依赖的状态与结构版本。

端口的领域操作应表达意图，而非暴露内部容器：

- Input：CanReceive / TryReceive；
- Output：CanWithdraw / TryWithdraw；
- Transfer coordinator：验证源 Output 与目标 Input，产生一份带来源、去向、数量、版本与结果的 ResourceTransfer/ResourceEffect。

这些名称是契约语义，不要求当前代码立即照搬方法名。任何实现都必须保留工作台已有的 Ban、极性、容量、订单暂存范围和事务边界。

### 3.3 Connection

Connection 是两个端口之间的显式连接，必须满足：

- 源端为 Output，目标端为 Input；
- 资源类型兼容；
- 身份映射明确；
- 连接在当前 Board Definition/Assignment 中存在；
- 两端策略均允许；
- 不违反单向/阶段/所有权约束。

连接只表示“可尝试通过”，不等于资源已经转移。实际转移仍需规则判定与原子 ResourceEffect。

## 4. 自机电路：固定拓扑、动态状态

电路板应被编译为有向、多类型的资源转换图（Directed Typed Resource Transition Graph），而不是一条预写死的算法流程。

- 顶点：Area Port、规则转换节点、订单需求端点。
- 边：Board Connection、资源转移边或规则声明的前驱/后继关系。
- 节点/边类型：Jar、Liquid、Production、Order 等。
- 约束：容量、身份、Ban、极性、方向、角色、阶段、资源账户和规则版本。

### 4.1 当前订单通路的源端（Head）

按当前玩法模型，订单求解网络的初始资源边界由两类入口共同构成：

1. **自机液体存储区的液体输出端口**：为允许的工作台液体操作提供可用液体来源。资源先由自机账户持有，经过明确的输入连接/准入后才进入工作台暂存账户。
2. **进入主工作台 Jar 存储区的 Jar 输入端口**：初始 Jar 资源进入主工作台的 Jar 账户；进入时必须绑定真实 Jar 身份，并遵守 Jar 所有权与工作台接收规则。

这两类源不可合并成一个“总资源池”。液体与 Jar 的数量、所有权、位置和生命周期分别记账。主台的游离液体来源、主台 Jar 初始资源入口也不能被算法私自替换成从台或全局仓库。

### 4.2 中间通路（Body）

中间路径由当前 Board Assignment 中安装的工作台实例、其区域端口、规则转换以及显式连接组成。

工作台操作是路径上的状态转换，不是路径之外的副作用。例如 PourIn 会同时改变 Jar 内部液体状态和工作台/订单液体账户；ReshapeEmpty 可能消耗工作台游离液体；ReshapeFull 的罐内液体自耗来自 Jar 自有账户。每项影响都必须通过规则与 ResourceEffect 表达。

工作台实例之间的主从关系只允许声明过的方向和资源类型。特别是既有主从液体单向依赖、主台 Ban 的暂存区作用域、Jar 自身极性操作权限，均不得因增加通用端口而被弱化。

### 4.3 订单通路的尾端（Tail）

订单区提供目标输入与验收/交付端点。订单声明最终 Jar 事实与数量，不指定 Jar ID、工作台分配或操作顺序。路径必须在满足所有订单需求后才算到达目标。

订单不是一个可以随意接收资源的普通库存区。订单需求端口定义接受的最终状态、数量、阶段和交付约束；订单满足/交付由订单规则和结算流程确认。

### 4.4 生产区与订单主路径的关系

生产区属于电路板，但不能因此被假定为所有订单 Jar 通路的隐式起点。生产元件必须显式声明输入端、处理规则、产出端和资源归属。其产出经过真实连接与接收准入后，才成为某个资源账户中的可用资源。若生产输出先进入 Core Warehouse，再由自机/工作台接收，则必须保留该仓库边界与导入操作身份。

## 5. 路径不是单一线性链

“头—尾”定义资源边界，不意味着内部只有一条线性序列。真实网络可能存在：

- 多种资源源头（Jar 与 Liquid）；
- 一条需求的多种候选路径；
- 多个订单需求共享有限资源；
- 多工作台分段处理与单向交接；
- 分支、汇合、前置准备操作；
- 同一 Jar 路径与游离液体路径在某个规则节点发生耦合。

因此模型是有向多重图/转换网络；单个可执行方案可以表现为有序操作序列或 DAG。资源竞争与先后关系由依赖和账户余额约束表达，不能把图结构误写成简单的“每个工作台接下一个工作台”。

## 6. 固定与变化的边界

在以下内容冻结时，电路拓扑和规则结构固定：

- Board Definition/Version；
- exact Board Assignment（slot、Owned Instance、Definition/version）；
- 实例成长快照与 progression revision；
- 区域、端口、连接及其版本；
- World Vocabulary、Rule Registry、规则参数与有效窗口；
- 角色/阶段策略与生产元件能力。

资源余额、Jar 当前内容、工作台暂存液体、订单目标和运行游标则按各自依赖变化。

订单目标改变通常意味着重新求目标路径，但不一定需要重编译静态拓扑。资源余额改变通常只要求重新做 Resource Binding / Readiness / Admission；若规则明确依赖余额或时间窗口，必须把该依赖纳入相应指纹。

故“暂时固定的复杂路径”应精确定义为：**给定冻结的自机结构和实例快照后，端口连接与规则转换图固定；在此图上针对订单目标和资源状态求解具体可执行路径。** 不能把结构图、解集、真实资源状态混成一个缓存对象。

## 7. 资源转移的强制不变量

1. 单一资源在任一时刻只有一个权威账户/位置；转移不能复制资源。
2. 转移量 = 源账户扣除量 = 目标账户接收量 + 明确的消耗/丢弃/溢出量。
3. 每次转移记录 SourceOwner/Area/Port、TargetOwner/Area/Port、ResourceIdentity、Quantity、RuleId/Version、Before/After Version 与结果。
4. 目标拒收时，资源留在源账户或按明确规则进入显式丢弃/补偿路径；不得无日志消失。
5. Ban 是作用域规则，不是全局资源污染标签。工作台 Ban 只约束其定义的工作台暂存区接收/使用语义，不因为 Jar 内含该液体就无条件禁止 Jar 移动或自耗。
6. Jar 内液体与游离液体是不同账户。Jar 自耗不能记成工作台游离液体扣除。
7. 主台 Ban 的液体不得由主台传给从台；主从液体通路保持单向。
8. 工作台 Jar 存储区只接收/输出 Jar；工作台液体暂存区只接收/输出游离液体。不得通过跨账户“打包”绕过任何区域策略。
9. 规则不支持、端口策略不透明、拓扑缺失或预算耗尽时，结果为 Rejected 或 Undetermined，绝不能假装成功或当成无资源需求。
10. 资源账户只有所属个体的领域端口可以修改；Board、Solver、UI 和 Host 不得直接改写账户内部状态。

## 8. 规则、算法与架构的边界

依赖方向必须保持：

Identity/Vocabulary -> Area & Port Contracts -> Rule Definitions -> Circuit Image -> SolveProblem -> Solver Strategy -> Resource Binding -> Admission/Lease -> Execution/Settlement

- Area/Port 定义资源边界与可调用操作，不知道订单求解算法。
- Rule Definition 描述约束和状态转换，不知道具体 UI、Board 渲染或搜索实现。
- Circuit Compiler 消费冻结结构和规则，产出不可变图像，不执行资源扣款。
- Solver 只消费 Circuit Image、目标和策略，不直接操作 Workshop/Jar 实体。
- Resource Binder 将符号路径匹配到真实 Jar 与账户。
- Admission 对当前余额和版本原子复验并建立 Lease。
- Executor 只执行获准转换并写入账本。
- Host/UI 只调用端口/用例，不得复制 Ban、容量、极性或资源守恒规则。

**禁止**在 Solver 或 Board Adapter 中写死“主台有几罐”“液体总量”“某个区域的内部 List/Dictionary”或按 UI 索引直接转移。角色、区域、端口和规则应通过身份与注册契约解析。

## 9. 现有实现映射与缺口（2026-11-25）

已存在的实现事实：

- Workshop 是独立领域对象，持有自己的 Jar 集合、游离液体暂存账户、能力和规则上下文。
- Workshop 对 Jar/液体集合提供只读视图，变更通过 AddJar/RemoveJar/TransferOut/TransferIn 与 AddLiquid/RemoveLiquid/TryReceiveLiquid/DrainLiquid 等领域方法。
- WorldSnapshot 分开保存 Master/Slave Workshop Profile、Ship Liquid Inventory 与资源指纹。
- Board Definition/Assignment、Owned Instance 与主从槽位角色已在上层模型中分离。
- Core 已有 ResourceEffect、staging admission、主从 Transfer 和订单资源范围的部分契约/适配器。

尚未完成、不能误报为已有：

- Area/Port 作为统一显式类型的完整领域契约；
- 自机 Board 的 Production/Workshop/Order Area 通用拓扑编译；
- 每个区域输入/输出端口的统一注册、连接和版本化；
- 将现有 Workshop 直接领域方法统一映射为 Port Adapter；
- 通用 Circuit Image 的构建、依赖指纹与持久化/重建策略；
- 将订单反向路径完整表达为多资源端口图，并与正向运行共用同一图契约。

迁移必须从适配层开始，不要一次性重写 Workshop 或现有资源账户。旧实现作为差分 Oracle，逐个端口建立等价性测试后再迁移调用方。

## 10. 下一阶段实施顺序

1. **领域词汇冻结**：为 Individual、Area、Port、Connection、Route、Circuit Image、Resource Account、Transfer Receipt 建立稳定术语和身份规则。
2. **端口契约**：定义 typed Input/Output Port、Receive/Withdraw 意图、Transfer Request/Receipt、拒绝原因和原子边界；先为 Workshop 两个区域与 Ship Liquid Inventory 建 Adapter。
3. **账户与端口差分测试**：验证旧 Workshop 方法与新 Port Adapter 完全同义，覆盖 Ban scope、容量、极性、Jar 内液体自耗、staging 拒收、主从单向和资源守恒。
4. **Board Area/Connection 模型**：为 Production/Workshop/Order 建立显式区域与槽位归属，区分 Board Definition、Assignment 和 Owned Instance。
5. **Circuit Compiler**：从冻结结构、端口注册表和规则版本生成不可变 Circuit Image；未知端口、缺失连接、重复资源所有权 fail-closed。
6. **订单路径图**：把 Ship Liquid Inventory 与 Master Jar ingress 作为源边界，把 Order Area 的需求端作为目标边界；先保留现有 A/B 与主从策略作为显式 Policy，再逐步解耦。
7. **统一执行**：求解、绑定、准入、运行和结算消费同一份 Port/Rule/ResourceEffect 契约。
8. **生产区接入**：单独映射生产元件的输入/输出和 Warehouse ingress；只有明确连接后才进入订单通路。
9. **结构审查与清理**：完成依赖规则、禁止引用、硬编码分类、缓存指纹、回放和压力样本后，才删除旧适配器与历史兼容层。

## 11. 完成定义

只有同时满足以下条件，才可称为符合 Jar 电路世界模型：

- 每个资源账户有唯一所有者和稳定身份；
- 每个 Area 有明确职责、类型、容量和访问策略；
- 所有跨 Area 资源流通过有类型的 Output -> Input Connection；
- 工作台 Jar 区和液体区分别拥有输入/输出契约；
- 自机液体账户与工作台液体暂存账户完全分离；
- Board Assignment 冻结的实例与连接可确定地产生 Circuit Image；
- 路径头部和订单尾部明确，所有中间转换可追踪；
- Solver 不包含资源账户写操作或玩法语义分支；
- 未知/不支持规则 fail-closed 或 Undetermined；
- 资源守恒、Ban 作用域、主从方向、Jar 生命周期和执行回放测试通过；
- Space/Godot 只消费稳定 Core 契约，不复制资源和规则语义。

本文定义目标模型；代码实现状态以第 9 节和最新工作记录为准。

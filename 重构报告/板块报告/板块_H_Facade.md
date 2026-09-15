# 板块 H — Facade（修复报告）

> 范围：`JarSystem.cs`、`Services/WorkshopService.cs`、`Services/OrderService.cs`、`Services/ProductionService.cs`、`Services/StateSerializer.cs`、`Services/StateDtos.cs`、`Services/SequentialIdGenerator.cs`、`Services/DefaultIdGenerators.cs`、`Bootstrap/SystemBootstrapper.cs`、`Abstractions/`（`ISolver`/`IIdGenerator`/`IJarSelectionStrategy`/`ISystemLog`）
> 阶段：1（批 2/3）+ 2（批 2A/2E）+ 3（DTO）
> 验证状态：**待验证**（`重构报告\verify.bat`）

---

## 1. 板块职责

门面（唯一对外入口）与组合根：持有世界状态与协作者、转发用例、装配默认世界、对外状态快照。

## 2. 重构前的问题（引用审计）

| 编号 | 问题 | 证据 |
|---|---|---|
| H1 | 12 个公共可写属性（含 `Checker`/`Executor`/`ProducerModule`/`EventHistory`） | `JarSystem.cs:14-25` |
| H2 | 默认数据硬编码在门面里 | `:33-45` |
| H3 | `SetMaster` 60 行编排 + `Dictionary<string,object?>` 弱契约返回 | `:47-100` |
| H4 | `ExecuteOrder` 五元组返回 + 直接改 `order.Status` | `:204-243` |
| H5 | 容量调整直接改集合/字典 | `:249-276` |
| H6 | 手工 `GetState`/`Serialize*` 字典树（弱类型契约） | `:292-338` |
| H7 | `Tick` 死返回值（恒 0） | `:144-160` |
| H8 | 门面即 God class（339 行，10+ 职责） | 全文件 |
| §2.2 | 全仓库 0 个 `interface`（无抽象层） | 审计 §2.2 |
| §2.5 | `Jar._nextGlobalId` 与 `Order._idCounter` 两处静态可变计数器 | 审计 §2.5 |

## 3. 改动清单

### 3.1 阶段 1/2A

| 文件 | 改动 |
|---|---|
| `Abstractions/ISolver.cs`（新） | 求解器契约；`class StateSpaceSolver : ISolver` 在编译期固定签名一致 |
| `Abstractions/IIdGenerator.cs`（新） | 唯一 ID 源契约 |
| `Abstractions/IJarSelectionStrategy.cs`（新） | 罐子选择策略契约（阶段 3 落地 5 个实现） |
| `Abstractions/ISystemLog.cs`（新） | 可观测性出口契约（预留：集中审计 §2.1 的空 catch 留痕） |
| `Services/SequentialIdGenerator.cs`（新） | `Interlocked.Increment`，返回 1,2,3…（与旧 `++_idCounter` 逐值相同） |
| `Services/DefaultIdGenerators.cs`（新） | **两个独立**默认计数器（罐子/订单）——合一会让 `GetState` 的 `id` 取值变化 |
| `Bootstrap/SystemBootstrapper.cs`（新） | 默认世界装配（4 生产者 → 酒/蜜各 20 → 2 模板台 → 青龙台 → 白虎台），工作台顺序与索引不变 |
| `JarSystem.cs` | `InitializeDefault()` 一行转发；容量调整改走 `Workshop.ResizeJarCapacity/ResizeLiquidCapacity`；`GetState` 改 `ToDictionary`；4 处 `FeasibilityResult` 写入改 `RecordFeasibility`；2 处 `Status` 写入改 `MarkSucceeded/MarkFailed` |

### 3.2 阶段 2E（门面瘦身）

| 新类 | 承接的用例 |
|---|---|
| `Services/WorkshopService.cs` | `CreateWorkshop`/`CreateRandomWorkshop`/`UpdateWorkshopCapacity`/`SetMaster`/`SetSlave` |
| `Services/OrderService.cs` | `CreateOrder`/`CreateRandomOrder`/`DeleteOrder`/`CheckFeasibility`/`CheckAllFeasibility`/`ExecuteOrder` |
| `Services/ProductionService.cs` | `Tick`/`UpdateProducerRate`/`UpdateLiquidProductionRate` |
| `Services/StateSerializer.cs` | `GetState` 快照（原 `SerializeWorkshop`/`SerializeOrder`） |

门面由 328 行 → 119 行：**保留全部 12 个状态成员与 22 个公开方法**，方法体改为一行转发。

### 3.3 阶段 3（强类型快照）

| 文件 | 改动 |
|---|---|
| `Services/StateDtos.cs`（新） | `StateDto`/`WorkshopStateDto`/`JarStateDto`/`OrderStateDto`/`FeasibilityStateDto`/`WarehouseStateDto`/`GlobalStateDto`，每个属性带 `[JsonPropertyName]` 冻结键名 |
| `Services/StateSerializer.cs` | 改为 DTO 投影（字段来源、别名、null 语义与旧字典一一对应） |
| `JarSystem.GetState()` | 返回类型 `Dictionary<string, object?>` → `StateDto` |

## 4. 等价性论证（要点）

1. **服务实时读取门面状态**（`internal ctor(JarSystem system)`，不缓存 `ProducerModule`/`LiquidProductionRate`）⇒ `Reset()` 或外部替换协作者后行为与重构前一致。
2. **公开 API 零变化**：12 个状态成员 + 22 个公开方法的签名与可写性全部保留（测试工程与策略实验把它们当注入口使用，属既有契约）。
   > ⚠️ **批 E 更新（本条已不再成立）**：状态成员的可写性已收口（集合改只读视图 + 9 个意图方法）；22 个公开**方法**未变。
   > 这是本项目唯一的源码级破坏性变更，理由与替换对照表见 `../08_批E_门面状态收口.md`。
3. **方法体逐行搬运**：`SetMaster`（事件历史 6 键与顺序、`switchEvent.Operations` 投影、主从自动解除）、`SetSlave`、`UpdateWorkshopCapacity`（先裁剪后计数、匿名对象形状）、`CreateRandom*`（`new Random()` 调用顺序）、`CheckFeasibility`（4 条早返回与写入时机）、`CheckAllFeasibility`（`{index, orderId, result}`）、`ExecuteOrder`（五元组 + 两分支事件历史键集合）、`Tick`（生产者累积 → 逐次产液体 → 主台流入）。
4. **ID 语义**：默认共享实例 + 两个独立计数器 ⇒ 罐子/订单 ID 与重构前逐值相同；`Reset()` 不清零。
5. **JSON 契约**：`StateDto` 的键名/层级/可空性与旧字典逐项核对一致（详见《03 阶段3》§3.1 的核对表）；`producerStatus`、`eventHistory`、`requirements` 仍保持原结构。

## 5. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| `dotnet build` | 0 error / 0 warning | 待回贴 |
| 全测试 | `0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic` | 待回贴 |
| Web 冒烟 | 创建/分析/执行/设主从/改容量/重置 + `/api/state` 字段与重构前一致 | 待回贴 |
| 契约快照 | `verify.bat` 第 4 步建立基线（键名/层级/值） | 待回贴 |

## 6. 遗留项

| 项 | 计划 |
|---|---|
| ~~状态成员仍公开可写（H1）~~ | ✅ **已完成（批 E）**：集合改对外只读视图 + `internal` 可变视图；新增 9 个意图方法（`AddWorkshop/ClearWorkshops/AddOrder/ClearOrders/SetMasterIndex/SetSlaveIndex/ResetProducerModule/SetLiquidProductionRate/SetFlowSettings`）；`ProducerModule` `private set`、`Checker`/`Executor` 只读；测试与策略实验 20 处注入点同批改写。见 `../08_批E_门面状态收口.md` |
| `producerStatus` 与 `eventHistory` 仍是字典结构 | 阶段 3-1b：继续 DTO 化（需同步改 `ProducerModule.GetProducerStatus` 与各事件写入处） |
| ~~`ISystemLog` 目前无实现/无注入点~~ | ✅ **已完成（批 A）**：新增 `Services/ConsoleSystemLog.cs`；`JarSystem.Log`（构造注入）；`OrderEvent`/`CommandExecutor` 带可选 `ISystemLog? log = null` 并透传。排查结论：真正需要它的只有 `CommandExecutor.Rollback`（`event_` 为 null 时会丢），Web 层 catch 属宿主职责、保持直写 stderr。见 `../07_批A_ISystemLog日志接线.md` |
| `Tick` 的 `int` 返回值（H7） | 保留（对外签名不变优先）；实际恒为 0，未来如无消费者可改 `void` |

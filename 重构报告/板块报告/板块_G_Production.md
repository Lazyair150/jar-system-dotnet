# 板块 G — Production（修复报告）

> 范围：`Production/ProducerModule.cs`（含 `Producer`、`ShapeProductionLine`）、`Services/ProductionService.cs`、`Bootstrap/SystemBootstrapper.cs`
> 阶段：1（批 3）+ 2（批 2E）
> 验证状态：**待验证**（`重构报告\verify.bat`）

---

## 1. 板块职责

生产者与中央仓库：按形状的生产速率累积产罐、产出液体、维护中央仓库（上限 200 罐 / 每种液体 200）、溢出丢弃计数，以及「仓库 → 主台」的流入（每 Tick 3 罐 2 液，主台基础形状优先）。

## 2. 重构前的问题（引用审计）

| 编号 | 问题 | 证据 |
|---|---|---|
| G1 | `Generators`/`Producers`/`Warehouse`/`LiquidWarehouse`/`DiscardedLiquids` 全部公开可变（含可替换集合） | `ProducerModule.cs:70-75` |
| G2 | `GetWarehouseJars()` 直接暴露内部列表引用（调用方仅用于取 `Count`） | `:190` |
| G3 | `FlowToMaster` 直接改 `master.LiquidStorage` 并自行计算容量 | `:180-184` |
| G4 | 状态以手工字典拼装（弱类型契约） | `:59-65,193-209` |
| G6 | `Tick` 返回 `List<Jar>`，上游忽略返回值 | `:114-129` |
| G7 | 类名 `ShapeGenerator` 与职责（按形状的生产线）不符 | `:21` |
| — | 默认世界的装配散在门面里（4 生产者 + 各 20 液体） | `JarSystem.cs:33-45` |

## 3. 改动清单

| 文件 | 改动 | 阶段 |
|---|---|---|
| `ProducerModule.cs` | `ShapeGenerator` → **`ShapeProductionLine`**（类名揭示职责；3 处引用同步） | 1（批 3） |
| `ProducerModule.cs` | `FlowToMaster` 的液体转移改走 **`master.TryReceiveLiquid(liquid, amount)`**：不再直改 `LiquidStorage`、不再自算容量；被禁/容量满时全额留仓库 | 1（批 3） |
| `Bootstrap/SystemBootstrapper.cs`（新） | 默认世界装配迁出门面：4 生产者（陶工甲/乙、机器A/B 及其形状速率）→ 酒/蜜各 20 → 2 个模板工作台 → 青龙台 → 白虎台 | 2A |
| `Services/ProductionService.cs`（新） | `Tick`/`UpdateProducerRate`/`UpdateLiquidProductionRate` 迁出门面；属性一律从门面实时读取（`Reset()` 会重建 `ProducerModule` 与速率） | 2E |

## 4. 等价性论证（要点）

1. **`FlowToMaster` 液体转移三条路径净效应相同**（逐条推导）：原实现在 `-= amount` 之后，
   - 被禁 → `+= amount`（净 0）→ 新实现 `TryReceiveLiquid` 返回 0（净 0）；
   - 容量不足 → `+= amount`（净 0）→ 同上（净 0）；
   - 成功 → `+= amount - accepted`（净 −accepted）→ 新实现按 `accepted` 直接相减（净 −accepted）。
   同时 `LiquidWarehouse` 的写法由「先减后加」改为「按实际接收量相减」，字典键集合与值一致。
2. **装配顺序与数值不变**：4 个生产者的名称/形状/速率、液体初值 20、工作台顺序（模板 2 个 → 青龙台 → 白虎台，索引 0..5）逐字保留；`JarSystem.InitializeDefault()` 仍公开、`Reset()` 调用链不变。
3. **`Tick` 三段顺序不变**：`ProducerModule.Tick(count)` → 逐次按速率 `ProduceLiquid`（`> 0` 才产出）→ 有主台时 `FlowToMaster(主台, FlowSettings.JarCount, FlowSettings.LiquidAmount)`；返回值恒 0 保留（对外签名不变）。
4. **`UpdateProducerRate`/`UpdateLiquidProductionRate` 语义不变**：前者委托 `ProducerModule.UpdateRate(name, shape, rate)`；后者仅在液体键存在时写入（`ContainsKey` 守卫保留）。
5. **类名替换无外部影响**：`ShapeProductionLine` 原为 `ShapeGenerator`，仅在 `ProducerModule` 内部使用（grep 证实），枚举/字典的键（形状名）不变。

## 5. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| `dotnet build` | 0 error / 0 warning | 待回贴 |
| 全测试（含容量压力 3000） | `0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic` | 待回贴 |
| 仓库上限与丢弃计数 | `warehouse.discardedJars/discardedLiquids` 与重构前一致 | 待回贴 |
| 策略实验 | 依赖 `Tick` + 流入的 711 模式实验成功率不变（FIFO 最高） | 待回贴 |

## 6. 遗留项（本板块**未做**的部分，诚实记录）

| 项 | 原因 / 计划 |
|---|---|
| `Generators`/`Producers`/`Warehouse`/`LiquidWarehouse`/`DiscardedLiquids` 仍公开可变（G1） | 未做。原因：策略实验与测试把它当注入口（`sys.ProducerModule = new ProducerModule();`、`sys.ProducerModule.LiquidWarehouse["蜜"]`）。收口计划与测试同步进行（阶段 3 后续） |
| `GetWarehouseJars()` 仍泄漏内部列表（G2） | 未做；当前唯一调用方是 `StateSerializer` 取 `Count`。建议后续改为 `int WarehouseJarCount`（或用只读视图） |
| `Tick` 返回 `List<Jar>` 被忽略（G6） | 未改（避免动对外签名）；实际调用方（`ProductionService.Tick`）不使用返回值 |
| `GetProducerStatus()` 与 `ShapeProductionLine.GetStatus()` 仍手工拼字典（G4） | 未做；已作为 `StateDto.ProducerStatus` 的后续 DTO 化项记录在《03 阶段3》§3 |

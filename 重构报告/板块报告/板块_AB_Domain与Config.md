# 板块 A+B — Domain 与 Config（修复报告）

> 范围：`Domain/Jar.cs`、`Domain/Workshop.cs`（含 `WorkshopSnapshot`）、`Config/Constants.cs`、`Config/Rules.cs`（含 `WorkshopTemplate`、`ShapeRules`）
> 阶段：阶段 1 批 1 + 批 2 + 批 3（部分）；阶段 2A 追加 ID 注入
> 验证状态：**待验证**（用户运行 `重构报告\verify.bat`）

---

## 1. 板块职责

- **Jar**：陶罐实体。不变量：形状 ∈ 8 种、`Liquid == null` 表示空罐、`IsMarked` 表达「一罐一操作」。
- **Workshop**：规则容器（基础/附加标记、禁令）+ 资源容器（罐子、游离液体）+ 资源操作原子步骤。
- **WorkshopSnapshot**：工作台状态备忘录（切换事件回滚点）。
- **Constants / Rules / ShapeRules**：形状与液体枚举、工作台模板、形状转换规则唯一来源。

## 2. 重构前的结构问题（引用审计）

| 编号 | 问题 | 证据 |
|---|---|---|
| A1/A4 | `Jar` 四个属性全部 `get; set;`，外部可绕过不变量直接赋值 | `Jar.cs:14-17` |
| A2 | 静态可变全局 ID 计数器 | `Jar.cs:12,20` |
| A3 | 形状校验重复实现（构造函数 + `Reshape`） | `Jar.cs:24,69` |
| A5/A6 | `Workshop` 的罐子列表/液体字典直接暴露，跨模块可增删改 | `Workshop.cs:15-16`；11 处绕过封装（审计 §2.3） |
| A7 | 命名无法揭示意图（`Drink` = 支付改形成本） | `Workshop.cs:48-49` |
| A9/A10 | 快照可被外部篡改；恢复语义与快照字段不一致 | `Workshop.cs:110-135` |
| B1 | `WORKSHOP_TEMPLATES` 是属性 → 每次访问重新分配 | `Rules.cs:8-12` |
| B3 | `ShapeRules` 存在与 `else` 完全同体的死分支 | `Rules.cs:37-46` |
| B4/B5/B6 | 字面量魔法字符串、缺常量类、`LIQUID_CAPACITY` 死常量 | `Constants.cs:9,16-28` |

## 3. 改动清单

### `Domain/Jar.cs`（整体重写）

| 改动 | 说明 |
|---|---|
| `Shape`/`Liquid`/`IsMarked` → `private set` | 状态只能经意图方法变更 |
| `Id` → `internal set` | 仅 Core 内部（`Workshop.AddJar`）可分配 |
| 新增 `Unmark()`、`RestoreLiquid(string?)` | 取代外部 `Jar.IsMarked = false` / `Jar.Liquid = x`（4 处调用点） |
| 新增 `IsValidShape(string)` | 形状校验单一实现（构造与改形共用） |
| `Clone()` → `DeepClone()` | 显式命名，消除 `ICloneable` 语义歧义 |
| 新增 `Id`/成员 XML 注释 | 说明不变量与 ID 归属规则 |
| **阶段 2A**：删除 `_nextGlobalId` 与 `NextId()` | ID 源改由 `IIdGenerator` 注入到 `Workshop` |

### `Domain/Workshop.cs`（整体重写）

| 改动 | 说明 |
|---|---|
| `_jars`、`_liquidStorage` 私有化；对外 `IReadOnlyList<Jar>` / `IReadOnlyDictionary<string,int>` | 切断审计 §2.3 的全部 11 处直接改写 |
| 新增意图方法：`TryReceiveLiquid`（返回实际接收量）、`DrainLiquid`、`ResizeJarCapacity`、`ResizeLiquidCapacity`、`ClearMarks`、`TransferOut`/`TransferIn`（统一出口） | 容量/禁令/记账集中在一处 |
| `Drink` → `TryPayReshapeCost`；`CanDrink` → `HasUsableLiquid` | 命名揭示意图（grep 证实无外部调用者，可安全改名） |
| 删除无调用者的 `TransferLiquid` / `ReceiveLiquid` | 死代码（调用方原先绕过它们直改字典） |
| `WorkshopSnapshot` → 不可变（`init` + 只读集合） | 备忘录不可被篡改 |
| `RestoreFromSnapshot` 改为清空后按快照重填 | 内容语义与旧实现一致（见 §4.4） |
| **阶段 2A**：构造函数追加可选参数 `IIdGenerator? ids = null`，`AddJar` 用 `_ids.Next()` | 源码兼容；默认共享实例保持全局唯一语义 |

### `Config/Constants.cs` / `Config/Rules.cs`

| 改动 | 说明 |
|---|---|
| 删除 `LIQUID_CAPACITY` | 死常量（grep 无引用） |
| 新增 `OrderPhase` / `CheckMethod` / `SwitchCaseType` 常量类 | 值为行为契约（`none/master/slave`、`single_phase/slave_only/partition`、`same_bans/different_bans`），供替换散落字面量 |
| `WORKSHOP_TEMPLATES` → `static readonly` | 不再每次访问重新分配数组 |
| `WorkshopTemplate` → `init` + `IReadOnlyList<string>` | 模板不可变 |
| `ShapeRules.GetPossibleTransforms` 参数放宽为 `IReadOnlyList<string>`；合并同体死分支 | 行为等价（两条分支结果本就相同） |

**阶段 2A 追加**：`Bootstrap/SystemBootstrapper.cs` 承接原 `JarSystem.InitializeDefault` 的默认数据装配（见 H 板块报告）。

## 4. 等价性论证

1. **只读视图**：`IReadOnlyList<Jar>`/`IReadOnlyDictionary<string,int>` 由私有 `List`/`Dictionary` 支持，读取语义（`Count`、索引器、`Values`、`Select`、`GetValueOrDefault`）与原来完全一致；原先依赖 `List` 特有方法的 3 处（`GetRange/RemoveRange`、`Remove`）全部改为意图方法，语义见下条。
2. **`ResizeJarCapacity`**：原实现「当 `maxJars < Count` 时取 `GetRange(maxJars, rc)` 后 `RemoveRange(maxJars, rc)`」= 丢弃尾部 rc 个 → 新实现 `RemoveRange(maxJars, discarded)` 后返回 `discarded`，调用方在原触发条件下记账 ⇒ 逐罐相同。
3. **`TryReceiveLiquid`**：内部即 `min(amount, MaxLiquids − cur)`，与原先散落在执行器/回滚/仓库流入处的三段自算容量逐式相同；禁令判断与 `AddLiquid` 一致。
4. **`RestoreFromSnapshot`**：原实现 `Jars = 快照罐子; LiquidStorage = new(快照液体)`；新实现先 `Clear()` 再逐项填入 ⇒ 内容与键集合相同（快照始终包含 `酒/蜜` 两个键，故不会丢失零值键）。
5. **`ShapeRules` 死分支合并**：原 `else if (!base.Contains && !add.Contains)` 与 `else` 分支体完全相同，合并后插入顺序（`HashSet` 插入序）不变 ⇒ 返回列表顺序不变。
6. **ID 注入（2A）**：`SequentialIdGenerator.Next()` 返回 1,2,3…；旧 `Jar.NextId()`（`_nextGlobalId++`，初值 1）与 `Order`（`++_idCounter`，初值 0）首次调用同样得到 1 ⇒ **ID 取值逐值不变**；罐子与订单仍是两个独立计数器，故 `GetState` 中 `jars[].id` / `orders[].id` 与重构前一致。
7. **`Unmark`/`RestoreLiquid`**：方法体即原来的赋值语句，取值语义不变。

## 5. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| `dotnet build` | 0 error / 0 warning | 待回贴 |
| `JarSystem.Test` | `0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic` | 待回贴 |
| 测试兼容性（已逐条核对 `test/JarSystem.Test/Program.cs`） | `master.Jars.Count/[0].Shape/.Liquid/.Any`、`LiquidStorage["酒"]/.Values/.Select`、`AddJar(new Jar(...))`、`AddLiquid`、`CreateSnapshot()`、`ws.Ban.Count/Contains`、`new Workshop(name,bm,am,ban,20,30)` 全部仍可编译 | 静态核对通过 |

## 6. 遗留项

| 项 | 原因 / 计划 |
|---|---|
| `Workshop` 的 `Name/BaseMarks/AdditionalMarks/Ban/MaxJars/MaxLiquids` 仍可写 | 阶段 3 处理：`BaseMarks/Ban` 改只读 + Case2 的「临时附加标记」改为作用域对象（`using`），一并消除审计 A5 与 C15 |
| `WorkshopSnapshot` 只恢复罐子与液体，规则/容量字段仅作诊断 | 审计 §2.10 记录的不一致；阶段 3 `Memento` 规范化时决定是否恢复全字段（当前唯一调用点下两者等价，故未擅改） |
| `Workshop.PourIn/PourOut/ReshapeEmpty/ReshapeFull` 仍返回 `(bool, string?)` 且错误为中文文案 | 阶段 2B：改为结果对象（可判定错误码 + 文案分离） |
| `Jar.Liquid == null` 表示空罐 | 阶段 3 评估 `Null Object`（`Liquid.None`）；当前不引入以避免全库大改 |

# 07 批 A：`ISystemLog` 日志接线

> 状态：**✅ 已由用户本机验证通过**（四步 `exit=0`、构建 0 错误、测试基线不变、契约快照 165 行）。
> 范围：`Abstractions/ISystemLog.cs`、`Services/ConsoleSystemLog.cs`（新）、`Commands/CommandExecutor.cs`、
> `Orders/OrderEvent.cs`、`Services/OrderService.cs`、`JarSystem.cs`。
> **不改 JSON 契约、不改 `eventHistory` 键、不改规则与执行行为**；唯一新增的可观测输出是 stderr 上的诊断行。

---

## 1. 背景：审计 §2.1 的"丢失的日志"到底丢在哪

审计把"散落的静默 catch"列为 P0，阶段 2 为此定义了 `ISystemLog` 接口，但一直**没有接线**（`04_重构总结.md` §5 记录的遗留项 A）。
本次先做**逐处排查**，确认"究竟哪些错误真的被丢掉了"，避免为了"用上接口"而到处加日志：

| 位置 | 现状 | 是否丢信息 |
|---|---|---|
| `CommandExecutor.Rollback` 的 catch | `event_?.Errors.Add(...)` | ⚠️ **会丢**：`event_` 为 null 时整条丢失（代码注释本身写着"原 Node 版此处是 console.error，移植到 .NET 时丢失了该日志"） |
| `WorkshopSwitchEvent` 的 catch | 写入 `SwitchOperationLog` + 返回 `Reason` | 不丢（进契约 + 返回给调用方） |
| `OrderService.CreateOrder` 的 catch | `return (false, e.Message)` | 不丢（返回给调用方，Web 层转成 `{type:"error"}`） |
| `Web/WebSocketHub` 的 catch | `Console.Error.WriteLine` | 不丢（可见），属宿主层 |
| `Web/CommandDispatcher` 两个定时器 catch | `Console.Error.WriteLine` | 不丢（可见），属宿主层 |
| `WebSocketHub` 收尾的空 catch | `catch { }` + 注释 | 不丢（设计如此：对端已断开，无补救动作） |

**结论**：Core 里真正"静默丢失"的只有一处 —— 回滚失败且无事件上下文。因此本批**只接这一处**，
其余位置保持原样（加日志只会制造噪音，不改信息量）。

---

## 2. 改动清单

| # | 文件 | 改动 |
|---|---|---|
| 1 | `Services/ConsoleSystemLog.cs`（**新建**） | `ISystemLog` 默认实现：`Error` → stderr（带异常类型与消息）、`Info` → stdout；`public static readonly Instance` 共享实例 |
| 2 | `Abstractions/ISystemLog.cs` | 补**契约文档**：① 只用于"原本会被静默丢弃"的诊断，不替代已进契约的错误；② 实现方**不得抛异常**（日志失败不得影响领域流程）；③ 不得反向调用 `JarSystem` |
| 3 | `Commands/CommandExecutor.cs` | 新增构造参数 `ISystemLog? log = null`（null → 默认控制台日志）；回滚失败 catch 内**追加** `_log.Error(...)`，原有 `event_?.Errors.Add(...)` 逐字保留 |
| 4 | `Orders/OrderEvent.cs` | 构造追加可选参数 `ISystemLog? log = null`，并把它透传给自有的 `CommandExecutor`（原先是 `Executor { get; set; } = new()`，现改为构造内装配） |
| 5 | `Services/OrderService.cs` | `new OrderEvent(order, master, effectiveSlave, _system.Log)` |
| 6 | `JarSystem.cs` | 新增 `public ISystemLog Log { get; }` + 构造可选参数 `JarSystem(ISystemLog? log = null)`（默认 `ConsoleSystemLog.Instance`） |

**兼容性**：`new JarSystem()`、`new OrderEvent(o, m, s)`、`new CommandExecutor()` 三种既有写法全部继续可用（可选参数追加在末尾）。

---

## 3. 等价性论证

1. **契约侧零改动**：`event_?.Errors.Add(new LogError { Phase/Command/Reason })` 的**位置、顺序、内容一字未动**；
   新增的只有紧随其后的一行日志调用。`LogError` 仍按原样进入 `eventHistory[..].operations`/报告。
2. **控制流零改动**：不新增分支、不提前 return、不吞异常；`Rollback` 仍然是"单条撤销失败不中断其余回滚"。
3. **新增输出不属于任何契约**：stderr 文本不参与 JSON、不参与测试断言（测试断言的是 bug 计数与罐子/液体守恒）。
4. **默认装配风格一致**：与 `DefaultIdGenerators`（默认给出真实现）、`FeasibilityChecker()`（默认装配真求解器）一致
   —— 默认即可用，宿主可注入替换；这也是**故意不引入 Null Object** 的原因（同 `03_阶段3` §1 的评估结论）。
5. **触发条件极窄**：只有"命令 `Undo` 抛异常"才会走到该行。正常路径（含 2000 随机 + 3000 容量用例）不触发。

---

## 4. 边界：Core 的日志 vs 宿主的日志

| 层 | 负责什么 | 去向 |
|---|---|---|
| Core（本批接线） | 领域内**否则会丢**的诊断（当前：回滚失败） | 经 `ISystemLog`，默认 stderr；宿主可注入文件/采集器 |
| Web 宿主 | 连接生命周期、定时器异常（`WebSocketHub` / `CommandDispatcher`） | 仍直接写 `Console.Error` —— 这是宿主自身的关注点 |

宿主若要统一去向（例如全部落文件），做法是：实现一个 `ISystemLog`，在组合根 `JarSystemService`/`Program.cs`
里 `new JarSystem(myLog)`，并把同一实例也给 Web 层的 catch 使用。**本批未改 Web 层**（避免把宿主职责塞进 Core 抽象）。

---

## 5. 验证方法（用户本机）

```bat
cd /d D:\codex\jar-system-dotnet
重构报告\verify.bat
```

期望：

1. 四步 `exit=0`；
2. 构建 `0 错误`（全量重编译时同时 `0 警告`）——特别留意 `CS8618`（`OrderEvent.Executor` 由构造赋值，不应再报未初始化）；
3. `=== 0 bugs / 2000 random + 0 bugs / 3000 capacity + 20 deterministic ===`；
4. 契约快照仍 **165 行**（本批没碰任何 DTO）；
5. **注意观察是否出现 `[error] 回滚失败: ...` 行**：若出现，说明随机测试里确实存在"回滚撤销异常"的用例，
   这正是原先被丢掉的信息 —— 请把它贴回来，我据此判断是否需要进一步定位（不影响测试通过与否）。

---

## 6. 仍待办

**只剩 E（门面状态收口）**：`JarSystem` 的 `Workshop(s)`/`Orders`/`CurrentMaster`/`CurrentSlave`/`ProducerModule`/
`CurrentEvent`/`EventHistory`/`Checker`/`Executor` 等成员公开可写，外部可绕过规则直接改世界状态。
收口必须**与测试工程同批修改**（测试把它们当注入口使用），单独一批做，改完仍需一次完整验证。

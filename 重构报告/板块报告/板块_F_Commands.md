# 板块 F — Commands（修复报告）

> 范围：`Commands/Command.cs`、`Commands/CommandExecutor.cs`、`Commands/JarCommands.cs`
> 阶段：阶段 1 批 3（含用户裁决的 Undo 修正）；阶段 3 计划做命令契约强类型化与日志装饰器
> 验证状态：**待验证**（新增回归用例 S20）

---

## 1. 板块职责

命令模式：把「倒液 / 倒出 / 改形 / 转移」封装为可执行、可撤销、可序列化的原子单元；`CommandExecutor` 负责单条执行、批量事务与逆序回滚；`OrderEvent` 是命令进入真实状态的唯一闸门（阶段/工作台校验）。

## 2. 重构前的问题（引用审计）

| 编号 | 问题 | 证据 |
|---|---|---|
| F1/F3 | `Command.Execute/Undo` 返回 `object`，调用方靠 `is CommandResult` / `is bool` 双轨判定；多态契约丢失 | `Command.cs:29-32`、`CommandExecutor.cs:21-25,36-41` |
| F2 | 基类 `Executed/Result/Timestamp` 公共可写 | `Command.cs:15-17` |
| F4 | **回滚失败被静默吞掉**（`catch (Exception) { }`），且丢失了原 Node 版的 `console.error` | `CommandExecutor.cs:59-60`（对照 `jar-system-v4.0-logic-fixed/src/commands/CommandExecutor.js:46-48`） |
| F5 | 死字段 `Pending`（只声明，从不读写） | `CommandExecutor.cs:15` |
| F6 | `PourInCommand.Undo` 账目错误且必然失败（历史潜伏 bug） | `JarCommands.cs:27-38`；溯源见 §4.1 |
| F7 | 直接改 `Jar` 状态（`Jar.IsMarked = false`、`Jar.Liquid = ...`、直改 `LiquidStorage`） | `JarCommands.cs:78,167-171` |
| F8 | `PourOutCommand` 同时持有 `OwnerWorkshop` 与 `Execute(workshop)` 参数，语义重叠 | `JarCommands.cs:136-175` |
| — | `CommandResult.Data` 只写不读（5 处赋值，0 处读取） | `Command.cs:51` |

## 3. 改动清单

| 文件 | 改动 | 对应 |
|---|---|---|
| `Command.cs` | 删除 `CommandResult.Data`（只写不读） | 审计 §2.8（用户确认删除范围） |
| `CommandExecutor.cs` | 删除死字段 `Pending` 及 `Clear()` 中的引用 | F5 |
| `CommandExecutor.cs` | `Rollback` 的 `catch` 改为把失败写入 `OrderEvent.Errors`（`Phase="rollback"`，`Command`=命令类型，`Reason="回滚失败: …"`） | F4（恢复原 Node 版可观测性） |
| `JarCommands.cs` | **`PourInCommand.Undo` 重写**：`Unmark()` → `PourOut` → `Unmark()`；删除重复返还的 `AddLiquid(Liquid, 1)` | F6（用户裁决"修正账目 + 补测试"） |
| `JarCommands.cs` | `ReshapeEmptyCommand.Undo` 用 `Jar.Unmark()`；`PourOutCommand.Undo` 用 `OwnerWorkshop.RemoveLiquid` + `Jar.RestoreLiquid` + `Jar.Unmark()` | F7 |
| `test/JarSystem.Test/Program.cs` | 新增 **S20**：`Execute` 后断言「罐满 + 已标记 + 库存 2→1」，`Undo` 后断言「罐空 + 未标记 + 库存回到 2」 | 补上该方法此前 0 覆盖 |

## 4. 等价性论证

### 4.1 `PourInCommand.Undo`：为什么旧实现是"双重死代码"

- **不可达**：`Undo` 只在批量事务失败时被调用，而全系统唯一批量事务是 B 类订单的罐子转移批次（只含 `TransferCommand`）——倒液/倒出/改形都走 `OrderEvent.ExecuteCommand` 单条路径。这一点在**原 Node 项目里同样成立**（`JarCommands.js:19-28` 同构；`executeBatch` 唯一调用点是 `IndependentExecutor.js:180-181`）。
- **即便被调用也必然失败**：`Execute` 成功后罐子已被 `Mark()`，而 `Workshop.PourOut` 第一条守卫就是「罐子已被操作」→ 返回失败 → 后面的 `AddLiquid` 永不执行。
- **如果那一步真被执行，账目会错**：`PourOut` 已把罐内液体加回存储，再 `AddLiquid(Liquid,1)` 就是同一份液体被重复返还（库存 +2 而非 +1）。

⇒ 新实现三处都对齐：先清标记（让 `PourOut` 的守卫通过）→ 倒回液体（库存复原）→ 再次清标记（恢复"未操作"状态）。由于该方法在运行期不可达，**本修正不产生任何可观测行为变化**；它消除的是将来启用撤销栈时的隐患，并由 S20 固定契约。

### 4.2 回滚日志

原语义"单条撤销失败不中断其余回滚、不向上抛"完全保留；新增的只是 `OrderEvent.Errors` 里的一条记录。该列表会出现在 `event_.GetReport()["errors"]`，因此**仅当回滚真的抛异常**（当前测试与游戏路径从不发生，唯一可回滚命令是 `TransferCommand`，其 `Undo` 不抛异常）时，JSON 的 `errors` 会多一条。这是"恢复被移植丢失的可观测性"的必要代价，已在此显式记录。

### 4.3 其余

- `RemoveLiquid(OriginalLiquid)` 等价于原 `LiquidStorage[l]--`（都在 `c >= 1` 时扣 1）；`RestoreLiquid`/`Unmark` 即原赋值语句。
- 删除 `Data` 不改变任何读取方（grep：0 处读取）。

## 5. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| `dotnet build` | 0 error / 0 warning | 待回贴 |
| `JarSystem.Test` | 确定性用例数由 19 变 **20**，且 `S20-倒入命令撤销后液体与标记复原` 输出 `PASS` | 待回贴 |
| 随机套件 | `0 bugs / 2000`、`0 bugs / 3000` 不变（命令层改动不影响这两条路径） | 待回贴 |
| B 类订单回滚（S13/S19 覆盖） | 结果与重构前一致 | 待回贴 |

**S20 的可判定性**：在旧实现下 `Undo` 返回 `Success = false`（被标记守卫拒绝）⇒ 断言 `undone?.Success == true` 必然失败；在新实现下必然通过。因此该用例对本次修正是**有效判据**，而不是恒真断言。

## 6. 遗留项

| 项 | 计划 |
|---|---|
| `Command.Execute/Undo` 返回 `object` + 调用方 `is` 判定（F1/F3） | 阶段 3：改为强类型结果（`ICommandResult` 或泛型 `Command<TResult>`），同时去掉 `OrderEvent.ExecuteCommand` 里的 `is XxxCommand` 日志分支（C11） |
| 基类可变状态 `Executed/Result/Timestamp`（F2） | 阶段 3：改 `protected set` + 提供 `MarkExecuted()` |
| `PourOutCommand` 的 `OwnerWorkshop` 与参数 `workshop` 语义重叠（F8） | 阶段 2C：统一为构造注入的 Owner，消除"两处工作台"的可能性 |
| `Params` 弱类型字典（`Dictionary<string, object?>`） | **仍未做**：阶段 3-D 的 `SolverStepDto` **只覆盖路径步骤**，未触及 `Command.Params`（它是命令历史的 JSON 载荷，单独一批，见 `../06_阶段3D_路径步骤强类型化.md` §9） |
| 命令日志文案在 `OrderEvent` 与命令内部重复构造 | 阶段 3：`Decorator`（日志装饰器）作为单一出口 |

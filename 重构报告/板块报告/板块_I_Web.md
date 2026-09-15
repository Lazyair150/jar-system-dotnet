# 板块 I — Web 胶水层（修复报告）

> 范围：`src/JarSystem.Web/`：`Program.cs`、`CommandDispatcher.cs`、`WebSocketHub.cs`、`IWebSocketSink.cs`、`JsonArgs.cs`、`JarSystemService.cs`
> 阶段：1（批 3）+ 2（批 2F）
> 验证状态：**待验证**（`重构报告\verify.bat` + 浏览器冒烟）

---

## 1. 板块职责

开发/演示用 Web 壳：静态文件托管、`/api/state` 查询端点、`/ws` 原生 WebSocket（19 个命令、9 种消息）、单用户串行闸门、2 秒 Tick 与 5 秒自动检测两个定时器。**不含任何领域规则**。

## 2. 重构前的问题（引用审计）

| 编号 | 问题 | 证据 |
|---|---|---|
| I1 | 空 `catch` 吞异常（WS 读循环、发送、两个定时器），故障无痕迹 | `Program.cs:38,49,141,146` |
| I2 | 90 行 `switch` 命令分派挤在顶层函数里 | `:64-152` |
| I3 | 用 `idx < sys.Orders.Count` 反查 `orderId` | `:133-134` |
| I5 | `JarSystemService` 公共可写（`Timer`/`Clients`/`Gate`） | `:196-205` |
| I6 | JSON 取值辅助是顶层局部函数（无法复用/测试） | `:162-183` |
| — | 顶层语句文件承载全部职责（事务脚本化） | 全文件 |

## 3. 改动清单

### 3.1 阶段 1（批 3）

| 改动 | 说明 |
|---|---|
| 4 处空 `catch` → 记录到 stderr | `[ws] 连接异常`、`[ws] 发送失败`、`[timer]`（两个定时器）；WS 关闭的收尾 `catch` 保留但加**意图注释**（"连接可能已被对端关闭，关闭失败无补救动作"） |

### 3.2 阶段 2F（拆分）

| 文件 | 内容 | 原对应 |
|---|---|---|
| `JsonArgs.cs`（新） | 静态类：`Options`（camelCase）+ `J/JS/JI/JD/JB/JIOrNull/JL/ParseReqs` | 顶层局部函数，容错语义逐行保留 |
| `IWebSocketSink.cs`（新） | 推送出口接口（`SendAsync`/`BroadcastAsync`）——依赖倒置，便于替换与单测 | 新增抽象 |
| `WebSocketHub.cs`（新） | 连接注册、`init` 推送、接收循环、异常/收尾、广播与死连接清理 | 原中间件 + `WsSend`/`Broadcast` |
| `CommandDispatcher.cs`（新） | 19 个命令的 `switch` + `ResolveOrderIndex`；`HandleAsync` 承担串行闸门 | 原 `HandleCmdCore` + `HandleCmd` |
| `JarSystemService.cs`（新） | 组合根（系统实例 + 闸门 + 两个定时器）；删除因迁移失效的 `Clients` 字段 | 原文件底部类 |
| `Program.cs`（重写） | 205 行 → 39 行：`AddSingleton` + `/api/state` + 静态文件 + `/ws` 委派 + `app.Run()` | — |

## 4. 等价性论证（要点）

1. **契约逐字保留**：19 个命令名、9 种消息名、每个 payload 的键集合与产生顺序、错误文案（`订单不存在`/`切换失败`/`设置从台失败`）全部照搬；`setMaster` 的 `workshopSwitched` 触发条件、`checkFeasibility` 的 `orderId` 计算、`ResolveOrderIndex` 的「`orderId` 优先、`orderIndex` 兼容」顺序不变。
2. **闸门语义不变**：`HandleAsync`（命令）与两个定时器回调、`/api/state` 仍共用同一 `SemaphoreSlim(1,1)`；定时器仍是「取闸 → Tick/分析 → 释放 → 广播」，避免持锁广播。
3. **序列化不变**：`JsonArgs.Options` 与原 `jsonOpts` 相同；`J(object)` 签名不变（匿名类型按运行时类型序列化）；消息外层始终是 `{ type, data }`。
4. **传输语义不变**：非 Open 连接不发送、发送异常写 stderr；广播为「快照遍历 → 存活者发送 → 收集死连接 → 统一移除」；会话收尾为「移除注册 → 尝试正常关闭（失败忽略）」。
5. **`/ws` 判定不变**：`Path == "/ws" && IsWebSocketRequest`，否则 `next(ctx)`。
6. **`/api/state` 不变**：仍在闸门内返回 `GetState()`（阶段 3 后返回 `StateDto`，序列化键名与旧字典一致）。

## 5. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| `dotnet build` | 0 error / 0 warning | 待回贴 |
| 启动 | `dotnet run --project src\JarSystem.Web --urls http://localhost:3457` 正常监听 | 待回贴 |
| WS 冒烟 | `createOrder → checkFeasibility → executeOrder → setMaster → setSlave → updateWorkshopCapacity → deleteOrder → reset` 全链路正常，`state` 推送字段与前端预期一致 | 待回贴 |
| `/api/state` | 返回结构与 `GetState()` 一致（键名不变） | 待回贴 |
| 界面 | 工作台卡片高亮、B 类转移动画（`transferOccurred`）、切换动画（`workshopSwitched`）、容量编辑不再被打断 | 待回贴 |

## 6. 遗留项

| 项 | 计划 |
|---|---|
| `JarSystemService` 字段仍公共可写（I5） | 未改（本批按计划"字段可见性本阶段不动"）；后续可改为 `internal set` / 只读 + 方法 |
| 命令分派仍是 `switch` 而非**表驱动**（I2 部分） | 已集中到 `CommandDispatcher` 一个类；是否改为「命令名 → 处理器」字典属结构偏好，收益有限，暂缓并记录 |
| `JsonArgs` 的短名 `J/JS/JI/JD/JB/JL` | 保留（内部辅助；`J` = 序列化，语义在 XML 注释里）。若你希望改为 `Serialize/GetString/GetInt…` 我可以改名（纯改名、无行为影响） |
| `Idx < Orders.Count` 反查（I3） | 已实现为「按 `orderId` 查 `FindIndex`」（`ResolveOrderIndex`）；旧的 `orderIndex` 参数仍兼容（为保证前端旧缓存可用） |
| 多客户端并发 | 仍为单用户串行闸门定位（文档已注明），多人需另做同步层 |

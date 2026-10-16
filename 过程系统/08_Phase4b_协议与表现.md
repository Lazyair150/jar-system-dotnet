# Phase 4b 报告：过程进入协议与表现层

> 生成时间：2026-10-16　生成者：Codex
> 格式沿用「板块报告」：职责 → 改动 → 验证 → 过程中发现的真实问题 → 遗留。

---

## 1. 职责

把"过程"从**核心内部机制**变成**客户端能用、能看、能取消**的东西：
两条命令（`startOrder` / `cancelProcess`）+ 五条生命周期消息 + 前端消费（徽标、取消、分步执行按钮）。

## 2. 改动清单

| 层 | 文件 | 改动 |
|---|---|---|
| 核心 | `Process/Ports/IProcessObserver.cs` | **新增**：过程观察者（**单回调** `OnChanged(instance, reason, tick)`） |
| 核心 | `ProcessRun.cs`、`ProcessSystem.cs` | 生命周期四处（started/blocked/resumed/finished）+ 单位推进处（progressed）通知观察者 |
| 核心 | `ProcessSystem.Cancel`、`JarSystem.CancelProcess`、`WorldOps.CancelProcess`、`SessionReplay` | 取消进行中的过程：工作体标失败 ⇒ 收尾落账（已完成步不回滚）⇒ 过程以 `aborted` 结尾 |
| Web | `ProcessObserver.cs` | **新增**：把过程变化翻成 5 条消息（`processStarted/Progressed/Blocked/Resumed/Finished`），经 `IWebSocketSink` 广播 |
| Web | `JarSystemHosting.cs`、`JarSystemService.cs` | 注册观察者并挂到系统上（组合根接线） |
| Web | `CommandDispatcher.cs` | 新增 `startOrder`、`cancelProcess` 两条命令（载荷：`orderId`/`orderIndex`、`processId`） |
| 前端 | `index.html`、`app.js` | 执行弹窗加"⏱ 分步执行"；订单卡显示过程徽标（进度 / 剩余刻 / 阻断原因）并可取消；`handleMessage` 处理 5 条过程消息（记一笔 + 刷新） |
| 测试 | `Program.cs` | 协议清单 +2 命令 / +5 消息、5 条消息的载荷键声明、S30 用例；**协议守卫的扫描源与载荷键提取源扩展到 `ProcessObserver.cs`**；`contract.d.ts` 自动新增两个载荷接口 |

### 关于观察者的设计偏离（诚实记录）

P0 规格 §4 写的是五个具名回调；实际落地收成**一个** `OnChanged(instance, reason, tick)`：

1. `totalSteps` 在过程建立时**还不知道**（计划随后才声明、或按步推进），"Started 一次性给全字段"这条路走不通，
   宿主终究要在每次变化时读实例；
2. 单回调让**顺序**天然正确（按发生顺序通知），五个回调容易出现"谁先谁后"的隐式约定；
3. 宿主按 `reason` 决定发哪条消息，映射一目了然（见 `ProcessObserver`）。

## 3. 验证

| 项 | 结果 |
|---|---|
| 协议 | **`PROTOCOL-OK (commands=20, messages=15)`**；逐条载荷键核对全过（`processStarted: OK (5)` 等） |
| 主测试 | `0 bugs / 2000 random + 0 bugs / 3000 capacity + **30 deterministic**`（新增 S30） |
| 文档口径 | `docs baseline: OK (9 carriers say 30)` |
| 契约快照 / Schema / 配置世界 / 求解器差分 | 全部**未变**（协议扩展不动 `state`；`CONFIG-WORLD-MATCH`；`SOLVER-OK (compared=4003, …)`） |
| 会话回放 | `REPLAY-OK (commands=33, stateBytes=3125, logBytes=8797, journalBytes=1415)`（未变） |
| JS | `JS-OK`（`node --check` 覆盖新增的前端代码） |
| 末尾判定 | `VERDICT=OK`（18.7 秒） |

## 4. 过程中发现的真实问题

**协议守卫的载荷键提取源只扫了 `CommandDispatcher.cs`**（以及 `JsonArgs.cs`）。新增的 5 条过程消息
由 `ProcessObserver.cs` 发出，于是守卫报 `protocol reply-payload: NOT-FOUND processProgressed …`：
消息名能提取到（名字扫描源已含观察者），但**载荷键找不到**。

修法（两处，都是"让守卫的视野跟上代码的分布"）：

1. 消息名与**载荷键**的扫描源都加上 `ProcessObserver.cs`；
2. 载荷键提取调用从 `ExtractPayloadKeys(dispatcher, …)` 改为 `ExtractPayloadKeys(payloadSources, …)`
   —— 后者才是"全部会发消息的文件"。

这条正好印证守卫的设计价值：**新文件、新消息、新载荷键，一处没同步就会被机器抓住**。

## 5. 遗留

1. **`checkJs` 全量校验**：门里只做 JS 语法检查（`node --check`）；字段名/消息名的类型检查靠编辑器
   或手动 `tsc --noEmit`（`contract.d.ts` 已含新类型，属已知的做法）。
2. **同步轨也会发过程消息**（有意的：过程日志与消息口径一致）；前端目前对两条轨都打日志、
   只在有活动过程时显示徽标 —— 若将来只想让异步轨进日志，改 `ProcessObserver` 一处即可。
3. **数据驱动的修饰器/事件**：现在仍由宿主代码构造（含 `Require` 的谓词）；
   关卡 JSON → 修饰器/事件的映射是内容侧设计。
4. **`Intensity` 的更多消费者**（产出/容量/丢弃）与**装饰器表达力升级**（"抑制形状"要精确判定
   需在受限输入上重跑 + 带修饰器的差分门）。

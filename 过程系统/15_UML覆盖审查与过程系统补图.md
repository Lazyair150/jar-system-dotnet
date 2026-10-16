# UML 覆盖审查：补齐过程系统 + 全图覆盖体检

> 生成时间：2026-10-16　生成者：Codex
> 触发：用户要求"在 UML 部分添加最新过程系统的部分，并看看当前的 UML 是否涉及全面的系统"。

---

## 1. 审查结论（先说结果）

**原来的 10 张图完全不覆盖过程系统** —— 它们停在 2026-09-16，而过程系统是此后才建起来的
（`Rules/` 与 `Process/` 两个新层、5 个新端口、两个执行轨、场景数据与编辑器都没进图）。
审查同时发现：**旧图里已有两处内容随实现前进而过期**（契约数字、以及"两处味道"的清单）。

本轮：**新增 4 张 + 更新 3 张 + 索引重写**，并在下方列出**仍未覆盖的部分与原因**。

## 2. 新增的四张（13 → 14 张图）

| 文件 | 类型 | 内容 |
|---|---|---|
| `09_State_过程生命周期.puml` | 状态图 | `Created → Ready → Running(Check→Apply→Commit) → Blocked → Resumed/Failed`、时限/取消/完成；守卫五条判据与"失败也留代价"写成 note |
| `10_Sequence_异步订单执行.puml` | 时序图 | **异步轨端到端**：`startOrder` → `BeginOrderExecution` → `BeginSession` → `AdvanceActive`（守卫/工作体/观察者）→ `CompleteOrderExecution`；含事件与修饰器到期两步、阻断分支、取消说明 |
| `11_Activity_过程推进管线.puml` | 活动图 | 一个 tick 的**固定顺序**：时钟 → 事件 → 修饰器到期 → 过程推进（时限 → 阻断复查 → 守卫 → 单单位）→ 世界产出；含"越权/等待/收尾"各出口 |
| `12_ClassDiagram_规则与过程.puml` | 类图 | `Rules`（`RuleContext`/`ModifierStack`/`WorldQuery`/`ScenarioBinder`…）与 `Process`（`ProcessDefinition`/`Instance`/`Run`/`System`/`Journal`/`Session`）的类型、端口与**实现者**（`OrderExecutor`/`OrderService`/`OrderProcessAdvancer`） |

## 3. 更新的三张

| 文件 | 更新点 |
|---|---|
| `07_Component_架构.puml` | 新增 `Rules` 与 `Process` 两个包（含各自子件）、`Domain --> RuleContext` 这条新边；**改写"两处味道"的 note**：保留 `Domain→Services` 与 `Abstractions↔Solver/Orders`（仍在），并注明 **Process 相关的两处成环已修**、`Rules` 已独立成层；新增 note 记录过程系统的依赖方向与"反向依赖自检四项全空" |
| `04_Component_模块边界.puml` | 加 `Rules`/`Process` 两个模块、`config/scenario.sample.json` 与"场景编辑器 3 端点"；契约数字更新（schema 10→**11 类型**、快照 223→**225 行**）、协议 18/10 → **21/16**；验证模块补两道门（**MODDIFF**、**SCENARIO**） |
| `01_索引.puml` | 10 → **14 张图**；补四行清单；把"编号重复"那条历史遗留补上"新图因此从 09_ 起编号，不再制造新的重复" |

## 4. 覆盖体检：现在哪些方面有图、哪些没有

| 关注点 | 覆盖 | 图 |
|---|---|---|
| 世界静态结构（Core 领域/求解/执行/命令/产出） | ✅ | 02（类图）、07（组件图） |
| 契约与验证体系 | ✅ | 04（模块边界）、12（端口与实现者） |
| 订单生命周期（既有语义） | ✅ | 05_State_订单生命周期 |
| **过程生命周期（新）** | ✅ | 09 |
| B 类订单执行链路（同步轨） | ✅ | 03 |
| **异步轨端到端（新）** | ✅ | 10 |
| **一个 tick 的推进顺序（新）** | ✅ | 11 |
| 可行性分析（责任链 + 划分 + 未判定） | ✅ | 08 |
| 切换事件（内部 / 端到端） | ✅ | 04_Sequence、05_Sequence |
| 罐子操作状态 | ✅ | 06 |
| **规则层（修饰器 / 查询 / 场景）静态结构（新）** | ✅ | 12 |
| 前端与消息契约（app.js 侧） | 🟡 | 只在 04 的 Browser 组件与 note 里提及；**没有前端组件图**（10 条消息的时序图也缺） |
| Godot 宿主内部（8 脚本 / 13 条 headless 自检） | 🟡 | 04 里有一条组件与 note；**无宿主内部图** |
| 场景端到端（编辑 → 保存 → 重启 → 装配） | 🟡 | 04 里标了 3 端点与数据文件；**无时序图** |
| 存档 / 读档 / 回放流程（SessionReplay） | 🟡 | 04 标了 Sessions 与回放门；**无时序图** |
| 世界装配（world.json → WorldOptions → 世界） | 🟡 | 04 有 `WorldCheck` 与第 6 步门；**无活动图** |

**判断**：**"系统内部（内核 + 契约 + 过程）"这一侧现在覆盖完整**；缺的都在**外围**（前端、Godot 宿主、
场景/存档的端到端流程、世界装配）。这与 `04_Component` 里已有的判断一致：外围是下一步的战场。

## 5. 机械自检与遗留

我按 `UML.txt` 自己记的踩坑清单做了机械检查（不渲染，因为本机没有 Java/PlantUML）：

| 检查 | 结果 |
|---|---|
| `@startuml` / `@enduml` 配平 | 14/14 张都是 1:1 ✅ |
| 禁用写法（反引号 / 方括号别名 / 数字开头成员 / 嵌套组件 / 数组式类型 / 多词 stereotype / 状态与序列图里的 `note top|bottom`） | 新增与改写的文件均无 ✅ |
| 渲染 | ✅ **已验证（2026-10-16，用户本机）**：VS Code 的 PlantUML 插件 + 本机 JVM 逐张预览，**14 张全部正常显示** |

**遗留两条（都需要你手动，因为自动化工具没有改名权限）**：

1. **编号重复**：`04_` 与 `05_` 各两张（历史遗留，索引里已注明）；新图从 `09_` 起编号以避开。
2. **`04_Component_模块边界.puml` 的 note 里仍有反引号**（第 144~147 行一带），而 `UML.txt` 写的是
   "反引号已全删"。它此前渲染通过（反引号只是原样显示），所以我**没有擅自改动既有图**——按你的口径决定是否清理。

## 6. 验证

UML 不在九步门的判定范围内，但 `UML/04_Component_模块边界.puml` **是文档口径守卫（`CheckDocs`）的 9 个载体之一**
（它必须含 `+ 32 deterministic`）—— 本轮改动后重跑门仍 `VERDICT=OK`，该载体完好 ✅。

| 项 | 结果 |
|---|---|
| 九步门 | `VERDICT=OK`（19.8 秒） |
| 主测试 / 文档口径 | `0 bugs / 2000 random + 0 bugs / 3000 capacity + 32 deterministic` / 载体完好 |
| 其余门 | `SCENARIO-OK` / `MODDIFF-OK (1600)` / `SOLVER-OK (4003)` / `PROTOCOL-OK (21/16)` / `REPLAY-OK` / `CONFIG-WORLD-MATCH` / `JS-OK` |

---

## 7. 追加（用户报告 09/11 语法错误 → 在线实测并修正）

本机没有 Java/PlantUML，但 PlantUML 官方服务可做**语法验证**（GET + 十六进制编码：
`https://www.plantuml.com/plantuml/txt/~h<hex>`；返回 200 且正文不含 `Syntax Error` 才算过）。据此拿到两个**真实报错**：

| 图 | 报错行 | 错误写法 | 修正 |
|---|---|---|---|
| `09_State_过程生命周期.puml` | 第 48 行 | 状态图里对**单个状态**写 `note over Succeeded` | 改为 `note right of Succeeded` |
| `11_Activity_过程推进管线.puml` | 第 75 行 | 活动图里写**悬浮** `note as NPipeline … end note` | 改为紧跟在最后一条活动语句后的 `note right` |

**修正后复验**（同一在线服务）：

```
09_State_过程生命周期.puml : http=200 无语法错误（返回 5416 字符）
11_Activity_过程推进管线.puml : http=200 无语法错误（返回 183602 字符）
```

**项目知识库同步**：这两条都已写进 `UML.txt` 的"PlantUML 踩坑（必读）"——
原规则 1 写的是"序列图 / 状态图：只有 note left/right of X、note over X"，
实测**状态图的单状态不接受 note over**，已改成"状态图只有 left/right"并附本次证据行号；
活动图那条补上"也**不能**写悬浮 note as"。同时新增一小节"在线验证语法"（含方法、判据与两个坑：
`~h` 的 URL 过长会 400、POST 需要跟随 302）。

**其余四张的验证由用户完成**：`04_Component_模块边界`、`07_Component_架构`、
`10_Sequence_异步订单执行`、`12_ClassDiagram_规则与过程` 在用户本机 VS Code（PlantUML 插件 + JVM）
逐张预览，**全部正常显示** ✅ —— 我的在线请求那几次返回 HTTP 400/500 属公共服务器限流/URL 过长，
**与图本身无关**（这条教训已写进 `UML.txt`：4xx/5xx 不等于语法错，只能算"未验证"）。

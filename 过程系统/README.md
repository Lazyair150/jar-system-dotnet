# 过程系统（Process System）工作区

> 本目录是「把过程提升为一等公民」这条工作线的落地目录。设计依据见仓库根目录的
> `设计分析_过程系统内核.md`（设计与实施规格），本目录只放**阶段产物**与**冻结的接口/契约**。

## 阶段与状态

| 阶段 | 内容 | 状态 | 产物 |
|---|---|---|---|
| **Phase 0** | 设计与契约冻结（术语、状态机、接口签名、`state.processes[]` 键集合、测试清单） | ✅ 完成（五项裁决已落定） | `00_Phase0_接口规格.md` |
| **Phase 1** | 过程骨架（**瞬发模式**，行为逐字节不变） | ✅ **完成并验证**（九步门 `VERDICT=OK`，数字与基线逐项一致） | `01_Phase1_骨架.md` |
| **Phase 2a** | 时间机制上线（逻辑时钟 / tick 管线 / 步间隔默认 **2 刻**按裁决 / 过程日志进回放门） | ✅ **完成并验证**（九步门 `VERDICT=OK`，新增 `journalBytes=722`） | `02_Phase2_时间化.md` |
| **Phase 2b** | 双轨执行：`StartOrder` = tick 驱动（新），`ExecuteOrder` 保持同步 drain（旧路径零变化）—— 裁决选 **(c)** | ✅ **完成并验证**（异步轨已进回放门） | `04_Phase2b_异步路径.md` |
| **Phase 2c** | 产率**定点化**（修浮点漂移）+ **有意重建契约基线** | ✅ **完成并验证**（基线已重建） | `03_Phase2c_定点化与基线重建.md` |
| **Phase 3a** | 守卫与阻断（`Blocked → Resumed/Failed` + S23/S24） | ✅ **完成并验证**（`32 deterministic`，9 载体已同步） | `05_Phase3a_守卫与阻断.md` |
| **Phase 3b** | 修饰器层（`Suppress/Retime/Grant` 已接，`Require/Intensity` 已声明 + `RuleContext`） | ✅ **完成并验证**（`32 deterministic`） | `06_Phase3b_修饰器层.md` |
| **Phase 3c** | 修饰器统一处理层（`ModifierAwareSolver` 装饰器）+ 事件注入 + `Require/Intensity` 消费者 + **投影** | ✅ **完成并验证**（`32 deterministic`，契约基线 223→225 行） | `07_Phase3c_装饰器与投影.md` |
| **Phase 4b** | 过程进入协议与表现层（5 条消息 + 2 条命令 + 前端徽标/取消/分步执行） | ✅ **完成并验证**（`PROTOCOL-OK 20/15`，`32 deterministic`） | `08_Phase4b_协议与表现.md` |
| **Phase 4c** | 自审与修正（热路径缓存、端口归位）+ **修饰器差分门**（`MODDIFF-OK 400/1200`） | ✅ **完成并验证** | `09_自审与修正.md` |
| **Phase 4d** | 边界重构（A1 `Rules` 独立成层 / A2、A3 破两处成环）+ 生产者强度消费者 | ✅ **完成并验证**（反向依赖自检全空） | `10_边界重构与阶段推进.md` |
| **Phase 4 收口** | 数据驱动修饰器/事件 + 表达力升级（受限池重跑）+ `flow` 强度消费者 | ✅ **完成并验证**（`MODDIFF 1600`，`32 deterministic`） | `11_阶段四收口与阶段五开场.md` |
| **Phase 5（开场）** | 宿主侧时间参数（`JarSystem:TickIntervalMs`） | 🟡 **第一片已落地**；倍速/暂停适配与场景编辑器待做 | 同上 §3/§5 |
| **Phase 5（收口三项）** | `Require` 数据化（`IWorldQuery`）+ 倍速（`setTickInterval`）+ 场景装配与校验工具 | ✅ **完成并验证**（`PROTOCOL-OK 21/16`，`SCENARIO-OK`） | `12_阶段五_三项收口.md` |
| **Phase 5（收口）** | 结构化查询（`IWorldFacts` + `WorldQuery`）+ 场景编辑器（3 端点 + 弹窗）+ 校验入门（第 7 步子检查） | ✅ **完成并验证**（含端到端冒烟：编辑→保存→重启→装配） | `13_阶段五_场景编辑器与结构化查询.md` |
| **全项目审查** | 设计目的 vs 实现差异：修 D1 `Window.From`、D2 `Priority`、D3 `DeadlineTick`（三处"声明未读取"）+ 规格回填实现现状 | ✅ **完成并验证**（`32 deterministic`） | `14_全项目审查_设计目的与实现差异.md` |
| **UML 补齐** | 新增 4 张（过程状态机 / 异步轨时序 / tick 管线活动图 / 规则与过程类图）+ 更新 3 张 + 覆盖体检 | ✅ **完成**（10 → 14 张；**14 张已全部渲染验证**，用户本机 VS Code + JVM） | `15_UML覆盖审查与过程系统补图.md` |
| **UML 语法在线验证** | 09/11 的语法错误定位并修正（并回填 `UML.txt` 的踩坑规则） | ✅ **09/11 在线修正 + 全 14 张经用户本机验证正常显示** | `15` §7 |
| Phase 5 | （可选）引擎时间对接、世界事件注入编辑器 | ⬜ | 同上 |

## 最新实测（2026-10-16，P2b + P2c 后）

```
0 bugs / 2000 random + 0 bugs / 3000 capacity + 32 deterministic
docs baseline: OK (9 carriers say 22)     CONFIG-WORLD-MATCH
PROTOCOL-OK (commands=21, messages=16)    JS-OK
SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)
REPLAY-OK (commands=33, stateBytes=3091, logBytes=8797, journalBytes=1415)
        ↑ 数字变化全部来自"脚本会话新增异步段"；异步轨的过程日志逐字节可复现
VERDICT=OK        （耗时 18 秒；契约快照 223 行、schema 10 类型、contract.d.ts 224 行均未变）
JOURNAL-OK (processes=4, entries=20, steps=3, phases=6)   ← 新产物（--dump-journal）
```

> ⚠️ **契约基线于 2026-10-16 有意重建一次**：P2c 定点化把 7 处 `accumulated` 的浮点漂移值
> （如 `0.8999999999999999`）修正为十进制原值（`0.9`）。这是**经用户批准的**可观察值变更，
> 论证与前后对照见 `03_Phase2c_定点化与基线重建.md`。

> ⚠️ 本目录里的数字**不在 `CheckDocs` 的载体清单内**（那份清单只覆盖"通篇都该是当前值"的 9 个文件）。
> 因此一旦将来新增确定性用例（例如 P3 的 S23–S26），**这里的 `32 deterministic` 需要人工同步**。

## 硬约束（任何阶段都不得违反）

1. **契约只加不改**：既有 JSON 键、命令名、消息名一字不动。
2. **依赖单向**：既有内核（`Domain`/`Config`/`Orders`/`Solver`/`Executor`/`Commands`/`Production`）**零** `Process` 引用。
3. **判定只认逻辑 tick**：过程不得读墙钟。
4. **每阶段必须有等价性论证 + 验证门 + 回滚方式**（沿用 `重构报告/板块报告/` 的格式）。

## 当前基线（2026-10-16 复跑，`重构报告\verify.bat`，`CI=1`）

```
=== 0 bugs / 2000 random + 0 bugs / 3000 capacity + 32 deterministic ===
docs baseline: OK (9 carriers say 22)
schema: 10 types / total loose=1     contract.d.ts: 224 行
state_keys.txt: 223 行               CONFIG-WORLD-MATCH
PROTOCOL-OK (commands=21, messages=16)
JS-OK
SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)
REPLAY-OK (commands=29, stateBytes=2369, logBytes=7748)
VERDICT=OK
```

上述数字是 Phase 1 的**等价性基准**：Phase 1 完成后必须逐项相同（新增产物另行增加）。

## 运行验证

```bat
set CI=1
重构报告\verify.bat
```

（约 20 秒；末行必须是 `VERDICT=OK`，退出码 0。）

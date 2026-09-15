# Godot 学习路线与里程碑（面向 jar-system-dotnet 的移植）

> 目标：不是"学会 Godot 游戏开发"，而是**把已验证的 JarSystem.Core 接进 Godot 表现层**，
> 每一步都有可运行、可对照验证的阶段性成果。
> 权威行为基准：`Godot迁移指南.txt`（第六节为当前行为）、`test/JarSystem.Test`（回归）、Web 版（app.js/canvas.js 的视觉行为）。

---

## 一、先厘清：你到底缺什么

你已经完成的部分 = **最难的部分**：

- 状态空间求解器（DFS + 快照隔离）、可行性精确搜索（2^N 划分）、按 jarId 执行的路径执行器、
  事件回滚、规则单一来源、2000+3000 随机测试与 19 个确定性回归。

这些是 90% 的"逻辑工程量"。Godot 侧剩下的是：

```
Godot 节点/Control UI ──调用──▶ JarDriver(Autoload 桥) ──调用──▶ JarSystem.Core(原样复用)
        ▲                                                              │
        └────────────── 订阅 StateChanged 信号，读 GetState() 重绘 ────┘
```

你需要新学的只是几个 **Godot 概念**（不是一门新语言）：

1. 场景 / 节点 / 场景树（Scene, Node, Tree）
2. 信号（Signal）—— 等价于你熟悉的回调/事件
3. Control 容器 UI（VBox/HBox/Grid/Panel/Label/Button/ScrollContainer）
4. Tween（动画，替代 CSS 动画）
5. Autoload 单例（替代你的 Web 胶水层 Program.cs）
6. （可选）Resource（数据资产，做 WorldProfile）

**结论：不需要系统性先学 Godot。做小里程碑，边做边查，是最快的路径。**

---

## 二、按需学习清单（对应上面每个概念，每个只读 1–2 页官方文档）

| 概念 | 官方文档（先看中文版，不够再看英文） |
|---|---|
| 节点/场景/场景树 | https://docs.godotengine.org/zh-cn/4.x/getting_started/step_by_step/nodes_and_scenes.html |
| 信号 | https://docs.godotengine.org/zh-cn/4.x/getting_started/step_by_step/signals.html |
| C# 版基础（与 GDScript 差异） | https://docs.godotengine.org/zh-cn/4.x/tutorials/scripting/c_sharp/c_sharp_basics.html 与 https://docs.godotengine.org/zh-cn/4.x/tutorials/scripting/c_sharp/c_sharp_differences.html |
| UI（Control） | https://docs.godotengine.org/en/stable/tutorials/ui/index.html |
| 编辑器总览/Step by step | https://docs.godotengine.org/en/stable/getting_started/step_by_step/index.html |

> 官方"你的第一个 2D 游戏"教程是 GDScript 写的，但只跟其中"场景/节点/信号/UI"的概念部分即可，不用逐行照抄。
> 中文入口：https://docs.godotengine.org/zh-cn/4.x/

## 三、里程碑计划（每个都是"阶段性成果"）

版本匹配建议：设计文档假设 Godot 4.3 + .NET 8；**下载前到 https://godotengine.org/download/ 确认当前稳定版及其要求的 .NET SDK**（4.7 起已转向 .NET 9）。下列步骤中的框架版本以实际安装为准。

### M0 — 环境与"核心进 Godot"（半天，零 UI）
- 安装 Godot .NET 版（选 **.NET/C# 版**，不是普通版）+ 匹配的 .NET SDK + VS Code（C# 扩展 + Godot Tools）。
- 新建 Godot 项目（建议放本仓库内 `godot/` 子目录或同级目录），csproj 添加
  `<ProjectReference Include="..\src\JarSystem.Core\JarSystem.Core.csproj" />`。
- 把 `JarSystem.Core.csproj` 的 `TargetFramework` 改为与引擎匹配（net8.0 或 net9.0）——Web 项目 net10 引用它不受影响。
- **验收**：任意一个 Node 脚本里 `new JarSystem()` 不报错。

### M1 — 无头冒烟（半天）
- 写一个脚本：`new JarSystem()` → `Tick(5)` → `GetState()` 逐层打印（工作台/仓库/订单/事件）。
- **验收**：打印结果与 Web 版同一时刻的状态一致；`GetState()` 的字典结构能被直接遍历。

### M2 — JarDriver 桥（半天）
- 实现 Autoload `JarDriver`：持有 System；`Start()/Stop()/Reset()`；2 秒 `_Process` 累计调 `Tick()`；
  定义 `[Signal] StateChanged`；Tick 后 `EmitSignal`。
- **验收**：挂两个监听节点都能收到信号（用 `Console.WriteLine` 即可）。对应《Godot迁移指南》7.1/7.2。

### M3 — 只读视图（1 天）
- `main.tscn`：Header（推进/自动检测/状态栏）+ 工作台 ScrollContainer + 订单 ScrollContainer + 日志面板。
- `WorkshopCard.tscn` / `OrderCard.tscn` 实例化绑定，每次 StateChanged 用 `GetState()` 全量重绘
  （暂不做差异更新）。罐子用 ColorRect + 颜色映射（迁移指南 2.5）。
- **验收**：工作台/订单/仓库/事件日志随 Tick 自动刷新；与浏览器 Web 版画面内容一致。

### M4 — 交互闭环（1–2 天）
- 按钮：选主/从、随机订单、checkFeasibility、executeOrder、手动 Tick、自动检测开关。
- **验收**：A/B 类订单的执行结果与 `test/JarSystem.Test` 的断言逻辑一致（同一内核，结果必然一致，这里是验证接线没接错）。

### M5 — 切换动画与转移表现（1–2 天）
- SetMaster 返回值 → 卡片高亮/缩放 + Tween；转移动画用 `CreateTween()` 移动小球（迁移指南 2.7）。
- **验收**：与现有 `app.js`/`canvas.js` 的视觉行为一一对应（switch 发光、转移粒子、事件日志）。

### M6 — 玩法化配置（可选，2–3 天）
- `WorldProfile`（Resource）+ `JarModifiers` + `JarBalanceService`（Godot移植设计对话记录 7.5）→ 局外预览。
- 存档/读档：核心范围外，用 Godot `FileAccess` + `System.Text.Json` 序列化 `GetState()`/重建状态。

每个里程碑跑完，跑一遍 `dotnet test`（或 test/JarSystem.Test）确认核心没被碰坏。

---

## 四、可对照学习的案例

### 官方与文档
- Godot 官方文档（C# 节）：https://docs.godotengine.org/zh-cn/4.x/tutorials/scripting/c_sharp/index.html
- 官方示例仓库 godot-demo-projects（**含 `mono/` C# 示例**）：
  https://github.com/godotengine/godot-demo-projects
- Godot 下载页（选 .NET 版、核对 SDK 要求）：https://godotengine.org/download/

### 入门教程（视频/文章）
- B 站【国语】Godot 4.3+ 与 C# 从零开始 2D 开发（搜 BV 号 `BV1U2GrzLEDX`）
- CSDN《Godot游戏开发——C#》系列：https://blog.csdn.net/qq_61988383/article/details/154182009
- 书（英文）：*Game Development with Godot 4 and C#*（O'Reilly）

### 开源可读项目（与"资源调度/经营 UI"同类型的 C# 项目，直接读代码）
- plentiful-colony — C# 殖民地资源收集/建造：https://github.com/aadame3311/plentiful-colony
- game-dev-studio — Godot C# 经营模拟 + Mod（中文作者）：https://github.com/xiaojianll123045-beep/game-dev-studio
- chickensoft-games/GodotGame — Godot C# 工程模板（含 DI/测试/CI，贴合你有测试基建的习惯）：
  https://github.com/chickensoft-games/GodotGame

### 本机最强参照（不用上网）
- `D:\codex\tank_game`（Godot 4.3 纯 GDScript）——表现层模式参考；
  对照表见《Godot移植设计对话记录.md》第 3 节：autoload 信号总线 / 组件场景 / FSM / Tween 动画 / HUD。

---

## 五、三个"现在就能做"的动作

1. **M0 先行**：装环境 + 引用核心 + `new JarSystem()`。这一步走通，你就已经有"阶段成果一"。
2. **对照实验**：拿 M3 的画面和 Web 版（3457 端口）并排，作为每个里程碑的验收参照。
3. **遇卡先搜概念再问**：Godot 专属问题（场景组织/信号连接/C# 绑定）直接带着最小复现问
   Godot 论坛（https://forum.godotengine.org/）或问回来——最小复现 + 已有内核测试 = 我们很容易定位。

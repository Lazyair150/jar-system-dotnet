# 罐子系统 × Godot 移植 — 会话设计与参考记录

> 本文档整理自 `D:\codex` 环境下的一次完整设计对话（含开头的坦克项目引子），作为**以后的设计参考与上下文衔接文件**。
>
> 相关项目：
> - 坦克游戏：`D:\codex\tank_game`（Godot 4.3，纯 GDScript）——Godot 表现层与工程模式参考
> - 罐子系统：`D:\codex\jar-system-dotnet`（.NET 10，最终版）——待移植进 Godot 的核心
>
> 权威行为文档（本文件不替代，只汇总会话结论与决策）：
> - `Godot迁移指南.txt`（2026-08-01，与最终代码一致，迁移时以此为准）
> - `规则.txt`（规则定案，含“满罐需求限制不放宽”）
> - `问题/系统设计与构造.md`（架构与设计）
>
> 注意事项：仓库内 `问题/移植报告.md` 是早期 SignalR 版的历史文档（端口 3456），与最终 WebSocket 版（端口 3457）不符，引用时勿混淆。

---

## 0. 会话结论速查（TL;DR）

1. **罐子核心可原样复用**：`JarSystem.Core` 是零外部依赖的纯 .NET 类库（无 Godot/UI/网络引用，已实测构建 0 警告 0 错误）。Godot .NET 项目直接 `ProjectReference` 引用即可；唯一工程改动是把 `TargetFramework` 从 `net10.0` 改为 `net8.0`（对齐 Godot 4.3 的 .NET 8）。
2. **坦克项目不能提供核心逻辑**（它是纯 GDScript，且没有搜索/命令/回滚类代码），但提供完整的表现层与工程模式参考：autoload 信号总线、组件化场景、FSM、LevelManager 流程、Tween/粒子、HUD、SoundManager。
3. **推荐架构**：核心类库（原样不动）+ `JarDriver`（Godot Autoload 桥）+ 视图层（Control 节点绑定 `GetState()`）。依赖方向单向：**视图→核心**；核心不认识 Godot。
4. **参数可变性设计**：`WorldProfile`（基础配置，Resource）+ `JarModifiers`（装备/事件修正，Resource）+ `JarBalanceService`（单一漏斗，`Rebuild()` 重算并推入核心公开 setter）。
5. **模块生命周期**：`Start() / Stop() / Reset()`；Stop = 冻结推进（核心无时钟，只有 `Tick()` 才前进，冻结驱动器即停止）。
6. **三个待定策略**（产品决策，非架构缺口）：容量溢出时卸装的处理、可行性缓存失效规则、修正叠加规则。

---

## 1. 起始：坦克项目（Godot）的可读性与框架

### 1.1 Codex 能读哪些 Godot 文件

Godot 的专属文本文件全部是明文，可直接读取并理解：

- `project.godot`（引擎配置）
- `*.tscn`（场景：节点树、属性、资源引用）
- `*.gd`（GDScript 脚本）
- `*.tres / *.res`（资源）
- `*.gdshader`（着色器）
- `export_presets.cfg`（导出设置）
- `*.import / *.uid`（导入元数据与 UID）

二进制资源（png/wav/ogg）不能当文本读，但图片可直接查看，音频配置可从 `.import` 读取。

**是否需要 SKILL / MCP**：都不需要。SKILL 是可选指令包（当前无 Godot 专属 skill，读纯文本也不需要）；MCP 是外部工具协议，只有“实时操控 Godot 编辑器”（打开场景、运行游戏、运行时查改节点）才需要 Godot MCP server。静态读文件/改代码不需要。

### 1.2 坦克项目关键设置（project.godot）

- Godot 4.3，Forward Plus 渲染器，1280×800 视口，`canvas_items` 拉伸，自定义鼠标指针
- 主场景：`res://Scenes/UI/menu.tscn`
- Autoload：`Gamemanager`（全局信号总线 + 全局状态）、`SoundManager`（音乐/UI 音效）
- 输入：WASD 移动 + 鼠标左键射击
- 8 个 2D 物理层（PlayerHitBox / PlayerHurtBox / EnemyHitBox / EnemyHurtBox / Barrier / PlayerBody / EnemyBody / PickUp）+ 2 个导航层（wander / patrol）
- 导出：Windows Desktop x86_64，`embed_pck=true`，S3TC/BPTC 纹理

### 1.3 坦克项目架构要点

**关卡场景 `game.tscn` 结构**：

```
Game (Node2D)
├─ Points                 # 出生点/路径区域标记
├─ MapPCG                 # 程序化地图（TileMapLayer + AStarGrid2D + PathGenerator）
├─ HUDLayer / HUD         # 击杀数、血量条（信号绑定）
├─ Player                 # 玩家坦克（CharacterBody2D + 组件）
├─ VFXManager             # 爆炸动画、粒子、音效
├─ PickupManager          # 敌人死亡掉道具（50% 概率）
├─ EnemyManager           # 敌人波次生成与管理
├─ LevelManager           # 关卡流程：开场暂停 → 继续 → 胜负面板
├─ CanvasModulate         # 按关卡切换整体色调
└─ WorldEnvironment       # 2D 辉光
```

**组件化实体**：玩家/敌人由 `Scenes/Component/` 小组件拼装——WeaponComponent（瞄准/冷却/开火动画）、HealthComponent（血量/受伤/死亡信号）、HurtBoxComponent（受击盒，带 armor）、HitBoxComponent（伤害盒）、TrailCompoment（履带拖尾）、ShadowComponent、SearchComponent（RayCast2D 索敌，视野夹角 <90°）、DetectComponent（Area2D 探测）、AvoidComponent（ShapeCast2D 避障）。

**战斗信号链**（解耦核心）：

```
子弹 HitBox(damage) → HurtBox.get_hurt()（扣 armor）→ get_damage 信号
→ HealthComponent → died → Gamemanager.entity_died(pos, groups)
→ 监听者：EnemyManager（波次/计分）、PickupManager（掉宝）、VFXManager（爆炸）、实体自身
```

**敌人 AI（FSM）**：`State` 基类 + `StateMachine`（导出初始状态、transitioned 信号）；坦克为 Patrol → Attack → Wander，按导航层切换；炮塔为 StaticBody2D 探测射击；导弹车双管发射；沙袋纯静态障碍。

**程序化地图**：`map_pcg.gd` 用 5 个 TileMapLayer（草地/道路/边界/物品/树木），`PathGenerator` 生成随机路径点 + AStarGrid2D（禁对角）连通敌我出生点；安全区、随机石头/树、`get_patrol_points() / get_tower_points() / is_on_road()` 接口。

**已知问题（勿带到新项目）**：
- `enemy_patrol_astar.gd` 是死代码（调用了不存在的 `enemy.move()` / `enemy.map.get_road_path()`；实际用的是 `enemy_patrol_nav.gd`）
- `map_pcg.gd` 的 `setup_safe_area()` 有 `player_start_surround.append(player_start_surround)` 自引用 bug
- `Scenes/game/` 残留 `gamDE52.tmp`、`gam58F4.tmp` 等编辑器临时文件

---

## 2. 罐子系统（jar-system-dotnet）全面理解

### 2.1 技术栈与项目结构

- .NET 10（`net10.0`），解决方案 `JarSystem.slnx` 含 4 个项目：
  - `src/JarSystem.Core`：核心类库，**零 NuGet 依赖**（纯 C# + LINQ）
  - `src/JarSystem.Web`：ASP.NET Core 极简 API + 原生 WebSocket + 静态文件（端口 3457）
  - `test/JarSystem.Test`：2000 随机 + 3000 容量压力 + 12 个确定性回归
  - `test/StrategyExperiment`：711 结构模式 × 多种子 × 4 策略，验证 FIFO 选择策略最优
- 编码注意：`.cs` 为 UTF-8 无 BOM；Windows PowerShell 5.1 下 `Get-Content` 需加 `-Encoding UTF8` 才不会乱码。

### 2.2 分层架构

```
JarSystem (Facade，唯一入口)
├─ Domain     Jar / Workshop（形状标记、禁令、容量、快照）
├─ Orders     Order / OrderEvent / FeasibilityChecker / WorkshopSwitchEvent
├─ Solver     StateSpaceSolver（DFS 回溯 + 快照隔离）
├─ Executor   IndependentExecutor（按 jarId 定位 + 状态预检 + 路径执行/贪心降级）
├─ Commands   Command / CommandExecutor / PourIn、ReshapeEmpty、PourOut、Transfer
├─ Production ProducerModule（生产者速率、中央仓库、单向流动、溢出丢弃计数）
└─ Config     Constants（8 形状/2 液体/容量常量）、ShapeRules（转换规则唯一来源）
```

### 2.3 核心机制（最终版行为，以 `Godot迁移指南.txt` 第六节为准）

- **规则**：8 种形状（圆/方/三角/高/长/扁/鼓/尖罐）、2 种液体（酒/蜜）；工作台分基础标记/附加标记/禁令；转换规则：基础标记→所有其他基础+附加、附加→所有基础、不在任何标记→所有基础。
- **操作**：PourIn（空罐+基础标记+不违禁）、PourOut（满罐→空罐+液体回台）、ReshapeEmpty（空罐改形，消耗 1 份液体）、ReshapeFull（满罐改形，自耗罐内液体）。每次操作后 `isMarked=true`（一罐一操作）。
- **禁令边界**：只作用于“倒入空罐、游离液体存储、改形成本”；装满被禁液体的满罐可正常持有/转移/回滚。
- **满罐需求限制（设计定案，不放宽）**：满罐需求只能由现成满罐或“现成同形空罐+倒入”满足；改形只服务空罐需求。
- **可行性分析**：A 类单阶段；B 类 = `slave_only` 快速路径 + **需求子集划分精确搜索（2^N，N≤10）**，产出 `MasterPath` + `SolverPath` + `TransferJarIds` + `TransferLiquids`。
- **执行（选择性转移）**：主台按 MasterPath 执行 → 只转移“交付罐 + 从台计划使用的工具罐”（按 Id 清单）与所需液体 → 从台按 SolverPath 执行，失败降级贪心 → 交付；未交付的转移资源回流主台；转移失败即终止。
- **回滚**：OrderEvent 对主从台做快照，失败按 Id 回滚；溢出资源直接丢弃（仓库单向流动，不回收）。
- **工作台切换事件**：禁令相同 → Case 1（游离液体灌进基础形状空罐）；禁令不同 → Case 2（旧台临时获得新台基础标记为附加，改形/倒液/满罐改形）；转移按“新主台价值”排序，容量满丢弃；快照回滚保证原子性。
- **全局唯一罐子 Id**：`Jar.NextId()` 静态计数器；跨台转移/回滚按 Id 判定归属；`Reset()` 不清零（会话内无害）。
- **仓库**：罐 200、每种液体 200 上限；溢出丢弃计数；`FlowToMaster` 每 Tick 默认流入主台 3 罐 2 液，基础形状优先。

### 2.4 Web 层（最终版）

- `Program.cs`：托管静态文件 + `/api/state` + `/ws` WebSocket；JSON 命令（createOrder / setMaster / executeOrder / checkFeasibility / manualTick / startTick / setAutoCheck / reset 等）；`SemaphoreSlim` 单用户串行闸门；2 秒 Tick 定时器 + 5 秒自动检测定时器。
- `app.js`：手写 SPA，`state` 推送 → `updateUI()` 重绘工作台卡片/订单卡片/仓库/生产者；CSS 动画模拟工作台切换（缩放、发光、粒子点）。
- `canvas.js`：Canvas 绘制环形生产网络 + 粒子转移动画。
- 这些 DOM/CSS/WebSocket 内容在 Godot 移植时**整体废弃**，换成 Control 节点 + 信号 + Tween。

### 2.5 测试与“最终版”确认

- `test/JarSystem.Test/Program.cs`：双向一致性（可行必须执行成功、不可行执行成功也算 bug）+ 罐子守恒断言 + 液体不为负断言 + 容量压力测试 + 确定性回归。
- `test/StrategyExperiment/Program.cs`：抽象结构模式枚举 × 3 种子 × 4 策略（FIFO/LIFO/RareShapeFirst/Random），FIFO 显著最优。
- 最新文件（2026-08-01 13:40：`改动文档.txt`、`app.js`；13:30：`Godot迁移指南.txt`、核心源码）与最终代码一致，即“项目已完成”的版本。

---

## 3. 坦克项目对罐子 Godot 移植的参考价值

**总原则**：核心直接复用（C# 类库原样引用），表现层重写；坦克项目提供表现层与工程模式参考，不提供逻辑参考。

| 罐子项目 Godot 移植需求 | 坦克项目可借鉴的现成实现 |
|---|---|
| 全局单例持有系统实例、信号驱动刷新 | `Gamemanager` autoload：全局信号总线 + 全局状态，UI 全部 connect 信号 |
| 工作台/订单卡片可复用 UI 组件 | `Component/` 组件场景：一个 .tscn + 导出引用 + 对外接口，实例化拼装 |
| 订单生命周期状态切换 | `Enemy/FSM/` 的 `State` 基类 + `state_machine.gd`：导出初始状态、信号切换 |
| 开场暂停 → 展示信息 → 继续的流程控制 | `LevelManager`：`paused` 暂停、入场面板、继续按钮、胜负面板 |
| 转移动画、发光、粒子 | Tween/AnimationPlayer（闪光 shader、开火动画）、CPUParticles2D、VFXManager |
| 2D 网格/图形化表现（可选玩法化） | TileMapLayer 程序化地图、CanvasModulate 调色 |
| 全局音效 | SoundManager autoload |
| 场景切换（菜单→游戏→下一关） | menu.gd → Gamemanager.to_game() → 重载场景 |

**不可借鉴**：核心逻辑（求解器/可行性/命令/回滚）在坦克中没有对应物；Web 层（WebSocket/DOM/CSS/Canvas）全部废弃。

**约束**：坦克是纯 GDScript，罐子核心是 C#——代码不能复制，但概念一一对应（autoload/信号/场景/FSM/Tween）；坦克自身有小 bug（见 1.3），别把问题带过去。

---

## 4. 核心复用确认（三问三答）

### 4.1 能原样复用吗？——能

实证证据：
- `JarSystem.Core.csproj` 无任何 `PackageReference`
- 源码搜索 `Godot/Unity/System.Web/Http/WebSocket/Console/File/Thread` 零命中（仅 obj/ 编译器生成的 GlobalUsings，与业务无关）
- 现场 `dotnet build`：0 警告 0 错误
- `GetState()` 返回纯 `Dictionary<string, object?>`，不含 UI 类型；核心可无头运行

边界与注意点：
- **TargetFramework**：`net10.0` → 需改为 `net8.0`（对齐 Godot 4.3 的 .NET 8；代码用的 C# 12 语法 .NET 8 完全支持，一行工程配置，零代码改动）
- **非线程安全**：所有调用放 Godot 主线程，或照抄 Web 层的 SemaphoreSlim 加锁
- **无存档/读档**：设计上明确排除，Godot 端需自补序列化层
- **静态 `Jar.NextId()`**：进程级计数器，`Reset()` 不清零；单会话无害
- **随机不可复现**：核心内部 `new Random()`，如需确定性需自己加种子入口

### 4.2 职责分明吗？——分明

- Domain（Jar/Workshop）：只描述实体与原子操作，不含业务规则/订单/UI 概念
- Orders：订单生命周期、可行性、切换事件
- Solver：只读快照推演，从不修改真实状态
- Executor：经 Command 对真实状态做变更，命令可撤销，事件层统一记账
- Production：仓库与生产者、单向流动、溢出丢弃
- Config：`ShapeRules` 为转换规则唯一来源
- Facade（JarSystem）：唯一对外入口；Web 层是薄适配器

### 4.3 封闭自洽吗？——自洽

- 可行性分析全部作用在 `CreateSnapshot()` 副本上；OrderEvent 对主从台快照回滚
- 规则唯一来源（ShapeRules）避免多份实现漂移
- 双向一致性测试 + 守恒断言（罐子守恒、液体不为负）+ 12 确定性回归
- 自洽性的边界：`规则.txt` 明确“满罐需求限制”等为设计定案，改动需同步求解器/执行器并回归全部测试

---

## 5. 核心对外接口

核心不暴露 .NET 事件，而是**返回值 + GetState() 快照 + EventHistory 日志**三件套；Godot 侧由 JarDriver 包装成信号。

### 5.1 Facade 公开方法

| 类别 | 方法 | 签名/返回 |
|---|---|---|
| 构造/重置 | `new JarSystem()` | 自动 `InitializeDefault()`（4 生产者 + 4 默认工作台） |
| | `Reset()` | 清空全部状态并重建默认 |
| 时间/资源 | `Tick(int count = 1)` | 生产 + 向主台流入（默认 3 罐 2 液） |
| | `UpdateProducerRate(name, shape, rate)` | 调整生产者概率 |
| | `UpdateLiquidProductionRate(liquid, rate)` | 调整酒/蜜产量 |
| | `UpdateWorkshopCapacity(index, maxJars?, maxLiquids?)` | 改容量，溢出丢弃并计数 |
| | `SetAutoCheck(bool)` | 开/关自动可行性检测 |
| 主从选择 | `SetMaster(index)` | 返回 `{success, switched, caseType, operations, slaveCleared?}` |
| | `SetSlave(index)` | 返回 `{success, reason?}`；主从同台被拒 |
| 订单 | `CreateOrder(requirements, type)` | 返回 `(Success, Error?)`；需求上限 10、空罐在前 |
| | `CreateRandomOrder() / CreateRandomWorkshop()` | 随机生成 |
| | `DeleteOrder(index)` | 返回 `{success}` |
| 分析 | `CheckFeasibility(index)` | 返回 `FeasibilityResultData?`（含路径、转移清单） |
| | `CheckAllFeasibility()` | 所有待执行订单的分析结果 |
| 执行 | `ExecuteOrder(index)` | 五元组 `(Success, Reason, Event报告, TransferOccurred, 操作日志)` |
| 快照 | `GetState()` | 见 5.2 |
| 日志 | `EventHistory`（属性） | 最近事件完整列表；`GetState()` 只带最后 10 条 |

### 5.2 GetState() 固定结构（UI 绑定的唯一数据源）

```
workshops[]      → index/name/baseMarks/additionalMarks/ban/
                   maxJars/maxLiquids/jars[{id,shape,liquid,isMarked,description}]/
                   liquids/canPourShapes/isMaster/isSlave
orders[]         → index/id/type/requirements/status/feasibilityResult/description
warehouse        → jars 数量 / liquids / discardedJars / discardedLiquids
producerStatus   → producers 速率表 + generators 累积进度
globalState      → autoCheckEnabled / currentMaster / currentSlave / masterName / slaveName
eventHistory     → 最近 10 条事件
```

### 5.3 关键数据契约

- `FeasibilityResultData`：feasible / blockage / reason / timestamp / directCount / operationCount / solverPath / masterPath / transferJarIds / transferLiquids / checkMethod（`single_phase` / `slave_only` / `partition`）
- 路径步骤字段：`step / requirement / type / jarIndex / jarId / jarShape / jarLiquid / targetShape / targetLiquid / costLiquid / reshapeMode("full"/"empty") / consumed / jarsAfter`——执行器按 jarId 定位、执行前校验状态
- `ExecutionLogEntry`：phase / action / description / jar / liquid / amount / reason

---

## 6. Godot 的 C# 原理与 Node 认知

### 6.1 为什么 Godot .NET 能直接复用 JarSystem.Core

Godot 的 .NET 版（旧称 Mono 版）= 引擎内置“标准 .NET 运行时 ↔ 引擎”的桥。它**不捆绑 .NET SDK**，需自行安装（Godot 4.3 对应 .NET 8）。任何纯标准 .NET 库（如 JarSystem.Core）都能被 Godot 项目 `ProjectReference` 引用并原样运行——就像引用 NuGet 库。

### 6.2 “支持 C#”做了什么（三层机制）

1. **GodotSharp 绑定**：把引擎 C++ API 翻译成 C# 类（Node、Sprite2D……内部持原生句柄，跨边界互操作）
2. **源生成器**：`[Export]`、`[Signal]`、`[Tool]` 等特性在编译期生成注册胶水，把 C# 成员注册进引擎 ClassDB——Inspector 才能显示、场景才能实例化
3. **引擎桥接模块**：把 `_Ready`、`_Process`、信号等回调从 C++ 侧路由到托管类

场景里“脚本从 .gd 换成 .cs”只是表面；`.tscn` 中 `[ext_resource type="Script" path="res://X.cs"]` 指向类文件。

### 6.3 GDScript vs C# 对照

| GDScript | C# 等价 | 作用 |
|---|---|---|
| `@export var speed = 100` | `[Export] public int Speed = 100;` | 暴露到属性栏 |
| `@onready var hp = $Health` | `_Ready()` 里 `GetNode<HealthComp>("Health")` | 延迟获取子节点 |
| `signal died(x)` | `[Signal] public delegate void DiedEventHandler(int x);` + `EmitSignal(SignalName.Died, x)` | 定义/发射信号 |
| `@tool` | `[Tool]` | 编辑器中运行脚本 |
| `class_name Player` | 类名即类型 | 全局类型注册 |

### 6.4 Node 是什么

- Node 是场景树的基本单位，`Node` 是基类，引擎按功能派生上百个子类（Node2D/Sprite2D、Control/Button/GridContainer、Timer/Tween、AudioStreamPlayer、CharacterBody2D/Area2D……），每个封装一类引擎能力
- “脚本 = 类”不只挂节点一种用法：纯类（C# 任意类）、Resource 子类（数据资产，可在 Inspector 编辑）、Autoload（单例节点）
- **Node 不是唯一代码桥，但是“进入游戏世界”（可见、可交互、参与 _Process）的唯一桥**；纯逻辑可完全住在场景树外

### 6.5 依赖方向与数据流

```
玩家点击 Button（node）
   │ ① 依赖/交互方向：node → 罐子CS（唯一依赖方向）
   ▼
JarDriver（node）→ System.ExecuteOrder() → 核心改状态、返回结果
   │ ② 驱动翻译成 Godot 信号
   ▼
其他 node 收到信号 → 读 GetState() 刷新（③ 数据流：核心 → node）
```

结论：**依赖上只能“node 认识核心，核心不认识 node”；数据流上核心产生状态、视图消费状态**。这是核心能原封不动进 Godot 的根本原因。

---

## 7. 罐子系统 Godot 模块化设计（本会话产出）

### 7.1 总体架构（四层）

```
主系统（时间循环）
   │ 每帧/每 tick 调用
   ▼
JarDriver (Godot Autoload 节点)   ← 唯一桥：持有 JarSystem，转发调用、翻译成 Godot 信号
   │
   ▼
JarSystem.Core（纯类库，零 Godot 依赖，原样不动）
   │
   ▼
视图层（WorkshopCard / JarView 等 Control 节点）← 订阅信号，读 GetState() 渲染
```

驱动器骨架（C#）：

```csharp
public partial class JarDriver : Node
{
    public JarSystem.Core.JarSystem System { get; private set; }
    [Signal] public delegate void StateChangedEventHandler();

    public override void _Ready() { System = new JarSystem(); }

    public override void _Process(double delta)
    {
        _timer += delta;
        if (_timer >= 2.0) { _timer = 0; System.Tick(); EmitSignal(SignalName.StateChanged); }
    }
}
```

### 7.2 生命周期 Start / Stop / Reset

核心无时钟，只有 `Tick()` 才前进 ⇒ **Stop = 冻结推进循环（状态保留），恢复即继续**。

| 方式 | 适用场景 |
|---|---|
| 驱动器节点 `SetProcess(false)` / `SetPhysicsProcess(false)` | 只停罐子模块，游戏其他系统照常（推荐） |
| `get_tree().paused = true` + 节点 ProcessMode | 全局暂停 |
| Timer 节点驱动 Tick，`Stop()/Start()` | 固定节拍（每 2 秒一 tick） |

```
Stopped ──Start()──▶ Running ──Stop()──▶ Stopped
   ▲                    │
   └──────Reset()───────┘
```

`Start()` = 开始推进；`Stop()` = 冻结（状态保留）；`Reset()` = `core.Reset()` + 重新应用当前 WorldProfile（状态清空）。三者幂等。

### 7.3 可配置参数盘点（核心现有旋钮）

| 可配置项 | 核心入口 | 性质 |
|---|---|---|
| 罐子生产速率（生产者×形状） | `RegisterProducer()` / `UpdateRate()` | 每 Tick 累积生成 |
| 液体生产速率（酒/蜜） | `UpdateLiquidProductionRate()` / `LiquidProductionRate` | 每 Tick 产量 |
| 每 Tick 流入主台量 | `FlowSettings = (JarCount, LiquidAmount)`（公开可写） | 仓库→主台 |
| 工作台模板 | `CreateWorkshop()` / `Rules.WORKSHOP_TEMPLATES` | 启动建台 |
| 工作台容量 | `UpdateWorkshopCapacity()` | 实时生效，溢出丢弃 |
| 工作台当前资源 | `Workshop.Jars` / `LiquidStorage` | 公开属性 |
| 订单需求 | `CreateOrder()` / `CreateRandomOrder()` | 形状/液体/数量/A/B |
| 仓库上限 | `Constants.WAREHOUSE_MAX_*` | 目前编译期常量 |
| 主从选择、自动检测 | `SetMaster/SetSlave/SetAutoCheck` | 全局开关 |

### 7.4 参数 vs 规则（可配置 vs 设计定案）

- **可配置**：上表全部
- **设计定案（不可配置，动了破坏自洽）**：形状转换规则（ShapeRules）、禁令语义、满罐需求限制、订单“空罐在前”校验、求解器/执行器行为

### 7.5 WorldProfile / JarModifiers / JarBalanceService

**WorldProfile（基础配置，Godot Resource，可在 Inspector 编辑）**：

```csharp
public partial class WorldProfile : Resource
{
    [Export] public int FlowJars = 3;
    [Export] public int FlowLiquids = 2;
    [Export] public int WineRate = 1;
    [Export] public int HoneyRate = 1;
    [Export] public int MaxJars = 20;
    [Export] public int MaxLiquids = 30;
}
```

**JarModifiers（装备/道具修正，Resource）**：

```csharp
public partial class JarModifiers : Resource
{
    [Export] public int WineRateDelta;
    [Export] public float HoneyRateMul = 1f;
    [Export] public int FlowJarDelta;
    [Export] public int MaxJarsDelta;
    [Export] public string Shape;
    [Export] public float ShapeRateMul = 1f;
}
```

**JarBalanceService（单一漏斗，全部参数变化只走这里）**：

```csharp
public partial class JarBalanceService : Node
{
    JarSystem _core;
    WorldProfile _base;
    List<JarModifiers> _active = new();

    public void ApplyBase(WorldProfile p) { _base = p; Rebuild(); }
    public void Equip(JarModifiers m)   { _active.Add(m);    Rebuild(); }
    public void Unequip(JarModifiers m) { _active.Remove(m); Rebuild(); }

    void Rebuild()
    {
        _core.UpdateLiquidProductionRate("酒", _base.WineRate + _active.Sum(m => m.WineRateDelta));
        _core.UpdateLiquidProductionRate("蜜", (int)(_base.HoneyRate * _active.Aggregate(1f, (a,m)=>a*m.HoneyRateMul)));
        _core.FlowSettings = (_base.FlowJars + _active.Sum(m => m.FlowJarDelta), _base.FlowLiquids);
        // 每种生产者×形状：base × 形状倍率累乘
        foreach (var (producer, shape) in EnumerateRates(_base))
            _core.UpdateProducerRate(producer, shape, _base.Rate(shape) * ProductOfShapeMods(shape));
        // 容量修正：UpdateWorkshopCapacity（注意溢出丢弃语义）
    }
}
```

原则：核心不知道“装备/事件”是什么，它只看到公开 setter 收到的数值变化。

### 7.6 局外预览（两种深度）

- **参数预览（必做）**：展示当前装备组合下生效的速率/流量/容量——即 `Rebuild()` 算出的数值，不跑模拟
- **运行预览（可选）**：用**第二个独立 `JarSystem` 实例**后台跑 N 个 Tick/订单，展示仓库走势
  - 注意：静态 `Jar.NextId()` 会被预览实例消耗（无碍）；核心内部 `new Random()` 不可复现，需确定性则要加种子入口
  - **预览实例与局内实例严格分离**

### 7.7 运行时事件漏斗

统一修正来源概念，任何事件（换装、buff、天气……）都是同一机制的不同数据：

```csharp
class ModifierSource
{
    string Id;
    JarModifiers Mods;
    double? ExpiresAt;   // 临时 buff 用；装备常驻则 null
}
```

`Rebuild()` 恒等于：`基础 WorldProfile + 所有活跃来源的修正` → 推入核心。到期由驱动器移除并重算。

### 7.8 三个待定策略（需用户拍板，非架构缺口）

1. **容量溢出时卸装**：卸下“罐位+N”装备时若工作台已满，核心会直接丢弃溢出罐子（单向流动语义）。需决定：阻止并提示 / 允许并警告。
2. **可行性缓存失效**：速率/流量只影响未来，不影响现有可行性；容量/初始资源类修改会使缓存过期。建议 `Rebuild()` 时清掉或标记所有 PENDING 订单的 `FeasibilityResult`。
3. **修正叠加规则**：加法还是乘法、同类效果叠加还是取最大（如两件“蜜产量 ×2”），提前定死写进 `Rebuild()`。

### 7.9 JarDriver 对外契约建议

```
JarDriver（唯一对外门面）
├─ 生命周期：Start() / Stop() / Reset()
├─ 配置：ApplyProfile(WorldProfile) / GetEffectiveStats()   ← 预览用
├─ 修正：AddModifierSource(id, mods, duration?) / RemoveModifierSource(id)
├─ 操作：ExecuteOrder(index) / CreateOrder(...) / SetMaster(...) / ...
└─ 信号：StateChanged / LifecycleChanged / ModifiersChanged
```

角色、装备、事件、UI 都只跟 JarDriver 说话。

---

## 8. 方案确认结论与实施步骤

**确认结论**：本会话产出的模块化设计与适配方案在架构层面**完整且无误**——所有用到的核心公开 API 均真实存在（已逐条核对）；Stop=冻结推进与核心无时钟特性一致；预览、事件漏斗、Resource 翻译层的注意点均符合代码事实。三个待定策略属于产品决策，不影响架构正确性。

**实施步骤清单（后续开发路线）**：

1. 创建 Godot 4.3 .NET 项目，安装 .NET 8 SDK
2. 将 `JarSystem.Core` 加入解决方案引用；csproj 目标框架改为 `net8.0`；先跑通 `new JarSystem()` + 无头测试
3. 实现 `JarDriver`（Autoload）：持有 System、Start/Stop/Reset、Tick 循环、信号
4. 搭视图层：`main.tscn` + `WorkshopCard.tscn` + `OrderCard.tscn` + 日志面板（参考坦克项目的 HUD/LevelManager 模式）
5. 实现 `WorldProfile` / `JarModifiers` / `JarBalanceService`，接局外装备预览
6. 定三个策略决定（7.8），实现事件漏斗
7. 按需补存档/读档（核心范围外）

---

## 附：本会话要点速查（一句话版）

- 罐子核心 = 纯 .NET 零依赖类库，Godot .NET 直接引用，唯一改动是目标框架
- 坦克项目 = Godot 表现层模式参考（信号总线/组件场景/FSM/LevelManager/Tween），不提供核心逻辑
- 依赖方向：视图(node) → 驱动器 → 核心；核心不认识 Godot
- 一切参数可变性收进 JarBalanceService 单一漏斗；规则（ShapeRules/禁令/满罐限制）不可配置
- 模块生命周期 Start/Stop/Reset；Stop = 冻结 Tick 推进
- 三个待定策略：卸装溢出、可行性缓存失效、修正叠加规则

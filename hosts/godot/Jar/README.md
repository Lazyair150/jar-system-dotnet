# JarSystem Godot 宿主（第一切片）

把 `JarSystem.Core`（零依赖类库）当作游戏内核，用 **Godot** 当宿主跑起来。
核心与 Web 宿主完全共用同一份代码 —— 这里没有任何"移植后的第二份实现"。

## 用什么打开

**必须用 .NET/mono 版的 Godot 4.7**（C# 脚本在标准版里是死文件）：

```
D:\Godot\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64.exe
```

（项目创建时的渲染器是 Mobile/D3D12，`config/features` 里已补上 `"C#"`。）

## 目录结构

```
Jar/
├── project.godot          # 主场景 = res://Main.tscn；Autoload = GameManager
├── jar.csproj             # Godot.NET.Sdk/4.7.1，net10.0（本机只有 .NET 10）
│                          # 程序集名必须是 jar：与 project.godot 的 assembly_name 一致
├── Main.tscn              # 主场景：Control(Main.cs) + Label(Status)
└── scripts/
    ├── GameManager.cs     # Autoload：持有 JarSystem、每 2 秒 Tick、发 StateChanged 信号
    └── Main.cs            # 节点附加脚本：把状态信号绑到 Label 上
```

`jar.csproj` 通过 **ProjectReference 直接引用 `..\..\..\src\JarSystem.Core\JarSystem.Core.csproj`**
⇒ 改核心源码后宿主立刻生效，不需要手动拷 DLL。

## 构建与运行

```powershell
# 构建（联网时直接 dotnet build 即可；离线时用本地 NuGet 源，见下）
cd hosts\godot\Jar
dotnet build jar.csproj

# 无界面跑一遍（固定 60fps × 900 帧 ≈ 15 秒游戏时间；末行应看到执行成功的那条）
& 'D:\Godot\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64_console.exe' `
    --headless --path . --fixed-fps 60 --quit-after 900
```

**离线构建**（本机 NuGet 缓存里已有 `godot.net.sdk/godotsharp/godot.sourcegenerators/godotsharpeditor` 4.7.1）：

```powershell
dotnet restore jar.csproj --source <放着这几个 .nupkg 的目录>
dotnet build jar.csproj --no-restore
```

> 注意：本仓库**没有**给宿主放 `NuGet.config`（避免把包源写死）。离线源只在命令行上用 `--source` 指定。

## 已验证到哪一步（2026-09）

在 `--headless` 下实测通过（输出见 `重构报告/00_从这里继续.md` 对应条目）：

- 引擎加载核心库并装配世界：**工作台 4 个 / 生产者 4 个 / 主台 玄武台 / 从台 朱雀台**
- `Tick()` 真实推进：主台罐数 0→2→4→7→…，仓库液体 20→21→…（与 Web 宿主同一入口）
- 完整用例链：随机订单 → 可行性分析 → 执行
  - `single_phase` 不可行 + 准确的阻塞文案（含"液体酒被禁止"这条禁令边界）
  - **B 类 `partition` 判定可行 → 执行成功、发生转移、交付完成**
- 场景绑定：`Main.tscn` 加载、`Main.cs` 的 `_Ready()` 执行、Label 绑定 `StateChanged` 信号

## UI 切片（2026-09，已实现）

界面结构（`Main.tscn` = 骨架，动态卡片由代码建）：

```
Main (Control, Main.cs)
└── Root (VBox)
    ├── Header (HBox)：推进 / 自动推进:开 / 自动检测:关 ｜ 随机订单 / 随机工作台 ｜ 新建订单 / 编辑器 ｜ 存档 / 读档 / 重置 ＋ 状态栏
    └── Body (HBox)
        ├── WorkshopsColumn：Scroll → WorkshopsList（WorkshopCardView × N，含 设为主/设为从）
        ├── OrdersColumn：Scroll → OrdersList（OrderCardView × N，含 分析/执行/路径/删除）
        └── SummaryColumn：概览 + 事件日志（RichTextLabel）

另有一个代码构建的 **可行性路径弹窗**（`PathOverlay.cs`，等价于 Web 端 `showPathModal`）：
显示判定结论、选择性转移清单（罐/液体）、以及主台/从台阶段的逐步操作与结果罐子。

以及**新建订单表单**（`OrderFormOverlay.cs`，等价于 Web 端 `showCreateOrder`）：
类型 A/B + 逐行选"形状 × 液体(空罐/酒/蜜)"，可增删行。
两个关键设计：
1. **下拉项来自核心词表**（`System.Vocabulary.Shapes/Liquids`）—— Godot 侧不硬编码清单，
   换 `config/world.json` 的形状表这里自动跟着变；
2. **需求顺序不由表单校验**："空罐必须在前"是玩法规则，住在核心（`Order.ValidateOrder`），
   表单只把拒绝原因展示出来 ⇒ 宿主 UI 与核心规则不会各说各话。

以及**世界编辑器**（`WorldEditorOverlay.cs`）—— 按"**同类操作归一类**"把三类编辑收在一个单元里：
新建工作台（名字 + 基础/附加标记 + 禁令）、改工作台容量、改生产者速率。
宿主 API 侧同样收口：三个操作都走 `GameManager.TryEdit(...)` 这一条受控通道
（调核心 → 成功记日志发信号 / 失败把**核心原文**回给界面），
避免"每个宿主各自 try/catch，迟早漏一处"——Web 端当初正是这么漏的。
```

数据流：`GameManager.StateChanged` / `LogAppended` 两个信号 → `Main.Refresh()` 重建面板。
**Godot 侧不解析 JSON、不复制任何规则**，一律取 `GetState()` 的强类型 DTO
（与 Web 宿主 `app.js` 的 `render*` 等价）。

### 存档 / 读档（Event Sourcing 切片在宿主上的兑现）

存档**不是世界快照**，而是三样东西：**种子 + 世界配置 + 命令日志**（`JarSystem.CommandLog`）。
读档 = 用这三样构建一个全新 `JarSystem`，再用 `SessionReplay.Into(...)` 重放命令。
好处：存档极小、记的是"意图"而非"结果"（对格式演进更友好），将来做回放/观战/多端同步是同一套东西。

落地位置：`user://jar-save.json`（桌面版落在用户数据目录，不污染工程目录）。

两个**必须满足的前置条件**（都在核心里落实了，宿主照做即可）：

1. **ID 必须可复现** ⇒ `GameManager` 用 `SequentialIdGenerator` 注入 `jarIds`/`orderIds`；
2. **时间必须可复现** ⇒ 事件历史的时间戳取自"本次意图的时间"（`JarSystem.Now`，由命令日志携带）。

> ⚠️ **唯一的例外**：`feasibilityResult.timestamp` 装的是**分析耗时毫秒**（墙面时间测得值），
> 回放无法复现 —— 与契约快照的 `<volatile>` 同一口径处理。其余字段（含 `eventHistory[].timestamp`）
> 都要求逐字节一致。

### 无界面自检（我不看像素也能验的部分）

```powershell
& 'D:\Godot\...\Godot_v4.7.1-stable_mono_win64_console.exe' --headless --path . --fixed-fps 60 --quit-after 900
```

期望看到（实测输出）：

```
[UI] Main.tscn 已加载并完成绑定
[UI] 自检：工作台卡片=4（首张标题="0. 玄武台 【主】"） 订单卡片=3 日志行数=13
[UI] 自检：点击"推进"按钮 ⇒ TickCount 4 → 5 ✅ 按钮接线正常
[UI] 自检：状态栏文本 = "主台 玄武台 → 从台 朱雀台 ｜ 已推进 5 次"
[UI] 自检：路径弹窗订单 #3 渲染 11 行，前 3 行：
        需求: 空罐(鼓罐)
        判定: 可行=True 未判定=False 方法=partition 直接 1 / 操作 0
        选择性转移: 罐 #4 ；液体 无（其余资源留在主台）
[UI] 自检：建单表单（合法：空罐(圆罐)）⇒ 订单 3 → 4，消息="已创建 A 类订单（1 个需求）"
[UI] 自检：建单表单（违规：满罐在前）⇒ 订单 4 → 4（应不变），
        消息="创建失败: 订单需求列表约定: 空罐需求在前、满罐需求在后（执行顺序为满罐优先）"
[UI] 自检：建台（未选基础标记）⇒ 工作台 4 → 4（应不变），消息="被核心拒绝: 工作台至少需要 1 个基础标记"
[UI] 自检：建台（基础标记=圆罐）⇒ 工作台 4 → 5，消息="成功"
[UI] 自检：改容量（罐位=0）⇒ 消息="被核心拒绝: 罐位上限必须是正整数"
[UI] 自检：改容量（罐位=25/液=30）⇒ 消息="成功"
[UI] 自检：改速率（1e9）⇒ 消息="被核心拒绝: 速率必须是 0 到 100 之间的有限数，收到: 1000000000"
[UI] 自检：改速率（0.5）⇒ 消息="成功"
[Host] 已存档到 user://jar-save.json（命令 18 条）
[Host] 读档成功：重放 18 条命令（种子 20260916）
[UI] 自检：存档/读档 ⇒ saved=True loaded=True 回到存档点=True（弄乱后确实不同=True）
```

> 这六行是**跨宿主规则一致性**的可见证据：这些拒绝文案全部由核心给出，
> Web 宿主对同样的输入会显示**逐字相同**的字符串。

### 尚未做的（下一批）

功能面已经齐了（Web 端有的入口这里都有）；剩下的是**美术替换**：
当前罐子用 `Label` 占位（`空圆` / `酒圆` 这种，颜色按液体区分），可换成
`TextureRect` / 自绘 `Control`；工作台卡片与订单卡片都在 `*CardView.cs` 里，节点结构改起来很直接。

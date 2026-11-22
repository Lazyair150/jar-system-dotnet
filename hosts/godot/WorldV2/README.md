# World v2 Godot Host

这是全新的 Godot 4.7.1 .NET 宿主工程，专门连接当前 World v2 系统。

它与旧的 `hosts/godot/Jar` 并存，**不继承、不复制、不调用旧 Jar 模块**。

## 工程关系

```text
Godot Main scene / UI
        |
        v
WorldV2HostNode
  - Godot delta -> world-time milliseconds
  - pause / speed controls
  - master/slave workshop-individual replacement commands and cancellation
  - snapshot-changed notification
        |
        v
WorldV2BridgeSession
        |
        v
WorldV2ApplicationService
        |
        v
WorldV2HostService
        |
        v
WorldV2Context / World v2 Core
```

## 时间边界

Godot 的 `_Process(delta)` 提供墙钟秒数。HostNode 只把秒转换为世界时间毫秒，并保留不足 1 毫秒的余数。

它**不乘倍速、不自行暂停逻辑、不实现世界规则**。暂停与流速倍率由 Core 的 `WorldClock` 处理。

## UI 边界

UI 只订阅无参数的 `SnapshotChanged` 通知，然后调用 HostNode 暴露的 DTO 快照刷新显示。工作台替换通过 HostNode -> Bridge -> ApplicationService -> Core 命令链路执行；候选工作台来自快照中的备用个体列表。从台候选只显示无资源的备用个体。UI 不会把 DTO 或 Core 对象塞进 Godot Signal，也不会直接访问 Host、Context 或领域实体。

## 打开与运行

1. 安装 Godot 4.7.1 .NET 版本及 .NET 10 SDK。
2. 在 Godot Project Manager 中导入本目录的 `project.godot`。
3. 等待 C# 工程恢复完成。
4. 运行主场景。

也可以从仓库根目录构建：

```powershell
dotnet build hosts/godot/WorldV2/JarWorldV2.csproj -c Release
```

## 当前范围

当前宿主链路覆盖：Godot 启动 -> 创建 World v2 -> 按墙钟增量推进 -> Core 应用暂停/倍速 -> DTO 快照 -> 主/从工作台个体替换与取消 -> 检查点 JSON 保存/恢复 -> 节点退出释放会话。

订单编辑、工作台动画和正式游戏玩法仍属于后续阶段。替换规则本身由 Core 决定；Godot 只提供候选选择、命令发送和结果展示。

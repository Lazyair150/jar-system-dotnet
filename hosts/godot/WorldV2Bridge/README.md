# World v2 Godot Bridge

这是新 Godot 宿主使用的 World v2 桥接层。它与旧的 `hosts/godot/Jar` 工程并存，不修改、不继承旧 Jar 模块。

## 当前阶段的定位

`JarSystem.WorldV2Bridge` 是一个普通 .NET 10 类库，不引用 Godot，也不复制 World v2 业务规则。

- `WorldV2BridgeSession` 是组合根：创建并持有一个 `WorldV2HostService`，同时创建对应的 `WorldV2ApplicationService`。
- `WorldV2ApplicationService` 是命令和查询入口。
- `WorldV2HostService` 负责世界生命周期与独占操作边界。
- `JarSystem.Core` 继续拥有生产、资源、订单、求解、执行、冷却、工作台替换和结算规则。

Godot 项目后续只需引用本项目，并由一个 Godot Node 持有 `WorldV2BridgeSession`。Godot 场景、输入和 UI 不应直接访问 Host、Context 或 Core 领域实体。

## 生命周期

1. Godot 宿主创建 `WorldV2BridgeSession`。
2. 宿主加载/构造 `WorldV2Options`，调用 `CreateOrReplaceWorld`。
3. Godot 的时间驱动调用 `AdvanceWorld(deltaWorldTimeMilli)`。
4. UI 在命令完成或需要刷新时调用 `GetWorldSnapshot()`。
5. Godot Node 退出时调用 `Dispose()`，释放会话拥有的 Host 和 World Context。

会话不创建后台线程、不读取墙钟、不自动推进时间。时间间隔由宿主提供，流速和逻辑时间换算由 Core 的 WorldClock 负责。

## 边界约束

- 不向调用方暴露 `WorldV2Context`、`Workshop`、`Jar`、`OrderDemand` 等可变 Core 对象。
- 只返回 ApplicationService 的脱离式 DTO 或简单结果值。
- 不把 DTO 直接作为 Godot Signal 参数；Godot Signal 只通知状态变化，UI 再主动读取快照。
- 不在 Godot 层重新实现任何 World v2 规则。
- 不通过旧 `JarSystem`、旧 `Tick()` 或旧 `GameManager` 驱动 World v2。

## 后续阶段

- 4.9b：为该会话增加纯 .NET 端到端测试，验证初始化、推进、命令、快照和释放。
- 4.9c：新建独立 Godot .NET 工程，引用本项目并完成最小场景运行链路。

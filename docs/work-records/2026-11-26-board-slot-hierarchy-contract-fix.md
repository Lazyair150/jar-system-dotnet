# 2026-11-26 Board Slot 层级区域契约修复

## 目标

修复 Space 的真实战斗启动配置在 Core World V2 Host 入口被拒绝的问题，同时保留 Core 对冻结 Board、分配身份和指纹的严格校验。

## 起始状态

- Core 仓库：`Lazyair150/jar-system-dotnet`
- 分支：`architecture/core-liquid-identity-consolidation`
- 起始 commit：`391f318a24fde724a66054e771d0590f0e20c231`
- Space 实测错误：`Board Definition slot has an unsupported area/component/role/control-area combination: decomposition.parser.primary`
- Space Board 定义将解析器槽位表达为 `areaId=production` + `subAreaId=decomposition`。

## 根因

Core 的 `WorldV2BoardSlotSnapshotOptions` 只保留了 `areaId`，没有接收 `subAreaId`。其校验却把 `ore_parser` 当成顶层 `areaId=decomposition`，与 Space 的 Board 层级模型冲突。

这不仅造成槽位形状校验失败；Core 的规范化 assignment fingerprint 也没有覆盖子区域身份，因此无法与 Space 冻结的 Board 内容保持同一契约。

## 实施

- 为 Core Board Slot DTO 增加 `subAreaId`，缺省值为 `none`，兼容不含子区域的旧 JSON。
- 将解析器合法位置明确为 `production/decomposition/ore_parser`。
- workshop、skill、production_device 位置显式要求 `subAreaId=none`。
- 将 `subAreaId` 纳入 Core 的规范化 Board Assignment fingerprint。
- 新增正向测试：生产区下的 decomposition 子区域允许安装 ore_parser。
- 新增反向测试：缺少 decomposition 子区域的 parser 槽位必须被拒绝。

## 验证状态

- 本轮通过 GitHub 源码检查确认 Space 发送的 Board Definition 同时包含 `areaId` 与 `subAreaId`。
- 本记录对应实现 commit：`778bac8b1ead77d7940ecb138d4432ec13a03879`。
- 对应测试 commit：`9244723e3117d9f96def22c5fb61e296b37aeb65`。
- 尚未在本地执行 `dotnet test` 或 Godot 跨仓库验证；不得将源码提交视为运行通过。
- Space 必须将 Core checkout 更新到包含该 DTO 修复的 commit，再重新构建 Bridge 并运行真实战斗启动验证。

## 后续

1. 在 Core 本地运行新增测试及完整既有测试集（只因 Core 源码发生了新改动，这次回归有必要）。
2. 更新 Space 集成使用的精确 Core SHA。
3. 验证真实 Hangar 装配、战斗启动、暂停和返回菜单。
4. 单独修复主菜单场景切换后 HUD 残留/输入不可用问题；不能用 headless 状态探针替代 GUI 验收。

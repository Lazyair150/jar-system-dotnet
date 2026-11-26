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

## 续记：2026-11-26 回归结果与测试夹具修复

### 原始失败证据

Core commit `9aed7c07eb1940893a28707c3fe0398cda568729` 的本地 xUnit 结果为 1040 项中 1037 通过、3 失败。对应 PR merge CI Run `45844458666` 也定位到三项 Board Slot 层级相关测试失败。

### 三项失败根因

1. `WorldV2BoardRuleProfileOptionsTests.ValidateAcceptsParserAndWorkbenchAssignmentsWhenSlotHierarchyMatches`：正向测试的 assignment entries 未按稳定 `SlotId` 顺序排列。
2. `WorldV2BoardRuleProfileOptionsTests.ValidateRejectsParserAssignmentWhenSlotAreaDoesNotMatchParserContract`：测试仍断言旧错误文案 `unsupported area/sub-area/component`，而实现已报告 `unsupported area/component/role/control-area combination`。
3. `WorldV2FactoryTests.CreateWorldV2IncludesParserInProductionDecompositionSlot`：测试夹具把 parser 写成顶层 `AreaId=decomposition`，正式层级应为 `AreaId=production`、`SubAreaId=decomposition`。

这三项是测试夹具/断言漂移，不构成放宽 Core 运行时契约的理由。

### 已实施修正

- 正向测试在计算 fingerprint 前按稳定 `SlotId` 排序 assignment entries。
- 负向测试更新为当前正式校验错误文案。
- Factory 测试夹具改用 `production/decomposition` 层级。
- 没有修改运行时合法槽位范围或校验器行为。

### 修正提交

- 测试排序修正：`3f33c74157a2e429ac4c22ff36725a7b39be30b3`。
- 测试断言与 Factory fixture 修正：`47c2c7f4e6c9f78b448e8fe601940c39be580835`。
- 本记录此前状态更新：`fce78edd54c85ddcba8fd5aae4da0b8897659e85`。

### 最终验证闭环：2026-11-26

用户在本地对 Core 分支执行：

```powershell
dotnet run --project .\test\JarSystem.Tests\JarSystem.Tests.csproj -c Release
```

实际输出：

```
Finished: JarSystem.Tests
=== TEST EXECUTION SUMMARY ===
JarSystem.Tests  Total: 1040, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 7.879s
```

因此，当前 Core HEAD `fce78edd54c85ddcba8fd5aae4da0b8897659e85` 的用户本地 xUnit 结果为 **1040/1040 通过**。

同一精确 HEAD 的 GitHub PR 验证：
- Run：`45846180974`
- Workflow：`verify`
- 结论：`success`
- Job `verify` 成功；构建、测试基线、契约快照、schema、配置世界等价验证步骤均成功。
- 失败日志上传步骤因无失败而跳过。

### 验证边界与后续

- Core 1040 项全量测试已由用户本地结果验证；同一精确 HEAD 的 PR CI 也已成功。
- Core PR #63 仍为 Draft / Open / Unmerged；CI 通过不代表已合并。
- Space PR #2 当前 GitHub HEAD 为 `f6475ba5fad9ab77f480571fda09a6117325f4ed`。
- Space 最近一次用户本地跨仓库通过日志使用的 Core SHA 是 `9aed7c07eb1940893a28707c3fe0398cda568729`，并非本次验证的最新 Core SHA。
- 因跨仓库脚本强制要求 `CORE_EXPECTED_SHA` 与 Core checkout HEAD 完全相同，下一次集成验证必须将 Core checkout 更新至 `fce78edd54c85ddcba8fd5aae4da0b8897659e85`，并以该 SHA 设置 `CORE_EXPECTED_SHA`。这是对精确依赖版本的确认，不需要重跑 Core xUnit。
- Space 上次完整跨仓库运行已通过的探针不应被无故重复；仅因 Core pin 变化而执行一次精确版本的 Bridge/Space 构建与集成脚本。
- 桌面 GUI 验收仍需独立完成：默认 Hangar 装配、进入战斗、暂停/恢复、返回菜单、再次进入战斗。Headless 探针不能替代 GUI 验收。

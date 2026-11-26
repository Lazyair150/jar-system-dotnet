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

### 新的实际验证证据

用户在本地执行了 Core xUnit 与 Space 跨仓库验证。实际结果必须拆开记录：

- Core xUnit：`Total: 1040, Errors: 0, Failed: 3`，因此 Core 全量测试尚未通过。
- Space HUD：`21 checks, 0 failures`。
- Space Hangar / Board / progression domain contracts：`all-domain-contracts=passed`。
- Parser ingress：`parsed=1`、重复回执保留、逐形状容量结果精确、共享时钟生效、没有绕过工作台。
- World V2 runtime、生命周期、故障边界、释放后命令拒绝、快照隔离探针通过。
- 暂停控制器探针：`passed terminal-menu-transition=true`。
- 跨仓库脚本最终标记：`CROSS-REPO-WORLD-V2-OK core=9aed7c07eb1940893a28707c3fe0398cda568729`。
- 本次上传日志没有出现此前三个 detached Node 的 `Leaked instance: Node:` 或 `Cannot get path of node as it is not in a scene tree` 记录；不能据此宣称所有 Godot Resource/ObjectDB 引用均已无泄漏。

### Core xUnit 三项失败的根因

精确 CI Run：`45844458666`，验证对象是 Core commit `9aed7c07eb1940893a28707c3fe0398cda568729` 对应的 PR merge build。Release Build、Godot hosts、StrategyExperiment、contract snapshot/schema、config-world equivalence、protocol guard、solver differential（4004/4004）、modifier differential（400 场景 / 1600 项）、session replay（33 条命令）和安全扫描（6 个项目、0 个漏洞）均通过；失败集中在 xUnit 的三个 Board Slot 相关用例：

1. `WorldV2BoardRuleProfileOptionsTests.ValidateAcceptsParserInDecompositionSubAreaOfProduction`：assignment entries 未按稳定 `slotId` 排序，违反现有快照契约。
2. `WorldV2BoardRuleProfileOptionsTests.ValidateRejectsParserPlacedDirectlyInProductionWithoutDecompositionSubArea`：断言仍使用旧错误文案，没有跟随当前校验器的错误文本。
3. `WorldV2FactoryTests.FactoryAppliesBoardContributionsToExactMasterAndSlaveRuntimeSlots`：测试夹具把 parser 写成顶层 `AreaId=decomposition`，而正式层级是 `AreaId=production`、`SubAreaId=decomposition`。

这三项是测试夹具/断言漂移，不构成放宽 Core 运行时契约的理由。

### 已实施的修正

- `WorldV2BoardRuleProfileOptionsTests`：正向用例在计算 fingerprint 前按稳定 `SlotId` 排序 assignment entries。
- 同一测试文件：拒绝用例改为断言当前正式校验错误 `unsupported area/component/role/control-area combination`。
- `WorldV2FactoryTests`：parser slot 改为 `AreaId=production` + `SubAreaId=decomposition`。
- 没有修改 `IsValidSlotShape`、assignment 顺序要求或运行时接受范围；Core 仍只接受正式定义的 Board slot 层级。

### 提交与验证边界

- 测试修正提交一：`3f33c74157a2e429ac4c22ff36725a7b39be30b3`。
- 测试修正提交二：`47c2c7f4e6c9f78b448e8fe601940c39be580835`。
- 当前 Core 分支应以第二个提交为最新已知 HEAD；尚未取得这两个提交对应的成功 CI 或本地 xUnit 回归结果。
- 这次跨仓库成功标记使用 Core `9aed7c0`，并不包含上述测试修正提交；它证明 Space 集成链路通过，不证明 Core xUnit 通过。
- Space 仓库的本地分支 HEAD 未出现在这份日志中，因此本记录不推断其 SHA。

### 下一步

1. 拉取 Core 分支最新提交 `47c2c7f4e6c9f78b448e8fe601940c39be580835`。
2. 运行 Core xUnit，确认上述三个用例修正后全量通过；若失败，按新日志处理，不复用旧失败结论。
3. 重新运行 Core PR 对应验证，确认精确 HEAD 的 Actions 状态。
4. Core 验证闭环后，再更新 Space 使用的 Core pin；不把当前成功的旧 pin 当作最新验证基线。
5. GUI 验收仍需独立进行：默认 Hangar 配置、进入战斗、暂停、返回主菜单、再次进入战斗。
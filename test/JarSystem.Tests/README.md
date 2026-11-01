# test/JarSystem.Tests —— 聚焦回归测试工程

## 现在的位置

它**已经在** `JarSystem.slnx` 里，并由 `重构报告/verify.bat` 第 2 步执行。
九步门因此同时覆盖旧 runner 的 32 条确定性用例和本工程的 22 条 xUnit 用例；
其中 2 条直接锁定过程内核的收尾一致性与 `Reset()` 运行时清理。

核心工程仍保持零外部包；NuGet 测试包只属于测试工程。

## 用法

```bat
cd /d D:\codex\jar-system-dotnet

:: 1) 装测试框架（版本交给 NuGet 选最新，避免手写版本号猜错）
dotnet add test\JarSystem.Tests package xunit.v3

:: 2) 运行（xUnit v3 的测试项目本身就是可执行程序；本项目使用 dotnet run，不依赖 VSTest）
dotnet run --project test\JarSystem.Tests
```

**已知前提（两条，都踩过）**：
1. csproj 必须设 `<OutputType>Exe</OutputType>`，否则报
   `xUnit.net v3 test projects must be executable`；
2. **csproj 的 XML 注释里不能出现连续两个减号**（例如命令行里的 `--project`），否则 MSBuild 报 `MSB4025`；
   因此说明一律放在本文件（markdown），不放 csproj 注释。

可选（性质测试，P3 第二步）：

```bat
dotnet add test\JarSystem.Tests package FsCheck.Xunit.v3
```

**包名坑（踩过，务必按上面写）**：

| 想装 | 正确包名 | 错误写法（会炸） |
|---|---|---|
| xUnit v3 本体 | `xunit.v3` | — |
| FsCheck 的 v3 集成 | `FsCheck.Xunit.v3` | `FsCheck.Xunit`（v2 集成） |

### ⚠️ 为什么**不用 Verify**（快照/审批测试）

Verify 33.x 起是**赞助门槛软件**：`SponsorCheck` 会让构建直接失败（`SC021`），除非声明赞助、豁免，
或显式设置 `<Verify_SponsorshipLicenseIgnored>true</Verify_SponsorshipLicenseIgnored>`
—— 最后那条的原文是"你接受这次构建**违反包许可证**"，本项目不采用。

因此审批测试**自实现**（约 20 行，零依赖、行为等价）：
- 基线：`Approvals/DefaultWorldOptions.approved.json`（**提交进仓库**）
- 不一致时写出 `DefaultWorldOptions.received.json` 并让测试失败
- 人工比对后把 received 覆盖为 approved —— 这就是"审批"这一步
- `*.received.json` 已加入 `.gitignore`（那是过程产物）

（若你将来愿意赞助 Verify，把 `Verify.XunitV3` 加回来并按其要求声明赞助/豁免即可。）

## 与现有门的关系

* 旧 runner（`test/JarSystem.Test`，2000 随机 + 3000 容量 + 32 确定性）继续作为
  **基线守护**留在 `verify.bat` 第 2 步，其输出字符串不变；
  对应输出仍是 `0 bugs / 2000 random + 0 bugs / 3000 capacity + 32 deterministic`；
* `quality-gate.ps1` 要求 xUnit 总数不少于 22，且 Errors/Failed/Skipped/Not Run 全为 0；
* Release 构建现在覆盖本工程，编译错误会在第 1 步直接失败。

## 已包含的测试（共 22 条）

| 测试 | 锁定的不变量 |
|---|---|
| `DefaultWorld_HasFourWorkshops_FourProducers_AndInitialStock` | 内置世界：4 工作台 / 4 生产者 / 各液体 20 / 流入 3,2 |
| `Vocabulary_Default_MatchesBuiltInConstants` | 词表是形状/液体目录的唯一权威，默认等于内置常量，非法形状被拒 |
| `WorldAssembledFromDefaultOptions_EqualsBuiltInWorld` | **配置装配 == 内置世界**（`GetState()` 序列化逐字节相等） |
| `FeasibilityAndExecution_AreConsistentInBothDirections` | 判定可行⇒执行成功、判定不可行⇒执行失败 |
| `CompletionFailure_DrivesTheProcessOutcome` | 最终交付失败时过程不得误报成功 |
| `Reset_ClearsProcessRuntimeState` | 世界重置会清空活动/最近过程、过程日志与逻辑时钟 |

后续按文件尾部注释里的两个模式补：FsCheck 性质测试（失败会自动收缩出最小反例 + 可重放种子）、
审批快照测试（`GetState()` 的 JSON 审批基线，与现有 **225 行**文本快照互补）。

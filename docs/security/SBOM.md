# SBOM（软件物料清单）

> 生成方式：`dotnet list JarSystem.slnx package --include-transitive --format json` 的实测输出，
> 人工整理成下表；漏洞结论来自 `dotnet list ... --vulnerable --include-transitive`（NuGet 官方数据源）。
> 复核命令见文末。**依赖一旦变化，本表必须同步**（`CONSTRAINTS.md` I4 的口径：公共表面变了就要重基线）。

## 1. 结论先说

| 事实 | 数值 |
|---|---|
| **产品代码的第三方依赖** | **0 个**（`JarSystem.Core`、`JarSystem.Web` 都没有 PackageReference） |
| 随仓库发布的可执行文件来源 | 只来自本仓库源码 + .NET SDK（`global.json` 固定 SDK 版本） |
| 第三方依赖的唯一使用者 | `test/JarSystem.Tests`（xUnit v3 + FsCheck），**不进入产品产物** |
| 已知高危漏洞 | **0**（`SEC-OK (projects=5, vulnerable=0)`） |
| 传递依赖总数 | 18（全部来自测试工程） |

这条结论对本项目的安全价值很高：**运行时的攻击面里没有第三方包**，
供应链风险被压缩到"构建时用到的 SDK 与测试框架"。

## 2. 逐项目清单

### `src/JarSystem.Core/JarSystem.Core.csproj`
- 直接依赖：无
- 传递依赖：无

### `src/JarSystem.Web/JarSystem.Web.csproj`
- 直接依赖：无
- 传递依赖：无
  （只用到 ASP.NET Core 共享框架里的 `Microsoft.AspNetCore.*`，它由 SDK 提供、不按 NuGet 包解析）

### `test/JarSystem.Test/JarSystem.Test.csproj`
- 直接依赖：无
- 传递依赖：无

### `test/StrategyExperiment/StrategyExperiment.csproj`
- 直接依赖：无
- 传递依赖：无

### `test/JarSystem.Tests/JarSystem.Tests.csproj`（仅测试，不入产品）
- 直接依赖（2）
  | 包 | 版本 |
  |---|---|
  | `xunit.v3` | 4.0.1 |
  | `FsCheck.Xunit.v3` | 3.4.0 |
- 传递依赖（18）
  | 包 | 版本 |
  |---|---|
  | `FSharp.Core` | 5.0.2 |
  | `FsCheck` | 3.4.0 |
  | `Microsoft.ApplicationInsights` | 2.23.0 |
  | `Microsoft.Bcl.AsyncInterfaces` | 6.0.0 |
  | `Microsoft.Testing.Extensions.Telemetry` | 2.4.0 |
  | `Microsoft.Testing.Extensions.TrxReport.Abstractions` | 2.4.0 |
  | `Microsoft.Testing.Platform` | 2.4.0 |
  | `Microsoft.Testing.Platform.MSBuild` | 2.4.0 |
  | `Microsoft.Win32.Registry` | 5.0.0 |
  | `System.Security.AccessControl` | 6.0.1 |
  | `xunit.analyzers` | 2.1.0 |
  | `xunit.v3.assert` | 4.0.1 |
  | `xunit.v3.common` | 4.0.1 |
  | `xunit.v3.core.mtp-v2` | 4.0.1 |
  | `xunit.v3.extensibility.core` | 4.0.1 |
  | `xunit.v3.mtp-v2` | 4.0.1 |
  | `xunit.v3.runner.common` | 4.0.1 |
  | `xunit.v3.runner.inproc.console` | 4.0.1 |

## 3. 漏洞结论（按包）

`dotnet list JarSystem.slnx package --vulnerable --include-transitive` 对全部 5 个项目返回
"没有易受攻击的包"，即上表所有包的已解析版本都不在 NuGet 漏洞库的高危/严重列表中。

注意口径：`--vulnerable` **只列出有漏洞的包**，所以"列表为空"等于"没有命中"，
而不是"没有依赖"—— 依赖数量请以上文 §2 为准。

## 4. 供应链控制（与 SBOM 配套）

| 控制 | 在哪 | 失败时 |
|---|---|---|
| 版本锁定 | 各 `.csproj` 写具体版本；SDK 版本由 `global.json` 固定 | 构建即失败或产出漂移 |
| 漏洞扫描阻断 | `重构报告/security-gate.ps1` 第一部分 | `SEC-SCAN-FAILED` → `VERDICT=FAIL` |
| 秘密扫描 | 同脚本第二部分（8 条保守模式） | `SEC-SECRETS-FAILED` → `VERDICT=FAIL` |
| 契约与回放基线 | `verify.bat` 第 6/9 步 + 快照基线 | `SNAPSHOT-MISMATCH` / `REPLAY-MISMATCH` → `VERDICT=FAIL` |
| 构建可复现 | `verify.bat` 每次 `--no-incremental` 全量重建 | 步骤失败即 `STEP-FAILED` |

**缺口（如实记录）**：产物未做 Sigstore / cosign 签名，也没有 SLSA provenance attestation ——
理由与整改条件写在 `docs/security/信任边界与代码安全架构.md` §11。

## 5. 复核命令

```bat
rem 完整清单（含传递依赖）
dotnet list JarSystem.slnx package --include-transitive

rem 只看产品项目
dotnet list src\JarSystem.Core\JarSystem.Core.csproj package --include-transitive
dotnet list src\JarSystem.Web\JarSystem.Web.csproj package --include-transitive

rem 漏洞
dotnet list JarSystem.slnx package --vulnerable --include-transitive

rem 一键门禁（依赖 + 秘密 + 全部其它门）
powershell -NoProfile -ExecutionPolicy Bypass -File 重构报告\security-gate.ps1
```

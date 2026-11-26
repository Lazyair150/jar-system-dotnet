# 2026-11-26 外部 Jar 导入回执测试契约修正

## 目标

修正外部生产 Jar 导入精确回执测试的编译错误，保持公开资源账本读模型与内部持久化检查点之间的边界。

## 起始状态

- 仓库：`Lazyair150/jar-system-dotnet`
- 分支：`architecture/core-liquid-identity-consolidation`
- 起始 HEAD：`76d8fc0c480d35c4994a53c25097b8783f0b293c`
- 触发方式：本地执行 `dotnet run --project .\test\JarSystem.Tests\JarSystem.Tests.csproj -c Release` 时报告 CS1061 / CS1503。

## 根因

`WorldV2ApplicationService.GetResourceLedger()` 返回 `WorldV2ResourceLedgerDto`。该 DTO 是供调用方读取有限事件投影的公开读模型，不包含操作去重用的持久化回执。

`ResourceLedgerProducedJarImportReceipt` 是账本内部的持久化状态，随 `ResourceLedgerSnapshot` 进入执行检查点。测试错误地把公开 DTO 当作 `ResourceLedgerSnapshot` 使用，既访问不存在的 `ProducedJarImportReceipts`，又把 DTO 传给要求快照的 `ResourceLedger.Restore`，因此出现：

- CS1061：`WorldV2ResourceLedgerDto` 没有 `ProducedJarImportReceipts`。
- CS1503：`WorldV2ResourceLedgerDto` 不能转换为 `ResourceLedgerSnapshot`。

这不是应通过扩展公开 DTO 解决的问题。回执不属于事件列表投影；将其公开会把内部幂等/恢复状态混入观察模型。

## 修正

文件：`test/JarSystem.Tests/WorldV2/WorldV2ApplicationServiceTests.cs`

- 首次导入与重复导入测试改用 `app.CaptureExecutionResourceCheckpoint().ResourceLedger` 读取持久化回执。
- `ResourceLedger.Restore` 使用同一份 `ResourceLedgerSnapshot`，继续验证回执能随账本捕获/恢复保留。
- 检查点恢复后的重复导入测试同样从 `restoredApp.CaptureExecutionResourceCheckpoint().ResourceLedger` 检查回执。

未修改 `WorldV2ResourceLedgerDto`，未改变公开 API，也未放宽任何断言。

## 提交

- 测试修正：`c5deb761d05b33890d05d7b35aab5efd150518ce`

## 验证状态

- 已通过源码签名核对：公开 DTO 与内部 ResourceLedgerSnapshot 分离；检查点的 `ResourceLedger` 属性类型为 `ResourceLedgerSnapshot`。
- 未在本次 GitHub 编辑环境执行 .NET 构建或测试。
- 本地下一步执行：
  ```powershell
  git pull --ff-only origin architecture/core-liquid-identity-consolidation
  dotnet run --project .\test\JarSystem.Tests\JarSystem.Tests.csproj -c Release
  ```
- 该修正只解决当前报告的编译错误；后续若暴露新的编译或测试失败，按新的输出继续定位，不预先宣称全套测试通过。

# 2026-11-26 Board Assignment 测试夹具排序遗漏修复

## 目标

解决本地完整测试输出中的唯一失败：

`WorldV2BoardRuleProfileOptionsTests.ValidateAcceptsVersionTwoFrozenBoardWithProductionDecompositionHierarchy`

失败表现为 `boardAssignmentSnapshot assignments must be ordered by stable slotId`。

## 起始状态

- 仓库：`Lazyair150/jar-system-dotnet`
- 分支：`architecture/core-liquid-identity-consolidation`
- 修复前 head：`98e97c6930bcf795f73bf1ff4812ad19a54949a1`
- 用户提供的本地测试摘要：Total 1041，Errors 0，Failed 1，Skipped 0。
- 失败断言位置：`WorldV2BoardRuleProfileOptionsTests.cs:115`。

## 根因

`WorldV2BoardAssignmentSnapshotOptions.Validate` 明确要求 assignment entries 按稳定 `SlotId` ordinal 排序。这是输入快照契约的一部分，验证器拒绝乱序数据是正确行为。

测试文件中第一个 parser fixture 在计算 fingerprint 前显式按 `SlotId` 排序；但第二个测试使用的共享工厂方法 `CreateParserAssignment` 直接对插入顺序计算 fingerprint，没有履行快照排序契约。因而失败不是生产验证器错误，也不是层级合法性判断错误，而是测试夹具构造不完整。

## 修改

- 文件：`test/JarSystem.Tests/WorldV2/WorldV2BoardRuleProfileOptionsTests.cs`
- 在 `CreateParserAssignment` 中，计算 `AssignmentFingerprint` 前新增：
  `assignment.Assignments.Sort((left, right) => string.CompareOrdinal(left.SlotId, right.SlotId));`
- 保留验证器的稳定排序拒绝逻辑，不放宽生产契约，不改 fingerprint 算法。
- 提交：`b7f0f7b1824b5cf40b2b6d97b51f6bf2adb951e1`

## 验证

- 已核对失败测试、共享 fixture 工厂与生产验证器的完整源码。
- 修复提交已写入整合分支。
- 本记录生成时尚未取得修复后本地完整测试输出或精确 head CI 结果；因此状态为 **代码修复已提交，验证待完成**，不得标记为通过。

建议验证命令：

```powershell
dotnet run --project .\test\JarSystem.Tests\JarSystem.Tests.csproj -c Release
```

验收要求：失败测试通过，完整套件 `Failed: 0`；同时确认 CI 针对修复后的精确 head 通过。

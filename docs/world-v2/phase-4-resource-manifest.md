# 阶段四 4.4a：求解计划资源清单

## 本阶段范围

本阶段只把不可变 `WorldOrderSolvePlan` 解析为 `WorldOrderResourceManifest`。它不修改工作台、不预留资源、不启动执行。预留簿与执行准入属于下一阶段 4.4b。

## 资源身份

`JarIndex` 是当前求解阶段的 `Before` 列表下标，不是罐子 ID。必须先读取：

`step.Before[step.Operation.JarIndex].Id`

A 类主台路径的下标空间是主台初始快照列表。B 类从台路径的下标空间是 PartitionSearch 从主台终态筛选出的从台池；不能把从台下标用于主台列表。每个步骤都按自身 `Before` 解析稳定 ID。

## 罐子占用

- A 类：锁定主台路径实际引用的罐子，包含 `MATCH` 直接交付罐。
- B 类：锁定主台路径实际引用的罐子，以及 `TransferJarIds`。两者按 ID 去重。
- B 类从台路径引用的每只罐子必须标记为 `MasterOrigin`，并且必须属于 `TransferJarIds`。
- `TransferJarIds` 必须全部存在于主台初始快照。

## 液体占用

`Consumed` 是单步从工作台游离液体池扣除的账目，不代表所有操作的总资源消耗：

| 操作 | 游离液体消耗 | 游离液体产生 |
|---|---|---|
| MATCH | 无 | 无 |
| POUR_IN | TargetLiquid × 1 | 无 |
| POUR_OUT | 无 | Before[JarIndex].Liquid × 1 |
| 空罐 RESHAPE | CostLiquid × 1 | 无 |
| 满罐 RESHAPE | 无；消耗罐内液体 | 无 |

资源清单重新验证每步 `Consumed` 与上述语义一致，并按路径前缀累计净消耗，取最大值。

A 类主台初始液体需求是主台路径前缀净消耗峰值。B 类除满足主台路径每个前缀外，还必须在主台路径结束后留出从台最低供应量。因此 B 类主台初始液体需求为：

`max(0, maxPrefixNet, finalNet + TransferLiquids)`

`TransferLiquids` 是从台路径开始时所需的最低游离液体供应量，按从台路径前缀净消耗峰值重新计算；它不是路径总消耗量，也不是从台最终余额。

## 计划一致性检查

清单生成拒绝以下情况：

- 结果不是 Feasible，或没有计划；
- 结果世界版本与快照版本不一致；
- JarIndex 越界、步骤缺少 Before/After/操作；
- Before/After 的罐子数量、顺序、身份或来源标记发生意外变化；
- 步骤修改了非目标罐子；
- 操作状态变化与操作类型不符；
- Consumed 与操作真实语义不符；
- B 类从台使用未转移的罐子，或 TransferLiquids 与从台路径重新计算值不一致；
- 转移罐子不存在于主台快照。

## 尚未实现

- 多订单之间的资源冲突检测；
- 资源预留/释放；
- 执行期间独占；
- 执行准入；
- 资源实际转移和执行步骤推进。

这些不能由资源清单生成器顺手承担。
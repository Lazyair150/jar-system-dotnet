# 4.10b-9a：符号解按岗位分账

> 状态：首个代码切片已提交，等待 CI；不是完整操作程序，也不是完整 B 类交接证明。

## 背景

现有 `WorldOrderSymbolicSolution` 已将每项需求的规则候选组合起来，但原来的三个资源汇总字段直接跨所有岗位聚合：

- `GrossFreeLiquidConsumption`
- `GrossFreeLiquidProduction`
- `GrossJarLiquidConsumption`

这些值可以作为全局诊断统计，却不能用于证明某个工作台的液体可用，也不能用于证明主台向从台完成了交接。Master 与 Slave 的资源账户具有不同所有权；同一种液体在两个岗位的余额不能直接相抵。

## 本切片

新增 `WorldOrderRoleResourceEffect`，并在 `WorldOrderSymbolicSolution.ResourceEffectsByRole` 暴露按岗位隔离的资源账本。

每个岗位分别记录：

- 步骤数；
- 游离液体毛消耗；
- 游离液体毛产出；
- Jar 内液体自耗。

实现约束：

1. 角色键采用 ordinal 排序，结果稳定可复现。
2. 每种液体的数量使用 checked 累加，溢出显式失败。
3. 返回只读字典，不能由调用方修改求解结果。
4. A 类只有 Master 角色时，不虚构 Slave 账本。
5. 原有全局汇总字段暂时保留以兼容既有调用，但明确限定为诊断统计；不得用来做资源可用性、净额抵扣或 Master→Slave 转移证明。

## 明确不在本切片声称完成的事情

岗位毛消耗/毛产出仍然不是可执行资源流证明。本模型尚未表达：

- 同一岗位内操作的先后顺序；
- 前置 PourOut 产生的液体能否在后续步骤中使用；
- 订单 staging 的时间峰值和拒收/丢弃；
- 主台在完成自身操作后可交接的精确液体数量；
- 从台只接收主台交接液体的逐步证明；
- 真实 Jar 身份、标记、位置与资源账户绑定。

因此本切片不生成 TransferPlan，不宣称容量可行，也不改变旧 Solver 的 Feasible/Infeasible 结论。

## 下一切片

4.10b-9b 将把单需求候选扩展为有序的符号操作节点，并显式表达输入 Jar 角色、输出 Jar 事实、规则身份、资源效果和前置依赖。之后才能按岗位模拟 staging 余额与峰值，再构建 Master→Slave 单向交接证明。资源绑定仍由既有 Manifest / Admission 边界负责，活动 Session 的恢复数据继续保留。

## 回归测试

`WorldOrderRoleResourceEffectTests` 覆盖：

- Master 与 Slave 同种液体的账户隔离；
- 单岗位 A 类解不产生 Slave 账本；
- 同岗位同液种的确定性累加与排序；
- 只读约束与非法角色拒绝；
- `WorldOrderSymbolicSolution` 同时暴露岗位账本并保留旧全局诊断汇总。

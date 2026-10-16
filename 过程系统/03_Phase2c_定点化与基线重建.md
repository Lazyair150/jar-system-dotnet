# Phase 2c 报告：产率定点化（修浮点漂移）与有意重建契约基线

> 生成时间：2026-10-16　生成者：Codex
> 前置：裁决"批准，有意重建契约基线"（用户，2026-10-16）。
> 格式沿用「板块报告」：问题 → 改动 → 因果论证 → 验证 → 遗留。

---

## 1. 问题（不是"格式美化"，是实测到的行为缺陷）

`ProducerModule.ShapeProductionLine` 的产出累加器是 `double`：
`Accumulated += 总速率; n = (int)Math.Floor(Accumulated); Accumulated -= n;`

两处后果：

1. **契约里带着浮点漂移**：`state_keys.txt` 实测为 `0.20000000000000018` / `0.8999999999999999` /
   `0.7999999999999999` / `0.6000000000000001` 等（**共 7 处**；P2a 报告只举了 3 例，此处更正）。
2. **潜在少产一个罐子**：累加值一旦落在整数**下方**（如 6.999999999999999），`Floor` 就少产一个；
   而且这依赖运行时/平台的浮点行为 ⇒ 与"同输入必同结果"的确定性目标冲突
   （Gaffer On Games 在 *Deterministic Lockstep* 里点名的正是这种情形）。

## 2. 改动

| 文件 | 改动 |
|---|---|
| `src/JarSystem.Core/Production/ProducerModule.cs` | `ShapeProductionLine` 内部改为**整数定点**（千分之一）：`_accumulatedMilli` + `Milli(rate) = round(rate×1000)`；`Accumulated` 属性换算回小数输出（对外类型与键名不变）；整除替代 `Math.Floor`（速率恒非负 ⇒ 语义等价） |
| `test/StrategyExperiment/Program.cs` | 该程序把结论数字**写死在字符串里**，实测值已位移 ⇒ 同步更新为新实测值，并追加一条"口径变更"说明（否则报告正文与表格自相矛盾） |

## 3. 因果论证：为什么它会改变结果（以及这为什么是对的）

* 语义上速率是**十进制内容**（配置写 `0.4`），"正确的"累加是十进制累加；定点化把"意图"变成"事实"。
* 但修掉漂移**会改变落在边界上的判定**：原先 `0.8999999999999999` 这类值在某些时刻会让一次产出延后一刻。
* 因此**必须有实测证据**，而不是"应该没影响"。见 §4.2。

## 4. 验证

### 4.1 九步门

| 项 | 结果 |
|---|---|
| 主测试 | `0 bugs / 2000 random + 0 bugs / 3000 capacity + 22 deterministic`（不变） |
| 契约快照 | **只差 `accumulated` 那 7 个值**（`fc` 输出 14 行 = 7 对），其它 216 行逐字相同；行数仍 223 |
| Schema / `contract.d.ts` / 协议 / JS | 全部不变（`loose=1`、18 命令 / 10 消息） |
| 求解器差分 | `SOLVER-OK (compared=4003, agree=4003, feasible=583, undetermined=0, targeted=3)`（不变） |
| 基线 | **有意重建**：`state_keys.txt` → `state_keys.baseline.txt`（223 行）；重建后 `VERDICT=OK` |

### 4.2 策略实验对照（定点化前 → 后）

| 策略 | 成功率 | 严格最优运行流数 |
|---|---|---|
| FIFO | 44.96% → **44.78%** | 1330 → **1321** |
| LIFO | 40.26% → **40.20%** | 585 → **586** |
| RareShapeFirst | 27.13% → 27.13% | 3 → 3 |
| Random | 23.31% → **23.27%** | 0 → 0 |

模式最优分布：FIFO 520 → **515**，LIFO 170 → **178**，其余 0。
配对符号检验（FIFO vs LIFO）：1331/606/196 → **1321/608/204**，p 值仍 `< 0.001`。

**结论**：位移 0.1–0.2pp，说明那处漂移**此前确实在翻转个别订单的结果**（不是纯显示问题）；
排序与显著性结论不变（FIFO 仍是最优默认策略）。

## 5. 遗留与风险

1. `Producer.Rates` 仍以 `double` 承载（它是契约字段 `producerStatus.producers[].rates`，不能改类型）；
   定点只在**内部**使用，舍入发生在注册/改速率边界（`MidpointRounding.AwayFromZero`）。
2. 定点精度 = 1/1000，与 `world.json` 的书写粒度（0.1~0.4）匹配；若将来允许更细速率（如 0.0001），
   需要提高精度并把该决定记录在案。
3. 除产率外，世界状态里已无其它浮点（形状/液体/计数/容量都是整数或字符串）。

## 6. 回滚

```bat
git checkout -- src\JarSystem.Core\Production\ProducerModule.cs test\StrategyExperiment\Program.cs
git checkout -- 重构报告\_contract\state_keys.baseline.txt
```

（若已重跑过 `verify.bat`，`state_keys.txt` 会被重新生成为新值，基线也需一并回退。）

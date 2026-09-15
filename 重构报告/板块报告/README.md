# 板块修复/构建报告 — 索引

> 本目录是重构的**最终交付物**：按板块记录「问题 → 改动 → 等价性论证 → 验证结果 → 遗留项」。
> 验证结果一律以用户在本机终端运行 `重构报告\verify.bat` 的实际输出为准；未验证的一律标注 **待验证**。

## 板块划分（对应《00 审计报告》§3）

| 板块 | 内容 | 报告文件 | 当前状态 |
|---|---|---|---|
| A+B | Domain（Jar/Workshop/WorkshopSnapshot）+ Config（Constants/Rules/ShapeRules） | `板块_AB_Domain与Config.md` | ✅ 报告已出（阶段 1+2A 改动）· **待验证** |
| C | Orders（Order/OrderEvent/FeasibilityChecker/WorkshopSwitchEvent + Feasibility/ + Switching/） | `板块_C_Orders.md` | ✅ 报告已出（阶段 1+2B/2D+3）· **待验证** |
| D | Solver（StateSpaceSolver/JarState/WorkshopInfo） | `板块_D_Solver.md` | ✅ 报告已出（阶段 1+2A/2B）· **待验证** |
| E | Executor（OrderExecutor + SolverStepApplier/GreedyPlanBuilder/JarSelector/JarStrategies；`SolverPathAdapter` 已随阶段 3-D 废弃删除） | `板块_E_Executor.md` | ✅ 报告已出（阶段 1+2C+3 策略 + **3-D 路径步骤强类型化**）· **待验证** |
| F | Commands（Command/CommandExecutor/JarCommands） | `板块_F_Commands.md` | ✅ 报告已出（阶段 1 批 3，含 Undo 修正 + S20）· **待验证** |
| G | Production（ProducerModule/ShapeProductionLine/生产服务） | `板块_G_Production.md` | ✅ 报告已出（阶段 1+2A/2E；G1/G2 明确记为遗留）· **待验证** |
| H | Facade（JarSystem + 4 个服务 + Bootstrap + Abstractions + StateDtos） | `板块_H_Facade.md` | ✅ 报告已出（阶段 1+2A/2E+3 DTO + **批 A 日志 + 批 E 状态收口**）· **待验证** |
| I | Web 胶水层（Program/JsonArgs/CommandDispatcher/WebSocketHub/IWebSocketSink/JarSystemService） | `板块_I_Web.md` | ✅ 报告已出（阶段 1 catch + 2F 拆分）· **待验证** |
| J | 测试工程（JarSystem.Test / StrategyExperiment） | `板块_J_测试.md` | ✅ 报告已出（新增 S20 与 `--dump-contract`）· **待验证** |
| K | 前端（app.js/canvas.js） | `板块_K_前端.md` | ✅ 报告已出（契约冻结，未改动；列出字段依赖清单） |
| L | 文档/UML/Godot | `板块_L_文档与UML.md` | ✅ 报告已出（UML 02/07 与 Godot 指南已同步） |

> **报告完备性：12/12 板块报告 + 索引已全部产出**（A+B 合为一份）。所有「待验证」项统一由 `重构报告\verify.bat` 与浏览器冒烟确认。

## 报告统一格式

1. **板块职责**与重构前的结构问题（引用审计编号）。
2. **改动清单**：文件 / 改动 / 对应审计条目。
3. **等价性论证**：逐条说明为什么行为与对外 JSON 不变。
4. **验证**：命令、期望值、实际输出（用户回贴）、结论。
5. **遗留项**：本板块未做的事与原因（含"评估后不引入"的模式决策）。

## 验证口径（所有板块共用）

```bat
重构报告\verify.bat
```

| 项目 | 期望值 |
|---|---|
| `dotnet build JarSystem.slnx` | 0 error / 0 warning |
| `JarSystem.Test` | `=== 0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic ===` |
| `StrategyExperiment` | FIFO 成功率最高（与 `问题/问题十测试记录-重跑.txt` 的排序一致） |
| Web 冒烟 | 创建订单 → 分析 → 执行 → 设主从 → 改容量 → 重置，全程无异常，切换动画出现 |

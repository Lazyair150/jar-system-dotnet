# 板块 J — 测试工程（修复报告）

> 范围：`test/JarSystem.Test/Program.cs`、`test/StrategyExperiment/Program.cs`
> 阶段：1（批 3：S20）+ 2（批 2E：契约快照模式）
> 验证状态：**待验证**（本板块就是验证手段本身，见 §5）

---

## 1. 板块职责

- `JarSystem.Test`：**双向一致性**随机化测试（可行必须执行成功；不可行执行成功同样计 bug）+ 罐子守恒断言 + 液体非负断言 + 容量压力测试 + 20 个确定性回归 + **契约快照模式**。
- `StrategyExperiment`：711 抽象结构模式 × 3 种子 × 4 策略的订单完成率对比（验证 FIFO 显著最优）。

## 2. 重构前的问题（引用审计）

| 编号 | 问题 | 证据 |
|---|---|---|
| J1 | 单文件顶层脚本，无测试框架/断言库，失败靠手写 `Run(...)` 与字符串比较 | `Program.cs`（596 行） |
| J2 | 测试直接读写 `master.Jars`/`LiquidStorage`/`sys.Workshops`/`sys.CurrentMaster` —— 这些是"测试注入口"，也是领域封装一直无法收口的原因 | 全文件 |
| J3 | 现场信息用自写 `DescribeWS`，与核心的序列化职责重复 | `:473` |
| J4 | 与 `StrategyExperiment` 的随机生成器逻辑重复 | 两文件 |
| — | 缺"对外契约"维度的测试（没有任何机制防止 JSON 键名漂移） | 审计 §7 |

## 3. 改动清单

| 文件 | 改动 | 阶段 |
|---|---|---|
| `JarSystem.Test/Program.cs` | 新增 `using JarSystem.Core.Commands;` | 1 |
| `JarSystem.Test/Program.cs` | **新增 S20**（倒入命令撤销后液体与标记复原）：直接调用 `PourInCommand.Execute`/`Undo`，断言"罐满+已标记+库存 1"→"罐空+未标记+库存 2" | 1 |
| `JarSystem.Test/Program.cs` | **新增 `--dump-contract` 模式**（顶部早返回，不影响默认测试）：固定场景导出 `GetState()` 全部键路径与值到 `重构报告/_contract/state_keys.txt`（`timestamp` 字段记为 `<volatile>`；对象按公共属性反射展开） | 2E |
| `重构报告/verify.bat` | 第 4 步运行契约快照，并与 `state_keys.baseline.txt` 比对（`fc`），结果写入 `_contract/diff.txt`；首次运行建立基线 | 2E |

## 4. 等价性论证（要点）

1. **默认测试路径零变化**：契约模式由 `args[0] == "--dump-contract"` 触发（`dotnet run` 不加参数时行为与重构前完全一致）；`Run(...)`/`Gen`/`GenCap`/`GenBan` 的断言与生成逻辑未改动。
2. **S20 是有效判据**：修正前的 `PourInCommand.Undo` 会因 `PourOut` 的「罐子已被操作」守卫直接失败 ⇒ 断言必然不成立；修正后必然成立。它不是恒真断言。
3. **契约快照不改核心**：`Flatten`/`DumpContract` 是测试工程内的局部函数（顶层语句要求下置于类型声明之前），只读 `GetState()` 并写文件。
4. **确定性用例数由 19 → 20**：这是新增 S20 的结果，属预期变化（`verify.bat` 的期望值已同步为 20）。

## 5. 验证

| 项目 | 期望 | 实际 |
|---|---|---|
| 主测试 | `=== 0 bugs / 2000 + 0 bugs / 3000 + 20 deterministic ===` 且无 `FAIL` 行 | 待回贴 |
| 策略实验 | 四策略成功率排序与 `问题/问题十测试记录-重跑.txt` 一致（FIFO 最高） | 待回贴 |
| 契约快照 | 首次运行打印 `contract dumped: ...` 并建立基线；再次运行为 `FC: no differences encountered` | 待回贴 |

## 6. 遗留项

| 项 | 说明 / 计划 |
|---|---|
| 单文件脚本、无测试框架（J1） | 保留现状。迁移到 xUnit 会让「2000 场景 + 20 确定性」的直观输出与 `verify.bat` 流程全部重做，收益与风险不成比例；已记录为"评估后不引入"（如需 CI，可后续再加一个 xUnit 工程做冒烟子集） |
| 测试直连内部状态（J2） | 这是**领域封装收口的阻塞项**：`sys.Workshops.Clear()/Add(...)`、`sys.CurrentMaster = 0`、`sys.ProducerModule = new ...`、`LiquidWarehouse[...]` 都是注入口。若要与测试同步改为意图方法（`AddWorkshop`/`SetMasterIndex`/`AddJar`），需要在同一批里改测试与核心，属独立工作项 |
| 生成器重复（J4） | 未合并：两处生成器的随机序列不同（种子/调用顺序不同），合一会改变实验与测试的随机序列 ⇒ 属行为变更，**不建议合并**（记录理由） |
| `DescribeWS` 与序列化重复（J3） | 保留（失败现场可读性优先） |
| `_test_diag.csx` | 该临时脚本用旧的字典式 `GetState()` 访问，阶段 3 DTO 化后已失效；建议删除（我的工具无法删文件） |

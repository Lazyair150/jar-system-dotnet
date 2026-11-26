# 2026-11-26 外部 Jar 导入精确回执（Core）

## 工作方式与依据

采用“先固定过程依据、同组修改、末尾集中验证”的方式，不将每次文件写入拆成一次 CI 等待。源码审查、测试用例设计和版本化记录是本轮实施依据；PR/CI 只在成组工作完成后作为补充验证，不作为每步推进的阻塞条件。

- 仓库：`Lazyair150/jar-system-dotnet`
- 分支：`architecture/core-liquid-identity-consolidation`
- 修改前 HEAD：`e1e89cad5e0f11f369c8ade814140a7b4cc8df74`
- 核心源码：ResourceLedger、WorldV2ApplicationService、ExecutionCheckpoint Wire DTO、JSON Codec
- 核心测试：WorldV2ApplicationServiceTests

## 问题与不变量

Core 原实现将外部 Jar 导入的操作 ID 与 payload 指纹写入账本，但没有保留首次容量分配结果。相同请求重放时只能返回 duplicate 与 0/0/0，不能据此推断原始入库/丢弃数量。事件日志有容量上限，不能把事件扫描作为长期查询回执的替代品。

1. 同一个外部操作 ID + 规范化请求 payload 只能产生一次资源变化。
2. 首次提交的请求数、逐 shape 入库数、逐 shape 丢弃数及 payload SHA-256 必须作为账本持久回执保存。
3. 回执与账本事件、操作 ID / 指纹一起进入 ResourceLedger snapshot、Wire DTO 和 JSON 检查点。
4. 事件即使被有界账本裁剪，导入结果回执仍随操作 ID 保留。
5. 精确重放返回原始计数并标记 duplicate；旧检查点只有 operation tombstone、没有结果回执时，仍返回 outcome unknown，不伪造首次结果。
6. 逐 shape 满足 stored + discarded = requested；总量守恒且在 Int32 范围内。
7. 这不是跨组件数据库事务承诺。Host 独占区串行化操作，仓库写入仍在预先构造/校验后执行。

## 本轮实现

- 新增 ResourceLedgerProducedJarImportReceipt，保存外部操作身份、账本操作身份、payload 指纹及三个逐 shape 数量映射。
- RecordProducedJarImportOnce 与账本批次/指纹使用同一提交路径；提供只读精确回执查询。
- Snapshot 捕获、深拷贝、恢复和 RestoreFrom 均保留此回执。
- Wire DTO 增加 ProducedJarImportReceipts；检查点 schema 从 10 升至 11。
- schema 10 及更早版本仍可读取；缺少精确回执的历史不会被补造。
- 首次提交与可重放精确回执返回 HasExactReceipt=true；仅有旧去重标记时返回 duplicate + HasExactReceipt=false。
- 测试增强覆盖首次、重复、满仓溢出重放与执行检查点恢复。

## 集中验证清单

- [ ] Core Release build
- [ ] WorldV2ApplicationServiceTests 相关用例
- [ ] ResourceLedgerSnapshotTests
- [ ] WorldV2CheckpointJsonCodecTests：schema 10 兼容及 schema 11 必需字段
- [ ] Core 整体测试套件
- [ ] Space bridge / parser receipt 接入与 probe 同步

只有集中运行后才将测试项改为通过。未运行的构建、测试、Godot Probe 或 GUI 试玩不标记为通过。

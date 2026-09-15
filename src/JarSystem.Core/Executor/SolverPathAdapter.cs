// 阶段 3-D（路径步骤强类型化）后本文件已废弃 —— 请手动删除。
//
// 原内容：`internal static class SolverPathAdapter`
//   - Normalize(object?)：把弱类型步骤字典拍平（并兼容 Node 时代的 ji/ts/tl/cst 缩写）；
//   - ReadType/ReadJarId/ReadJarIndex/ReadJarShape/ReadJarLiquid/ReadTargetShape/
//     ReadTargetLiquid/ReadCostLiquid/ReadReshapeMode：从字典里 TryGetValue + as 转换。
//
// 废弃原因：
//   1. 路径步骤的唯一生产端是 PathSummaryFormatter.Summary，它现在直接产出
//      JarSystem.Core.Orders.SolverStepDto（14 个 [JsonPropertyName] 字段），
//      消费端 SolverStepApplier 直接读属性 ⇒ 本适配器无任何调用者；
//   2. Normalize 的「旧嵌套格式兼容」在现在的代码库里不可达：没有任何地方把
//      `operation` 子字典或 ji/ts/tl/cst 缩写写进路径（那些是重构前 Node 版遗留），
//      也不存在从外部 JSON 反序列化路径的入口；
//   3. 保留它会让「弱类型路径」看起来仍被支持，与本次改造的目的相反。
//
// 保留本注释文件只是因为自动化工具有文件写入权限、无删除权限：用户删除即可
// （同类待办：IndependentExecutor.cs → OrderExecutor.cs 改名，见 04_重构总结.md §6）。
//
// 详见：重构报告/06_阶段3D_路径步骤强类型化.md

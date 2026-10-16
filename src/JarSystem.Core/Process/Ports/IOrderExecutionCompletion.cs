using JarSystem.Core.Orders;

namespace JarSystem.Core.Process;

/// <summary>
/// **订单执行的收尾出口**（P4c：把 `Process ↔ Services` 的成环解开）。
///
/// 为什么需要它：过程系统的工作体（<see cref="OrderProcessAdvancer"/>）在工作结束时必须调用
/// 内核的收尾逻辑（交付 / 失败落账 / 事件历史）。直接依赖 `OrderService` 会让
/// **Process ↔ Services 互相引用**（Services 的投影层本来就要读过程实例）。
/// 把"收尾"抽成 Process 自己定义的端口、由 `OrderService` 实现，方向就变成单向：
/// <code>Services → Process → Abstractions</code>。
/// </summary>
internal interface IOrderExecutionCompletion
{
    /// <summary>执行收尾：返回与既有 `ExecuteOrder` 相同的五元组（成功 / 原因 / 事件报告 / 是否转移 / 操作日志）。</summary>
    (bool Success, string? Reason, Dictionary<string, object?>? Event, bool TransferOccurred,
        List<ExecutionLogEntry>? Operations) Complete(OrderExecutionSession session);
}

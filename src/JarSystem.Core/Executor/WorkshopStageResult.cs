using System.Collections.Generic;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Executor;

/// <summary>贪心执行一个工作台阶段的产出（操作日志 + 错误原因），原为文件级内部类。</summary>
internal sealed class WorkshopStageResult
{
    public List<ExecutionLogEntry> Ops { get; set; } = new();
    public List<string> Errs { get; set; } = new();
}

using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Abstractions;

/// <summary>一条错误记录。**init**：审计数据不可变。</summary>
public class LogError
{
    public string? Phase { get; init; }
    public string? Command { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// **命令的记账口**（把 `Commands ↔ Orders` 的成环解开）。
///
/// 一条命令只做两件事：改工作台（`Domain`），以及往记账簿上写一笔（本接口）。
/// 它**不需要**认识"订单执行"这个更大的概念 —— 那是 `Orders` 的事。
/// 此前三个命令文件的每个签名都写着 `OrderEvent? event_`，于是 `Commands → Orders`；
/// 而 `Orders/OrderEvent` 又要 `new CommandExecutor(...)` 驱动命令，于是环就闭死了。
///
/// 抽本接口后：`Commands → Abstractions ← Orders`，两侧单向。
/// 实测这三个文件只用 `OrderEvent` 的三个成员（`AppendError` / `Errors` / `RecordTransfer`），
/// 接口就是这三条，不多不少。
/// </summary>
public interface ICommandJournal
{
    /// <summary>本阶段累积的错误（审计口径，只读）。</summary>
    IReadOnlyList<LogError> Errors { get; }

    /// <summary>记一条错误。</summary>
    void AppendError(LogError error);

    /// <summary>记一次罐子转移（回滚依据：这些罐子已经跨台移动过）。</summary>
    void RecordTransfer(Jar jar);
}

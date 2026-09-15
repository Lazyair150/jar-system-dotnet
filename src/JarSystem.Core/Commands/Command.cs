using System;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Commands;

/// <summary>
/// Command 基类 - 所有陶罐操作的原子单元。
/// 支持：执行、撤销、序列化。
///
/// 阶段 3：执行/撤销的返回值由 <c>object</c> 收紧为 <see cref="CommandResult"/>，
/// 调用方不再需要 <c>is CommandResult</c> / <c>as bool</c> 双轨判定（多态契约回归正常）。
/// </summary>
public abstract class Command
{
    public string Type { get; }
    public Dictionary<string, object?> Params { get; }

    /// <summary>是否已成功执行（决定 <see cref="Undo"/> 是否生效）。</summary>
    public bool Executed { get; protected set; }

    /// <summary>最近一次执行结果。</summary>
    public CommandResult? Result { get; protected set; }

    /// <summary>最近一次执行时间（Unix 毫秒）。</summary>
    public long? Timestamp { get; protected set; }

    protected Command(string type, Dictionary<string, object?>? params_ = null)
    {
        Type = type;
        Params = params_ ?? new();
        Executed = false;
        Result = null;
        Timestamp = null;
    }

    /// <summary>执行本命令（子类实现）。</summary>
    public abstract CommandResult Execute(Workshop workshop, OrderEvent? event_);

    /// <summary>撤销本命令（用于回滚；子类实现）。</summary>
    public abstract CommandResult Undo(Workshop workshop, OrderEvent? event_);

    public Dictionary<string, object?> ToJson()
    {
        return new()
        {
            ["type"] = Type,
            ["params"] = Params,
            ["executed"] = Executed,
            ["result"] = Result,
            ["timestamp"] = Timestamp
        };
    }
}

/// <summary>命令执行结果（成功与否 + 失败原因文案）。</summary>
public class CommandResult
{
    public bool Success { get; set; }
    public string? Reason { get; set; }
}

using System;
using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Services;

namespace JarSystem.Core.Commands;

/// <summary>
/// CommandExecutor - 命令执行器
/// 负责：命令队列管理、事务边界、回滚
///
/// 阶段 3-A：回滚失败时除写入 <c>event_.Errors</c>（对外契约）外，另经 <see cref="ISystemLog"/> 留痕
/// —— 原实现在 <c>event_</c> 为 null 时会整条丢失（Node 版此处是 <c>console.error</c>，移植时丢了）。
/// </summary>
public class CommandExecutor
{
    private readonly ISystemLog _log;

    /// <summary>默认装配控制台日志（宿主可注入自己的实现）。</summary>
    public CommandExecutor(ISystemLog? log = null)
    {
        _log = log ?? ConsoleSystemLog.Instance;
    }

    public List<Command> History { get; } = new();

    // 执行单条命令
    public CommandResult Execute(Command command, Workshop workshop, OrderEvent? event_)
    {
        var result = command.Execute(workshop, event_);
        if (result.Success)
            History.Add(command);
        return result;
    }

    // 批量执行（事务）
    public (bool Success, int ExecutedCount, string? Reason) ExecuteBatch(
        List<Command> commands, Workshop workshop, OrderEvent? event_)
    {
        var executed = new List<Command>();
        foreach (var cmd in commands)
        {
            var result = Execute(cmd, workshop, event_);
            if (!result.Success)
            {
                // 事务失败，回滚已执行的
                Rollback(executed, workshop, event_);
                return (false, executed.Count, result.Reason);
            }
            executed.Add(cmd);
        }
        return (true, executed.Count, null);
    }

    // 回滚（逆序）。单条撤销失败不中断其余回滚，但必须留下痕迹
    // （原 Node 版此处为 console.error，移植到 .NET 时丢失了该日志 —— 阶段 3-A 经 ISystemLog 补回）
    public void Rollback(List<Command> commands, Workshop workshop, OrderEvent? event_)
    {
        // 逆序回滚
        for (int i = commands.Count - 1; i >= 0; i--)
        {
            try { commands[i].Undo(workshop, event_); }
            catch (Exception ex)
            {
                // 契约留痕：保持原样（event_ 为 null 时本行无效果，故下一行补日志出口）
                event_?.Errors.Add(new LogError
                {
                    Phase = "rollback",
                    Command = commands[i].Type,
                    Reason = "回滚失败: " + ex.Message
                });
                _log.Error("回滚失败: 命令 " + commands[i].Type + " 撤销异常 - " + ex.Message, ex);
            }
        }
    }

    // 清空历史
    public void Clear()
    {
        History.Clear();
    }

    public List<Dictionary<string, object?>> GetHistory()
    {
        return History.ConvertAll(cmd => cmd.ToJson());
    }
}

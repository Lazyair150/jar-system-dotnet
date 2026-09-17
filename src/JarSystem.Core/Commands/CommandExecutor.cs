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

    // 私有列表 + 只读视图：原先 public List<Command> 会让调用方
    // Add/Clear 掉历史（而历史是审计与回滚的依据）。
    private readonly List<Command> _history = new();

    // 真只读包装（2026-09-16 全量只读化）：接口类型挡不住 `((List<Command>)exec.History).Clear()`。
    private readonly IReadOnlyList<Command> _historyView;

    /// <summary>默认装配控制台日志（宿主可注入自己的实现）。</summary>
    public CommandExecutor(ISystemLog? log = null)
    {
        _log = log ?? ConsoleSystemLog.Instance;
        _historyView = _history.AsReadOnly();
    }

    /// <summary>已成功执行的命令历史（对外只读）。</summary>
    public IReadOnlyList<Command> History => _historyView;

    // 执行单条命令
    public CommandResult Execute(Command command, Workshop workshop, OrderEvent? event_)
    {
        var result = command.Execute(workshop, event_);
        if (result.Success)
            _history.Add(command);
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
            try
            {
                var undone = commands[i].Undo(workshop, event_);

                // ⚠️ **撤销失败有两条通路**：抛异常，或者**返回 Success=false**
                // （`TransferCommand` / `PourOutCommand` / `ReshapeEmptyCommand` 都用返回值表示失败）。
                // 原实现只看异常，返回值直接丢掉 ⇒ 那三种命令的失败**连一行痕迹都没有**，
                // 而本类注释却声称"回滚失败时经 ISystemLog 留痕"（2026-09-16 修：返回值现在也留痕）。
                if (!undone.Success)
                {
                    var why = string.IsNullOrEmpty(undone.Reason) ? "撤销未成功（未给出原因）" : undone.Reason!;
                    event_?.AppendError(new LogError
                    {
                        Phase = "rollback",
                        Command = commands[i].Type,
                        Reason = "回滚失败: " + why
                    });
                    _log.Error("回滚失败: 命令 " + commands[i].Type + " 撤销返回失败 - " + why);
                }
            }
            catch (Exception ex)
            {
                // 契约留痕：保持原样（event_ 为 null 时本行无效果，故下一行补日志出口）
                event_?.AppendError(new LogError
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
        _history.Clear();
    }

    public List<Dictionary<string, object?>> GetHistory()
    {
        return _history.ConvertAll(cmd => cmd.ToJson());
    }
}

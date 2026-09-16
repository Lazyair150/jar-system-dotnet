using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Commands;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

public class OrderEvent
{
    private readonly SlaveRollback _rollback = new();

    public Order Order { get; }
    public Workshop Master { get; }
    public Workshop Slave { get; }
    public string Status { get; set; }
    public WorkshopSnapshot? MasterSnapshot { get; set; }
    public WorkshopSnapshot? SlaveSnapshot { get; set; }
    public bool TransferOccurred { get; set; }
    public string CurrentPhase { get; set; } = Constants.OrderPhase.NONE;
    public List<ExecutionLogEntry> ExecutionLog { get; set; } = new();
    public List<LogError> Errors { get; set; } = new();
    public List<Jar> DeliveredJars { get; set; } = new();
    public HashSet<int> TransferredJarIds { get; set; } = new();
    public Dictionary<string, int> TransferredLiquids { get; set; } = new();
    public HashSet<int> SlaveOriginalJarIds { get; set; } = new();
    public CommandExecutor Executor { get; set; }

    /// <param name="log">可观测性出口（阶段 3-A）：透传给本事件的命令执行器，用于回滚失败留痕；null 用默认控制台日志。</param>
    public OrderEvent(Order order, Workshop masterWorkshop, Workshop slaveWorkshop, ISystemLog? log = null)
    {
        Order = order;
        Master = masterWorkshop;
        Slave = slaveWorkshop;
        Status = Constants.OrderStatus.PENDING;
        Executor = new CommandExecutor(log);
    }

    public bool Start()
    {
        MasterSnapshot = Master.CreateSnapshot();
        SlaveSnapshot = Slave.CreateSnapshot();
        SlaveOriginalJarIds = new HashSet<int>(Slave.Jars.Select(j => j.Id!.Value));
        Master.ClearMarks();
        Slave.ClearMarks();
        Status = Constants.OrderStatus.EXECUTING;
        CurrentPhase = Constants.OrderPhase.MASTER;
        Executor.Clear();
        return true;
    }

    public CommandResult ExecuteCommand(Command command, Workshop workshop)
    {
        bool isMaster = workshop == Master;
        bool isSlave = workshop == Slave;
        if (!isMaster && !isSlave)
            return new CommandResult { Success = false, Reason = "非法工作台" };
        if (isMaster && CurrentPhase != Constants.OrderPhase.MASTER)
            return new CommandResult { Success = false, Reason = "当前不在主台阶段" };
        if (isSlave && CurrentPhase != Constants.OrderPhase.SLAVE)
            return new CommandResult { Success = false, Reason = "当前不在从台阶段" };

        var result = Executor.Execute(command, workshop, this);

        string? jarDesc = null;
        if (command is PourInCommand pic) jarDesc = pic.Jar.GetDescription();
        else if (command is ReshapeEmptyCommand rec) jarDesc = rec.Jar.GetDescription();
        else if (command is PourOutCommand poc) jarDesc = poc.Jar.GetDescription();
        
        else if (command is TransferCommand tc) jarDesc = tc.Jar.GetDescription();

        if (result.Success)
        {
            ExecutionLog.Add(new ExecutionLogEntry
            {
                Phase = CurrentPhase,
                Workshop = workshop.Name,
                Command = command.Type,
                Jar = jarDesc
            });
        }
        else
        {
            Errors.Add(new LogError
            {
                Phase = CurrentPhase,
                Command = command.Type,
                Reason = result.Reason
            });
        }
        return result;
    }

    public (bool Success, int ExecutedCount, string? Reason) ExecuteBatch(
        List<Command> commands, Workshop workshop)
    {
        return Executor.ExecuteBatch(commands, workshop, this);
    }

    public CommandResult AdvanceToSlavePhase()
    {
        if (CurrentPhase != Constants.OrderPhase.MASTER)
            return new CommandResult { Success = false, Reason = "不在主台阶段" };
        CurrentPhase = Constants.OrderPhase.SLAVE;
        TransferOccurred = true;
        return new CommandResult { Success = true };
    }

    public void RecordTransfer(Jar jar)
    {
        if (jar.Id.HasValue)
            TransferredJarIds.Add(jar.Id.Value);
    }

    public void RecordLiquidTransfer(string liquid, int amount)
    {
        TransferredLiquids[liquid] = TransferredLiquids.GetValueOrDefault(liquid, 0) + amount;
    }

    public (bool CanDeliver, string Location) CheckDelivery()
    {
        var deliveryWorkshop = Order.Type == Constants.OrderType.A ? Master : Slave;
        var satisfied = Order.IsSatisfiedBy(deliveryWorkshop.Jars);
        return (satisfied, Order.Type == Constants.OrderType.A ? "主" : "从");
    }

    public (bool Success, string? Reason) Deliver()
    {
        var check = CheckDelivery();
        if (!check.CanDeliver) return Fail("需求未完全满足");

        var deliveryWorkshop = Order.Type == Constants.OrderType.A ? Master : Slave;
        var jarsToDeliver = new List<Jar>();
        var matched = JarMatcher.MatchAll(deliveryWorkshop.Jars, Order.Requirements, JarMatcher.Matches);
        foreach (var idx in matched)
            if (idx >= 0) jarsToDeliver.Add(deliveryWorkshop.Jars[idx]);

        foreach (var jar in jarsToDeliver)
            deliveryWorkshop.RemoveJar(jar);
        DeliveredJars = jarsToDeliver;

        if (Order.Type == Constants.OrderType.B)
            RollbackSlaveToMaster();

        Master.ClearMarks();
        Slave.ClearMarks();
        Status = Constants.OrderStatus.SUCCESS;
        return (true, null);
    }

    public (bool Success, string? Reason) Fail(string? reason)
    {
        Master.ClearMarks();
        Slave.ClearMarks();
        RollbackSlaveToMaster();
        Status = Constants.OrderStatus.FAILED;
        return (false, reason);
    }

    /// <summary>
    /// 把本次转移过来的罐子与未用液体退回主台（主台接不下的直接丢弃）。
    /// 语义细节全部移入 <see cref="SlaveRollback"/>（归属判定 / 禁令边界 / 容量丢弃三件事一处说清）。
    /// </summary>
    public void RollbackSlaveToMaster() =>
        _rollback.Execute(Master, Slave, TransferredJarIds, SlaveOriginalJarIds, TransferredLiquids, ExecutionLog);

    public Dictionary<string, object?> GetReport()
    {
        return new()
        {
            ["order"] = Order.ToString(),
            ["status"] = Status,
            ["master"] = Master.Name,
            ["slave"] = Slave.Name,
            ["transferOccurred"] = TransferOccurred,
            ["deliveredCount"] = DeliveredJars.Count,
            ["log"] = ExecutionLog,
            ["errors"] = Errors,
            ["commandHistory"] = Executor.GetHistory()
        };
    }
}

public class ExecutionLogEntry
{
    public string? Phase { get; set; }
    public string? Workshop { get; set; }
    public string? Command { get; set; }
    public string? Action { get; set; }
    public string? Description { get; set; }
    public string? Jar { get; set; }
    public string? Liquid { get; set; }
    public int? Amount { get; set; }
    public string? Reason { get; set; }
}

public class LogError
{
    public string? Phase { get; set; }
    public string? Command { get; set; }
    public string? Reason { get; set; }
}

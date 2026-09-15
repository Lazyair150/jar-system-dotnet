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

    public void RollbackSlaveToMaster()
    {
        var slaveJars = new List<Jar>(Slave.Jars);

        foreach (var jar in slaveJars)
        {
            if (jar == null) continue;
            var isTransferred = jar.Id.HasValue && TransferredJarIds.Contains(jar.Id.Value);
            var isOriginal = jar.Id.HasValue && SlaveOriginalJarIds.Contains(jar.Id.Value);

            if (isTransferred && !isOriginal)
            {
                Slave.TransferOut(jar);
                // 禁液令不影响满罐转移：装满被主台禁用的液体的罐子原样归还，
                // 不在此清空罐内液体（禁令只作用于倒入、游离存储与改形成本）。
                if (!Master.TransferIn(jar))
                {
                    ExecutionLog.Add(new ExecutionLogEntry
                    {
                        Phase = "rollback",
                        Command = "discard_jar",
                        Jar = jar.GetDescription(),
                        Reason = "主台罐子容量满，丢弃（仓库单向流动，不回收）"
                    });
                }
            }
        }

        foreach (var kv in TransferredLiquids)
        {
            var liquid = kv.Key;
            var amount = Math.Min(kv.Value, Slave.LiquidStorage.GetValueOrDefault(liquid, 0));
            if (amount > 0)
            {
                Slave.RemoveLiquid(liquid, amount);
                // 部分填充：主台可接收多少就接收多少，剩余丢弃（仓库单向流动，不回收）
                int accepted = Master.TryReceiveLiquid(liquid, amount);
                int remainder = amount - accepted;
                if (remainder > 0)
                {
                    ExecutionLog.Add(new ExecutionLogEntry
                    {
                        Phase = "rollback",
                        Command = "discard_liquid",
                        Reason = (Master.IsBanned(liquid) ? "主台禁" + liquid : "主台液体容量满") + "，" + liquid + "×" + remainder + " 丢弃（仓库单向流动，不回收）"
                    });
                }
            }
        }
    }

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

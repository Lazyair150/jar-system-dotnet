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

    // ------------------------------------------------------------
    // 状态一律**对外只读**：私有可变容器 + IReadOnly* 视图，变更只走本类的具名意图方法。
    // 此前这 15 个成员全是 public { get; set; }，容器还是可变的 List/HashSet/Dictionary
    // ⇒ 外部既能改状态、也能直接 Add 进执行日志与错误表，而这些恰恰是
    // 「审计留痕（GetReport / eventHistory）」与「回滚依据（TransferredJarIds）」的数据源。
    // 写入点已 grep 确认：除本类自身，只有 OrderExecutor（fallback 日志）、
    // CommandExecutor（回滚失败错误）、SlaveRollback（丢弃日志）三处，均已改为具名方法。
    // ------------------------------------------------------------
    private readonly List<ExecutionLogEntry> _executionLog = new();
    private readonly List<LogError> _errors = new();
    private readonly List<Jar> _deliveredJars = new();
    private readonly HashSet<int> _transferredJarIds = new();
    private readonly Dictionary<string, int> _transferredLiquids = new();
    private readonly HashSet<int> _slaveOriginalJarIds = new();

    public string Status { get; private set; }
    public WorkshopSnapshot? MasterSnapshot { get; private set; }
    public WorkshopSnapshot? SlaveSnapshot { get; private set; }
    public bool TransferOccurred { get; private set; }
    public string CurrentPhase { get; private set; } = Constants.OrderPhase.NONE;

    public IReadOnlyList<ExecutionLogEntry> ExecutionLog => _executionLog;
    public IReadOnlyList<LogError> Errors => _errors;
    public IReadOnlyList<Jar> DeliveredJars => _deliveredJars;
    public IReadOnlyCollection<int> TransferredJarIds => _transferredJarIds;
    public IReadOnlyDictionary<string, int> TransferredLiquids => _transferredLiquids;
    public IReadOnlyCollection<int> SlaveOriginalJarIds => _slaveOriginalJarIds;
    public CommandExecutor Executor { get; }

    /// <summary>追加一条执行日志（仅供本程序集内的执行 / 回滚路径调用）。</summary>
    internal void AppendLog(ExecutionLogEntry entry) => _executionLog.Add(entry);

    /// <summary>追加一条错误记录（仅供本程序集内的命令执行 / 回滚路径调用）。</summary>
    internal void AppendError(LogError error) => _errors.Add(error);

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
        _slaveOriginalJarIds.Clear();
        foreach (var j in Slave.Jars)
            if (j.Id.HasValue) _slaveOriginalJarIds.Add(j.Id.Value);
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
            _executionLog.Add(new ExecutionLogEntry
            {
                Phase = CurrentPhase,
                Workshop = workshop.Name,
                Command = command.Type,
                Jar = jarDesc
            });
        }
        else
        {
            _errors.Add(new LogError
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
            _transferredJarIds.Add(jar.Id.Value);
    }

    public void RecordLiquidTransfer(string liquid, int amount)
    {
        _transferredLiquids[liquid] = _transferredLiquids.GetValueOrDefault(liquid, 0) + amount;
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
        _deliveredJars.Clear();
        _deliveredJars.AddRange(jarsToDeliver);

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
        _rollback.Execute(Master, Slave, _transferredJarIds, _slaveOriginalJarIds, _transferredLiquids, AppendLog);

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

/// <summary>执行日志的一条记录。**init**：日志是审计数据（对外报告的数据源），构造后不得再改。</summary>
public class ExecutionLogEntry
{
    public string? Phase { get; init; }
    public string? Workshop { get; init; }
    public string? Command { get; init; }
    public string? Action { get; init; }
    public string? Description { get; init; }
    public string? Jar { get; init; }
    public string? Liquid { get; init; }
    public int? Amount { get; init; }
    public string? Reason { get; init; }
}

/// <summary>一条错误记录。**init**：同上，审计数据不可变。</summary>
public class LogError
{
    public string? Phase { get; init; }
    public string? Command { get; init; }
    public string? Reason { get; init; }
}

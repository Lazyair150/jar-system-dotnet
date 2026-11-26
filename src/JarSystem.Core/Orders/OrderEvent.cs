using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Commands;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;

namespace JarSystem.Core.Orders;

public class OrderEvent : ICommandJournal
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

    // 真只读包装（2026-09-16 全量只读化）：这些是**审计与回滚的依据**，交出裸 List/Dictionary 时
    // 外部可以 `((List<Jar>)ev.DeliveredJars).Clear()` 把回滚依据抹掉（而接口类型本身挡不住向下转型）。
    private readonly IReadOnlyList<ExecutionLogEntry> _executionLogView;
    private readonly IReadOnlyList<LogError> _errorsView;
    private readonly IReadOnlyList<Jar> _deliveredJarsView;

    /// <summary>执行报告只读取订单的单一状态源。</summary>
    public string Status => Order.Status;
    public WorkshopSnapshot? MasterSnapshot { get; private set; }
    public WorkshopSnapshot? SlaveSnapshot { get; private set; }
    public bool TransferOccurred { get; private set; }
    /// <summary>
    /// A post-publication rollback/projection failure leaves the legacy event's physical and
    /// ledger state committed or uncertain. Keep the order executing and block further commands;
    /// callers must surface the fault rather than retry delivery or mark the order failed/succeeded.
    /// </summary>
    public bool IsTransactionFaulted { get; private set; }
    public string? TransactionFaultReason { get; private set; }
    public string CurrentPhase { get; private set; } = Constants.OrderPhase.NONE;

    public IReadOnlyList<ExecutionLogEntry> ExecutionLog => _executionLogView;
    public IReadOnlyList<LogError> Errors => _errorsView;
    public IReadOnlyList<Jar> DeliveredJars => _deliveredJarsView;

    /// <summary>本单实际转移过的罐子 Id（只读；供执行守卫与审计）。</summary>
    public IReadOnlyCollection<int> TransferredJarIds => _transferredJarIds;

    public CommandExecutor Executor { get; }

    private readonly Action<int>? _countDiscardedJars;
    private readonly Action<string, int>? _countDiscardedLiquids;

    // 资源账本：从台回落是"资源从从台回到主台"的唯一产地，必须在这里指得到原因（return / discard）。
    private readonly ResourceLedger? _ledger;
    private readonly Func<int>? _tickOf;

    /// <summary>追加一条执行日志（仅供本程序集内的执行 / 回滚路径调用）。</summary>
    internal void AppendLog(ExecutionLogEntry entry) => _executionLog.Add(entry);

    /// <summary>追加一条错误记录（仅供本程序集内的命令执行 / 回滚路径调用）。</summary>
    public void AppendError(LogError error) => _errors.Add(error);

    /// <param name="log">可观测性出口（阶段 3-A）：透传给本事件的命令执行器，用于回滚失败留痕；null 用默认控制台日志。</param>
    /// <param name="ledger">资源账本（只读投影；null 表示不要溯源）。</param>
    /// <param name="tickOf">取当前逻辑 tick 的出口。</param>
    public OrderEvent(Order order, Workshop masterWorkshop, Workshop slaveWorkshop, ISystemLog? log = null,
        Action<int>? countDiscardedJars = null, Action<string, int>? countDiscardedLiquids = null,
        ResourceLedger? ledger = null, Func<int>? tickOf = null)
    {
        // 只读包装最先建立（活视图）——构造期间/之后读公开视图都不会拿到 null
        _executionLogView = _executionLog.AsReadOnly();
        _errorsView = _errors.AsReadOnly();
        _deliveredJarsView = _deliveredJars.AsReadOnly();

        Order = order;
        Master = masterWorkshop;
        Slave = slaveWorkshop;
        Executor = new CommandExecutor(log);
        _countDiscardedJars = countDiscardedJars;
        _countDiscardedLiquids = countDiscardedLiquids;
        _ledger = ledger;
        _tickOf = tickOf;
    }

    public bool Start()
    {
        if (IsTransactionFaulted) return false;
        MasterSnapshot = Master.CreateSnapshot();
        SlaveSnapshot = Slave.CreateSnapshot();
        Master.ClearMarks();
        Slave.ClearMarks();
        Order.MarkExecuting();
        CurrentPhase = Constants.OrderPhase.MASTER;
        Executor.Clear();
        return true;
    }

    public CommandResult ExecuteCommand(Command command, Workshop workshop)
    {
        if (IsTransactionFaulted)
            return new CommandResult { Success = false, Reason = TransactionFaultReason ?? "订单事务状态不确定，已冻结。" };
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
        if (IsTransactionFaulted)
            return (false, 0, TransactionFaultReason ?? "订单事务状态不确定，已冻结。");
        return Executor.ExecuteBatch(commands, workshop, this);
    }

    public CommandResult AdvanceToSlavePhase()
    {
        if (IsTransactionFaulted)
            return new CommandResult { Success = false, Reason = TransactionFaultReason ?? "订单事务状态不确定，已冻结。" };
        if (CurrentPhase != Constants.OrderPhase.MASTER)
            return new CommandResult { Success = false, Reason = "不在主台阶段" };
        CurrentPhase = Constants.OrderPhase.SLAVE;
        TransferOccurred = true;
        return new CommandResult { Success = true };
    }

    public void RecordTransfer(Jar jar)
    {
        ThrowIfTransactionFaulted();
        if (jar.Id.HasValue)
            _transferredJarIds.Add(jar.Id.Value);
    }

    public (bool CanDeliver, string Location) CheckDelivery()
    {
        var deliveryWorkshop = Order.Type == Constants.OrderType.A ? Master : Slave;
        var satisfied = Order.IsSatisfiedBy(deliveryWorkshop.Jars);
        return (satisfied, Order.Type == Constants.OrderType.A ? "主" : "从");
    }

    public (bool Success, string? Reason) Deliver()
    {
        if (IsTransactionFaulted)
            return (false, TransactionFaultReason ?? "订单事务状态不确定，已冻结。");
        var check = CheckDelivery();
        if (!check.CanDeliver) return Fail("需求未完全满足");

        var deliveryWorkshop = Order.Type == Constants.OrderType.A ? Master : Slave;
        var jarsToDeliver = new List<Jar>();
        var matched = JarMatcher.MatchAll(deliveryWorkshop.Jars, Order.Requirements, JarMatcher.Matches);
        foreach (var idx in matched)
            if (idx >= 0) jarsToDeliver.Add(deliveryWorkshop.Jars[idx]);

        if (Order.Type == Constants.OrderType.B)
        {
            // Delivery Jar removal is part of the same snapshot/ledger transaction as
            // returning the remaining Slave resources. A failed rollback must restore
            // both the delivered Jar and every resource that would otherwise return.
            RollbackSlaveToMaster(jarsToDeliver
                .Select(jar => jar.Id ?? 0)
                .ToArray());
        }
        else
        {
            foreach (var jar in jarsToDeliver)
                if (deliveryWorkshop.RemoveJar(jar) == null)
                    throw new InvalidOperationException($"订单交付罐 #{jar.Id} 无法从工作台移除");
        }

        _deliveredJars.Clear();
        _deliveredJars.AddRange(jarsToDeliver);

        Master.ClearMarks();
        Slave.ClearMarks();
        Order.MarkSucceeded();
        return (true, null);
    }

    public (bool Success, string? Reason) Fail(string? reason)
    {
        if (IsTransactionFaulted)
            return (false, TransactionFaultReason ?? "订单事务状态不确定，已冻结。");
        // Rollback captures the pre-failure Jar/mark state. Clear marks only after the
        // physical accounts and ledger batch commit; otherwise a failed rollback would
        // restore an already-cleared snapshot and lose the original execution state.
        RollbackSlaveToMaster();
        Master.ClearMarks();
        Slave.ClearMarks();
        Order.MarkFailed();
        return (false, reason);
    }

    /// <summary>
    /// 把从台剩余罐子与液体全部退回主台（主台接不下的直接丢弃）。
    /// 语义细节全部移入 <see cref="SlaveRollback"/>（全集排空 / 禁令边界 / 容量丢弃三件事一处说清）。
    /// </summary>
    public void RollbackSlaveToMaster(IReadOnlyCollection<int>? deliveredJarIds = null)
    {
        ThrowIfTransactionFaulted();
        try
        {
            _rollback.Execute(
                Master, Slave, AppendLog, _countDiscardedJars, _countDiscardedLiquids,
                _ledger, _tickOf,
                operationId: _ledger == null ? null :
                    "legacy-order:" + Order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    ":slave-rollback",
                deliveredJarIds: deliveredJarIds);
        }
        catch (SlaveRollbackTransactionException exception)
        {
            IsTransactionFaulted = true;
            TransactionFaultReason = exception.Message;
            throw;
        }
    }

    private void ThrowIfTransactionFaulted()
    {
        if (IsTransactionFaulted)
            throw new InvalidOperationException(
                TransactionFaultReason ?? "订单事务状态不确定，已冻结。");
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
            ["transactionFaulted"] = IsTransactionFaulted,
            ["transactionFaultReason"] = TransactionFaultReason,
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

using JarSystem.Core.Abstractions;
using JarSystem.Core.Commands;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Process;

/// <summary>订单执行的阶段（分步推进的状态机；顺序即执行顺序）。</summary>
internal enum OrderExecutionStage
{
    /// <summary>主台按计划步骤执行（A 类走 solverPath；B 类走 masterPath）。</summary>
    MasterPath,

    /// <summary>主台没有计划时用贪心兜底（一个批次单位）。</summary>
    MasterGreedy,

    /// <summary>选择性转移（器物 + 液体；一个批次单位）。</summary>
    Transfer,

    /// <summary>从台按计划步骤执行。</summary>
    SlavePath,

    /// <summary>从台没有计划（或路径失败降级）时用贪心兜底（一个批次单位）。</summary>
    SlaveGreedy,

    /// <summary>结束。</summary>
    Done
}

/// <summary>
/// **一次订单执行的分步会话**（P2b 的核心）：把原先"一口气跑完"的执行编排，
/// 改造成"一个单位一次推进"的可续跑状态机 —— 两条轨（同步 drain / 异步 tick 驱动）共用它。
///
/// 语义与改造前**逐条对齐**（这是等价性的来源）：
/// <list type="number">
/// <item>路径步骤：**失败也继续应用其余步骤**（与 <c>ExecuteViaSolverPath</c> 一致），最后按"有无错误 + 是否满足"判定；</item>
/// <item>A 类：路径成功即结束；失败则退回贪心，按 <c>IsSatisfiedBy(master)</c> 判成败；</item>
/// <item>B 类：主台路径任一步失败 ⇒ **立即失败**（"状态与可行性分析不一致"），不做转移；</item>
/// <item>B 类转移：选择性清单（无清单则全量）；批次失败 ⇒ 失败（"从台容量不足…"）；随后液体转移 + 切到从台阶段；</item>
/// <item>B 类从台：路径成功即结束；失败 ⇒ 记一条 fallback 并退回贪心；</item>
/// <item>贪心批次：一个单位跑完（P3 起才会细分成步骤）。</item>
/// </list>
/// </summary>
internal sealed class OrderExecutionSession
{
    private readonly Order _order;
    private readonly Workshop _master;
    private readonly Workshop _slave;
    private readonly OrderEvent _event;
    private readonly IProcessStepPrimitives _primitives;
    private readonly List<SolverStepDto> _masterSteps;
    private readonly List<SolverStepDto> _slaveSteps;
    private readonly HashSet<int> _directJarIds;
    private readonly List<ExecutionLogEntry> _ops = new();
    private readonly List<string> _errs = new();
    private int _masterIndex;
    private int _slaveIndex;
    private bool _masterPathErrored;

    /// <param name="masterSteps">主台计划（A 类 = solverPath；B 类 = masterPath；可为空）。</param>
    /// <param name="slaveSteps">从台计划（B 类的 solverPath；可为空）。</param>
    /// <param name="solverBasedMethod">B 类是否由求解器方案定义（决定"主台无事可做"时是否跑贪心）。</param>
    public OrderExecutionSession(Order order, Workshop master, Workshop slave, OrderEvent event_,
        IProcessStepPrimitives primitives, List<SolverStepDto> masterSteps, List<SolverStepDto> slaveSteps,
        bool solverBasedMethod, IProcessRecorder? recorder)
    {
        _order = order;
        _master = master;
        _slave = slave;
        _event = event_;
        _primitives = primitives;
        // MATCH 是交付预留与可行性匹配，不是实现动作。它不消耗过程单位，
        // 但对应罐子要进入守卫清单，防止执行期间被移走。
        _masterSteps = masterSteps.Where(step => !IsDirectMatch(step)).ToList();
        _slaveSteps = slaveSteps.Where(step => !IsDirectMatch(step)).ToList();
        _directJarIds = masterSteps
            .Concat(slaveSteps)
            .Where(IsDirectMatch)
            .Where(step => step.JarId.HasValue)
            .Select(step => step.JarId!.Value)
            .ToHashSet();
        _solverBasedMethod = solverBasedMethod;
        Recorder = recorder;
        Stage = InitialStage();
    }

    private readonly bool _solverBasedMethod;

    /// <summary>过程记账器（可为 null = 不记账）；收尾阶段（交付里程碑）也要用它。</summary>
    internal IProcessRecorder? Recorder { get; }

    /// <summary>本次执行的订单（内部：供收尾逻辑读取）。</summary>
    internal Order Order => _order;

    /// <summary>本次执行的事件对象（内部：供收尾逻辑读取）。</summary>
    internal OrderEvent Event => _event;

    /// <summary>主台（内部：供收尾逻辑读取）。</summary>
    internal Workshop Master => _master;

    /// <summary>从台（内部：供收尾逻辑读取；A 类时与主台相同）。</summary>
    internal Workshop Slave => _slave;

    /// <summary>当前阶段。</summary>
    public OrderExecutionStage Stage { get; private set; }

    /// <summary>执行是否成功（终态后有意义）。</summary>
    public bool Succeeded { get; private set; }

    /// <summary>失败原因（成功为 null）。</summary>
    public string? Reason { get; private set; }

    /// <summary>是否发生过跨台转移（对外报告的键）。</summary>
    public bool TransferOccurred => _event.TransferOccurred;

    /// <summary>操作日志（与既有 <c>Execute</c> 的返回一致）。</summary>
    public List<ExecutionLogEntry> Operations => _ops;

    /// <summary>错误清单（与既有 <c>Execute</c> 的返回一致）。</summary>
    public List<string> Errors => _errs;

    /// <summary>
    /// **守卫失败**：把会话标记为失败（供过程系统在"永久不成立"或"宽限耗尽"时调用）。
    /// 已完成的部分**不回滚**（与"失败要付代价"的设计定案一致）。
    /// </summary>
    internal void FailByGuard(string reason)
    {
        if (Stage == OrderExecutionStage.Done) return;
        Finish(false, reason);
    }

    /// <summary>
    /// 计划里**还要用到**的器物 id（守卫用它检查"器物是否还在工作台里"）：
    /// 剩余的计划步骤 + 尚未执行的转移清单 + 交付所需的已转移器物。
    /// </summary>
    internal IEnumerable<int> RemainingJarIds()
    {
        foreach (var id in _directJarIds) yield return id;
        for (int i = _masterIndex; i < _masterSteps.Count; i++)
            if (_masterSteps[i].JarId.HasValue) yield return _masterSteps[i].JarId!.Value;
        for (int i = _slaveIndex; i < _slaveSteps.Count; i++)
            if (_slaveSteps[i].JarId.HasValue) yield return _slaveSteps[i].JarId!.Value;
        if (Stage is OrderExecutionStage.Transfer or OrderExecutionStage.SlavePath or OrderExecutionStage.SlaveGreedy
            && _order.FeasibilityResult?.TransferJarIds is { } ids)
            foreach (var id in ids) yield return id;
    }

    /// <summary>
    /// 下一个单位的**目标**（守卫用）：操作名 + 形状 + 液体（任一可为 null）。
    ///
    /// "某一步被临时禁令 ban 掉"就是按这三者匹配的 —— 于是那种情形能在守卫里被识别为
    /// **可等待的阻断**（Blocked），而不是等执行到那一步时才失败；等禁令区间过去即恢复。
    /// </summary>
    internal (string? Operation, string? Shape, string? Liquid) NextUnitTarget()
    {
        switch (Stage)
        {
            case OrderExecutionStage.MasterPath when _masterIndex < _masterSteps.Count:
                return TargetOf(_masterSteps[_masterIndex]);
            case OrderExecutionStage.SlavePath when _slaveIndex < _slaveSteps.Count:
                return TargetOf(_slaveSteps[_slaveIndex]);
            case OrderExecutionStage.Transfer:
                return ("TRANSFER", null, null);
            case OrderExecutionStage.MasterGreedy:
            case OrderExecutionStage.SlaveGreedy:
                return ("GREEDY", null, null);
            default:
                return (null, null, null);
        }
    }

    /// <summary>把一个计划步骤投影成（操作 / 形状 / 液体）。改形步骤的"液体"取成本液体（那是它要消耗的）。</summary>
    private static (string? Operation, string? Shape, string? Liquid) TargetOf(SolverStepDto step) =>
        (step.Type, step.TargetShape, step.TargetLiquid ?? step.CostLiquid);

    /// <summary>
    /// 推进一个单位。返回 <c>true</c> 表示还有后续单位；<c>false</c> 表示本次推进把工作做完。
    /// </summary>
    public bool AdvanceOneUnit(IProcessRecorder? recorder)
    {
        switch (Stage)
        {
            case OrderExecutionStage.MasterPath:
                // 一步一单位：与既有实现一致——**失败也继续应用其余步骤**，最后统一判定。
                if (_masterIndex < _masterSteps.Count)
                {
                    // 阶段名沿用既有实现：A 类的计划就是"求解器路径"（solver_path）；
                    // B 类的主台计划叫 master_path（这个字面量**进对外契约** eventHistory[].operations[].phase）。
                    var phase = _order.Type == Constants.OrderType.A ? "solver_path" : "master_path";
                    var r = _primitives.ApplyPlannedStep(_master, _masterSteps[_masterIndex], phase, recorder);
                    if (r.Ok) _ops.Add(r.Entry!);
                    else { _errs.Add(r.Error ?? "步骤失败"); _masterPathErrored = true; }
                    _masterIndex++;
                }
                if (_masterIndex < _masterSteps.Count) return true;
                Stage = AfterMasterPath();
                return Stage != OrderExecutionStage.Done;

            case OrderExecutionStage.MasterGreedy:
            {
                var mr = _primitives.RunGreedyStage(_master, _order.Requirements, "master", _event, recorder);
                _ops.AddRange(mr.Ops);
                _errs.AddRange(mr.Errs);
                Stage = _order.Type == Constants.OrderType.A
                    ? (_order.IsSatisfiedBy(_master.Jars) ? FinishSuccess() : FinishFailure("主台资源不足"))
                    : OrderExecutionStage.Transfer;
                return Stage != OrderExecutionStage.Done;
            }

            case OrderExecutionStage.Transfer:
                return AdvanceTransfer(recorder);

            case OrderExecutionStage.SlavePath:
                if (_slaveIndex < _slaveSteps.Count)
                {
                    var r = _primitives.ApplyPlannedStep(_slave, _slaveSteps[_slaveIndex], "solver_path", recorder);
                    if (r.Ok) _ops.Add(r.Entry!);
                    else _errs.Add("步骤" + _slaveIndex + ": " + r.Error);
                    _slaveIndex++;
                }
                if (_slaveIndex < _slaveSteps.Count) return true;
                if (_errs.Count > 0)
                {
                    // 路径部分失败 ⇒ 与既有实现一致：记一条降级日志，退回贪心
                    _event.AppendLog(new ExecutionLogEntry
                    {
                        Phase = "fallback",
                        Action = "path_failed_fallback",
                        Description = "路径执行失败，使用默认策略"
                    });
                    recorder?.Milestone(ProcessMilestones.Fallback, "路径执行失败，使用默认策略");
                    Stage = OrderExecutionStage.SlaveGreedy;
                    return true;
                }
                Stage = _order.IsSatisfiedBy(_slave.Jars) ? FinishSuccess() : FinishFailure("从台资源不足");
                return Stage != OrderExecutionStage.Done;

            case OrderExecutionStage.SlaveGreedy:
                var sr = _primitives.RunGreedyStage(_slave, _order.Requirements, "slave", _event, recorder);
                _ops.AddRange(sr.Ops);
                _errs.AddRange(sr.Errs);
                Stage = _order.IsSatisfiedBy(_slave.Jars) ? FinishSuccess() : FinishFailure("从台资源不足");
                return false;

            default:
                return false;
        }
    }

    // ============================================================
    // 阶段推进
    // ============================================================

    /// <summary>起始阶段：有主台计划就走计划；否则 A 类与"非求解器方案"的 B 类走贪心，求解器方案的 B 类直接转移。</summary>
    private OrderExecutionStage InitialStage()
    {
        // 交付条件已经成立时没有“实现过程”：开始订单即原子交付。
        if (_order.Type == Constants.OrderType.A && _order.IsSatisfiedBy(_master.Jars))
            return FinishSuccess();
        if (_masterSteps.Count > 0) return OrderExecutionStage.MasterPath;
        if (_order.Type == Constants.OrderType.A) return OrderExecutionStage.MasterGreedy;
        return _solverBasedMethod ? OrderExecutionStage.Transfer : OrderExecutionStage.MasterGreedy;
    }

    /// <summary>主台计划走完之后的去向（与改造前的分支逐条对齐）。</summary>
    private OrderExecutionStage AfterMasterPath()
    {
        if (_order.Type == Constants.OrderType.A)
        {
            // A 类：计划成功且已满足 ⇒ 结束；否则退回贪心（已应用的步骤保留）
            if (!_masterPathErrored && _order.IsSatisfiedBy(_master.Jars)) return FinishSuccess();
            return OrderExecutionStage.MasterGreedy;
        }
        // B 类：主台路径任一步失败 ⇒ 立即失败，不做转移
        if (_masterPathErrored)
        {
            Finish(false, "主台路径执行失败，状态与可行性分析不一致");
            return OrderExecutionStage.Done;
        }
        return OrderExecutionStage.Transfer;
    }

    private bool AdvanceTransfer(IProcessRecorder? recorder)
    {
        List<Command> xfer;
        if (_order.FeasibilityResult?.TransferJarIds != null)
        {
            xfer = _order.FeasibilityResult.TransferJarIds
                .Select(id => _master.Jars.FirstOrDefault(j => j.Id == id))
                .Where(j => j != null)
                .Select(j => (Command)new TransferCommand(j!, _master, _slave))
                .ToList();
        }
        else
        {
            xfer = _master.Jars.Select(j => new TransferCommand(j, _master, _slave)).Cast<Command>().ToList();
        }

        var tr = _event.ExecuteBatch(xfer, _master);
        if (!tr.Success)
        {
            Finish(false, "从台容量不足，无法接收转移清单中的全部罐子");
            return false;
        }

        _ops.Add(new ExecutionLogEntry
        {
            Phase = "transfer",
            Action = "transfer",
            Description = $"转移 {xfer.Count} 个罐子与所需液体到从台"
        });
        // 多目标迁移是一个实现步骤；所有罐子和液体都是这一步的作用域。
        recorder?.StepApplied(ProcessMilestones.Transfer, null,
            $"主台 {_master.Name} → 从台 {_slave.Name}：转移 {xfer.Count} 个罐子与所需液体");

        var transferLiq = _order.FeasibilityResult?.TransferLiquids;
        foreach (var l in Constants.LIQUIDS)
        {
            var available = _master.LiquidStorage.GetValueOrDefault(l);
            var need = transferLiq != null ? transferLiq.GetValueOrDefault(l, 0) : available;
            if (need <= 0) continue;
            if (_slave.IsBanned(l)) continue;            // 从台禁液：留在主台，不转移
            var amt = Math.Min(need, available);
            if (amt <= 0) continue;
            var accepted = _slave.TryReceiveLiquid(l, amt);   // 容量感知的部分填充
            if (accepted <= 0)
            {
                _ops.Add(new ExecutionLogEntry
                {
                    Phase = "transfer",
                    Action = "transfer_liquid_failed",
                    Description = $"从台容量不足，{l}×{need} 留在主台"
                });
                continue;
            }
            _master.RemoveLiquid(l, accepted);
            recorder?.Milestone(ProcessMilestones.TransferLiquid, $"{l}×{accepted}");
            if (accepted < need)
                _ops.Add(new ExecutionLogEntry
                {
                    Phase = "transfer",
                    Action = "transfer_liquid_failed",
                    Description = $"从台容量不足，{l}×{need - accepted} 留在主台"
                });
        }

        _event.AdvanceToSlavePhase();
        _slave.ClearMarks();

        if (_slaveSteps.Count > 0)
        {
            Stage = OrderExecutionStage.SlavePath;
            return true;
        }

        // 迁移完成时若交付条件已经成立，不再为 MATCH 额外消耗一个过程单位。
        if (_order.IsSatisfiedBy(_slave.Jars))
        {
            Stage = FinishSuccess();
            return false;
        }

        Stage = OrderExecutionStage.SlaveGreedy;
        return true;
    }

    private static bool IsDirectMatch(SolverStepDto step) =>
        string.Equals(step.Type, "MATCH", StringComparison.Ordinal);

    private OrderExecutionStage FinishSuccess()
    {
        Finish(true, null);
        return OrderExecutionStage.Done;
    }

    private OrderExecutionStage FinishFailure(string reason)
    {
        Finish(false, reason);
        return OrderExecutionStage.Done;
    }

    private void Finish(bool success, string? reason)
    {
        Succeeded = success;
        Reason = reason;
        Stage = OrderExecutionStage.Done;
    }
}

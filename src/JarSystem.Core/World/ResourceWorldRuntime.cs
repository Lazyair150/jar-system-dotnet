using System;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 的持续运行时：先推进统一逻辑时间，再推进需求 CD、岗位替换与资源生产。
/// 替换活动期间继续生产，但关闭仓库到主台的流入。
/// </summary>
public sealed class ResourceWorldRuntime
{
    public WorldClock Clock { get; }
    public ResourceArea Resources { get; }
    public WorkshopRoster? Roster { get; }
    public OrderDemandBoard? DemandBoard { get; }
    public WorkshopReplacementProcess? ActiveReplacement { get; private set; }

    private readonly List<WorldOrderExecutionSession> _executionSessions = new();
    private readonly IReadOnlyList<WorldOrderExecutionSession> _executionSessionsView;
    /// <summary>当前仍在推进的订单执行会话（只读视图）。</summary>
    public IReadOnlyList<WorldOrderExecutionSession> ActiveExecutionSessions => _executionSessionsView;

    // 世界级资源服务由装配层提供，避免 Runtime 创建缺少账本/对象池依赖的迁徙事件。
    private readonly Func<Workshop, Workshop, WorkshopSwitchEvent>? _masterSwitchFactory;

    public ResourceWorldRuntime(
        ResourceArea resources,
        WorldClock? clock = null,
        WorkshopRoster? roster = null,
        Func<Workshop, Workshop, WorkshopSwitchEvent>? masterSwitchFactory = null,
        OrderDemandBoard? demandBoard = null)
    {
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        Clock = clock ?? new WorldClock();
        Roster = roster;
        _masterSwitchFactory = masterSwitchFactory;
        DemandBoard = demandBoard;
        _executionSessionsView = _executionSessions.AsReadOnly();
    }

    /// <summary>
    /// 将已完成准入的订单会话交给世界运行时推进。
    /// 会话使用本次 WorldClock 换算出的同一份逻辑时间，不另设时钟。
    /// </summary>
    public bool RegisterExecutionSession(WorldOrderExecutionSession session)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (session.IsTerminal) return false;
        if (_executionSessions.Any(x => x.DemandId == session.DemandId))
            throw new InvalidOperationException($"订单 {session.DemandId} 已有活动执行会话");
        _executionSessions.Add(session);
        return true;
    }

    /// <summary>基于已登记岗位推进世界。构造时未提供 Roster 则不能使用此重载。</summary>
    public ResourceWorldStep AdvanceWorldTime(long deltaWorldTimeMilli)
    {
        if (Roster == null)
            throw new InvalidOperationException("当前运行时没有工作台岗位集合");
        return AdvanceCore(deltaWorldTimeMilli, Roster.Master);
    }

    /// <summary>兼容第一阶段调用方式：显式指定本次资源流入目标；null 表示本次不向工作台流入。</summary>
    public ResourceWorldStep AdvanceWorldTime(long deltaWorldTimeMilli, Workshop? master) =>
        AdvanceCore(deltaWorldTimeMilli, master);

    /// <summary>请求主台或从台替换。同一时刻只允许一个岗位替换过程。</summary>
    public (bool Success, string? Error) BeginReplacement(
        WorkshopRole role,
        Workshop target,
        IOrderInterruptionPort interruptionPort,
        long phaseDurationMilli)
    {
        if (Roster == null)
            return (false, "当前运行时没有工作台岗位集合");
        if (ActiveReplacement is { IsFinished: false })
            return (false, "已有工作台替换过程正在进行");

        var process = new WorkshopReplacementProcess(
            Roster, role, target, interruptionPort, phaseDurationMilli, _masterSwitchFactory);
        var result = process.Begin();
        // 即使 Begin 失败，只要订单中断已提交或结果不确定，也保留终态过程供外部检查。
        if (result.Success || process.InterruptionCommitted || process.InterruptionOutcomeUncertain)
            ActiveReplacement = process;
        return result;
    }

    private ResourceWorldStep AdvanceCore(long deltaWorldTimeMilli, Workshop? requestedMaster)
    {
        var logicalDelta = Clock.Advance(deltaWorldTimeMilli);

        // CD 与资源生产使用同一份逻辑时间；暂停/零流速时 logicalDelta 为 0，CD 不减少。
        // 返回的到期需求 ID 暂由调用方忽略，阶段四将接入 Dirty/重新求解调度。
        DemandBoard?.AdvanceLogicalTime(logicalDelta);

        var replacementWasActive = ActiveReplacement is { IsFinished: false };
        if (replacementWasActive)
            ActiveReplacement!.AdvanceLogicalTime(logicalDelta);

        // 一个世界推进片段只要与替换过程有重叠，就全片段暂停流入。
        // 生产仍然推进并留在仓库；替换完成后的下一片段才向新主台流入。
        // 不在这里重新读取 Roster.Master：无参重载已在入口处解析岗位，
        // 显式参数重载则必须保留调用者指定的目标（包括 null）。
        var effectiveMaster = replacementWasActive ? null : requestedMaster;

        var resources = Resources.Advance(logicalDelta, effectiveMaster);

        // 执行会话与生产、CD 共用同一个逻辑时间增量。先推进本片段的生产，
        // 再推进已预留资源上的订单步骤；会话自身仍按步骤时长控制实际操作时点。
        foreach (var session in _executionSessions.ToArray())
        {
            try
            {
                session.AdvanceLogicalTime(logicalDelta);
            }
            finally
            {
                if (session.IsTerminal)
                    _executionSessions.Remove(session);
            }
        }

        return new ResourceWorldStep(logicalDelta, resources);
    }
}

/// <summary>一次世界推进的时间与资源结果。</summary>
public sealed class ResourceWorldStep
{
    public long LogicalTimeDeltaMilli { get; }
    public ResourceAreaAdvanceResult Resources { get; }

    internal ResourceWorldStep(long logicalTimeDeltaMilli, ResourceAreaAdvanceResult resources)
    {
        LogicalTimeDeltaMilli = logicalTimeDeltaMilli;
        Resources = resources;
    }
}

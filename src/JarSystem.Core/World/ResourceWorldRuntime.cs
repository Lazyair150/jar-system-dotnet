using JarSystem.Core.Domain;
using JarSystem.Core.Production;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 第一阶段的最小可运行世界，并提供第二阶段岗位替换的时间接入点。
/// 时间先换算为逻辑时间；活动替换过程推进期间继续生产，但关闭仓库到主台的流入。
/// </summary>
public sealed class ResourceWorldRuntime
{
    public WorldClock Clock { get; }
    public ResourceArea Resources { get; }
    public WorkshopRoster? Roster { get; }
    public WorkshopReplacementProcess? ActiveReplacement { get; private set; }

    public ResourceWorldRuntime(ResourceArea resources, WorldClock? clock = null, WorkshopRoster? roster = null)
    {
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        Clock = clock ?? new WorldClock();
        Roster = roster;
    }

    /// <summary>基于已登记岗位推进世界。构造时未提供 Roster 则不能使用此重载。</summary>
    public ResourceWorldStep AdvanceWorldTime(long deltaWorldTimeMilli)
    {
        if (Roster == null)
            throw new InvalidOperationException("当前运行时没有工作台岗位集合");
        return AdvanceCore(deltaWorldTimeMilli, Roster.Master);
    }

    /// <summary>兼容第一阶段调用方式：显式指定本次资源流入目标。</summary>
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
            Roster, role, target, interruptionPort, phaseDurationMilli);
        var result = process.Begin();
        if (result.Success)
            ActiveReplacement = process;
        return result;
    }

    private ResourceWorldStep AdvanceCore(long deltaWorldTimeMilli, Workshop? requestedMaster)
    {
        var logicalDelta = Clock.Advance(deltaWorldTimeMilli);
        var replacementWasActive = ActiveReplacement is { IsFinished: false };

        if (replacementWasActive)
            ActiveReplacement!.AdvanceLogicalTime(logicalDelta);

        // 一个世界推进片段只要与替换过程有重叠，就全片段暂停流入。
        // 生产仍然推进并留在仓库；替换完成后的下一片段才向新主台流入。
        var effectiveMaster = replacementWasActive
            ? null
            : (Roster != null ? Roster.Master : requestedMaster);

        var resources = Resources.Advance(logicalDelta, effectiveMaster);
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

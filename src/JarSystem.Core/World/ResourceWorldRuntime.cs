using JarSystem.Core.Domain;
using JarSystem.Core.Production;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 第一阶段的最小可运行世界。
///
/// 这里只组合时间、资源生产区和一个当前主工作台：
/// 没有订单、求解器、CD、从台或多工作台组合。
/// 世界时间先换算为逻辑时间，再由资源区完成生产与资源流入。
/// </summary>
public sealed class ResourceWorldRuntime
{
    public WorldClock Clock { get; }
    public ResourceArea Resources { get; }

    public ResourceWorldRuntime(ResourceArea resources, WorldClock? clock = null)
    {
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        Clock = clock ?? new WorldClock();
    }

    /// <summary>
    /// 推进世界时间。1000 表示 1 个世界时间单位。
    /// 主工作台可以为空；为空时资源只生产并积存在资源区。
    /// </summary>
    public ResourceWorldStep AdvanceWorldTime(long deltaWorldTimeMilli, Workshop? master)
    {
        var logicalDelta = Clock.Advance(deltaWorldTimeMilli);
        var resources = Resources.Advance(logicalDelta, master);
        return new ResourceWorldStep(logicalDelta, resources);
    }

    public void Reset() => Clock.Reset();
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

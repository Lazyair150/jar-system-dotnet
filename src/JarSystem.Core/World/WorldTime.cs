namespace JarSystem.Core.World;

/// <summary>
/// 世界时间的三种状态。
///
/// <para>
/// <see cref="NotStarted"/> 是**构造后、第一次推进之前**：此时 <c>Tick == 0</c>，
/// 世界还没有"发生过任何事"，过程日志里也不该出现 tick 0 的条目。
/// </para>
/// <para>
/// 一旦时间开始流动，<see cref="Running"/> 与 <see cref="Paused"/> 可以反复切换，
/// 但时钟只增不减 —— 暂停不是"把时间拨回去"，是"时间继续流动，只是世界不往前走"。
/// </para>
/// </summary>
public enum WorldTimeState
{
    /// <summary>世界尚未开始推进（tick = 0）。</summary>
    NotStarted,

    /// <summary>时间在流动：每次推进 tick 加一，过程与产出照常。</summary>
    Running,

    /// <summary>时间被冻住：推进请求一律不生效，tick 不变，过程宽限期也不流失。</summary>
    Paused
}

/// <summary>
/// **世界时间的唯一门面**：世界"现在几点、在不在走"的唯一权威。
///
/// <para>
/// 存在理由：tick 是这套系统的逻辑时间轴（替代实时游戏里的帧循环），
/// 而"时间"原本散落在四处 —— 逻辑时钟（tick）、产线累加器（产出节拍）、
/// 过程实例（截止/宽限/下一步）、修饰器栈（时效区间）。
/// 这四处**读数必须一致**，所以需要一个能被统一查询、统一断言的入口。
/// </para>
/// <para>
/// **本类不推进时钟**。推进只有一个入口：<see cref="Process.LogicalClock.Advance"/>，
/// 且只由 <see cref="Process.TickPipeline.AdvanceOneTick"/> 调用。
/// 本类只回答"世界时间现在的状态"，并守住两条不变式：
/// </para>
/// <list type="number">
/// <item>tick 只增（由时钟保证），因此"某 tick 距今多久"恒非负；</item>
/// <item>暂停不改 tick —— 于是"宽限期/截止期按 tick 计"这件事在暂停期间天然不流失，
///       不需要任何额外补偿逻辑（补偿就是把时钟拨回去，那是禁止的）。</item>
/// </list>
/// </summary>
public sealed class WorldTime
{
    private readonly Process.LogicalClock _clock;

    internal WorldTime(Process.LogicalClock clock)
    {
        _clock = clock;
    }

    /// <summary>当前 tick（0 = 世界尚未开始推进）。</summary>
    public int Tick => _clock.Tick;

    /// <summary>世界时间的状态。</summary>
    public WorldTimeState State { get; private set; } = WorldTimeState.NotStarted;

    /// <summary>时间是否在流动（<see cref="WorldTimeState.Running"/>）。</summary>
    public bool IsFlowing => State == WorldTimeState.Running;

    /// <summary>时间被冻在哪个 tick 上（未暂停时等于 <see cref="Tick"/>）。</summary>
    public int FrozenAtTick { get; private set; }

    /// <summary>从 <paramref name="tick"/> 到现在过了多少个 tick（含边界校验，拒绝未来）。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tick"/> 为负或大于当前 tick。</exception>
    public int ElapsedSince(int tick)
    {
        if (tick < 0)
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "tick 不能为负");
        if (tick > Tick)
            throw new ArgumentOutOfRangeException(nameof(tick), tick,
                $"不能计算尚未发生的 tick（当前是 {Tick}）");
        return Tick - tick;
    }

    /// <summary>时钟推进一 tick 之后由 <see cref="Process.TickPipeline"/> 回调。</summary>
    internal void OnAdvanced()
    {
        if (State == WorldTimeState.NotStarted)
            State = WorldTimeState.Running;
    }

    /// <summary>冻住时间。已经冻住时返回 false（幂等）。</summary>
    internal bool Pause()
    {
        if (State == WorldTimeState.Paused)
            return false;
        FrozenAtTick = _clock.Tick;
        State = WorldTimeState.Paused;
        return true;
    }

    /// <summary>恢复流动。本来就在流动时返回 false（幂等）。</summary>
    internal bool Resume()
    {
        if (State != WorldTimeState.Paused)
            return false;
        State = WorldTimeState.Running;
        return true;
    }

    /// <summary>世界重建时回到"尚未开始"（只由 <see cref="Process.ProcessSystem.Clear"/> 链路触发）。</summary>
    internal void Reset()
    {
        State = WorldTimeState.NotStarted;
        FrozenAtTick = 0;
    }
}

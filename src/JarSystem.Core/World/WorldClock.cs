namespace JarSystem.Core.World;

/// <summary>
/// 连续世界时间到逻辑时间的唯一换算器。
///
/// 输入与输出均采用千分之一单位：1000 = 1 个时间单位。
/// 世界流速也采用千分之一倍率：1000 = 1 倍。
/// 使用整数余数保存除法尾数，避免将 delta 分段传入时产生浮点漂移。
///
/// 本类型不推进旧 ProcessSystem 的整数 tick；它是 World v2 的时间轴，
/// 在第一阶段通过独立运行时使用，避免改变旧世界回放与 tick 契约。
/// </summary>
public sealed class WorldClock
{
    private const long RatioUnit = 1000;

    private long _conversionRemainder;

    public long WorldTimeMilli { get; private set; }
    public long LogicalTimeMilli { get; private set; }
    public long SpeedRatioMilli { get; private set; } = RatioUnit;
    public bool IsPaused { get; private set; }

    /// <summary>世界流速倍率。必须是有限非负数；零表示时间停止推进。</summary>
    public void SetSpeedRatio(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio < 0)
            throw new ArgumentOutOfRangeException(nameof(ratio), "世界流速必须是有限非负数");

        var scaled = Math.Round(ratio * RatioUnit, MidpointRounding.AwayFromZero);
        if (scaled > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ratio), "世界流速超出可表示范围");

        SpeedRatioMilli = (long)scaled;
    }

    public void Pause() => IsPaused = true;

    public void Resume() => IsPaused = false;

    /// <summary>
    /// 推进一段世界时间，返回本次产生的逻辑时间增量。
    /// deltaWorldTimeMilli 的 1000 表示 1 个世界时间单位。
    /// 暂停时不累计世界时间，也不累计逻辑时间。
    /// </summary>
    public long Advance(long deltaWorldTimeMilli)
    {
        if (deltaWorldTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaWorldTimeMilli));
        if (IsPaused || deltaWorldTimeMilli == 0 || SpeedRatioMilli == 0)
            return 0;

        WorldTimeMilli = checked(WorldTimeMilli + deltaWorldTimeMilli);

        // (世界时间千分之一 × 倍率千分之一) / 1000
        // 结果仍为逻辑时间千分之一；保留不足一个输出单位的余数。
        var numerator = checked(deltaWorldTimeMilli * SpeedRatioMilli + _conversionRemainder);
        var logicalDelta = numerator / RatioUnit;
        _conversionRemainder = numerator % RatioUnit;

        LogicalTimeMilli = checked(LogicalTimeMilli + logicalDelta);
        return logicalDelta;
    }

    /// <summary>清零时间与换算余数，保留当前流速设置。</summary>
    public void Reset()
    {
        WorldTimeMilli = 0;
        LogicalTimeMilli = 0;
        _conversionRemainder = 0;
        IsPaused = false;
    }
}

using System;
using System.Collections.Generic;
using JarSystem.Core;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 逻辑时间的契约测试。
///
/// 这套系统用 **tick 代替帧循环**当时间轴：世界不是"每帧重算"，而是"每次推进走一步"。
/// 于是有一批承诺必须被钉死，否则"活在时间线上"这件事只是说法：
/// <list type="number">
/// <item>时间有开始（tick 0 = 尚未开始），且**只增不减**；</item>
/// <item>时间能被冻住，冻住期间推进请求**完全**不生效 —— 时钟不动、产出不动、**日志也不动**；
///       最后一条是回放能保持一致的全部依据；</item>
/// <item>冻住与解冻是**可观察的**（发信号、幂等）。</item>
/// </list>
/// </summary>
public class WorldTimeTests
{
    private sealed class RecordingObserver : IWorldObserver
    {
        public List<WorldSignal> Seen { get; } = new();
        public void OnSignal(in WorldSignal signal) => Seen.Add(signal);
    }

    [Fact]
    public void Time_StartsNotStarted()
    {
        var sys = new JarSystem.Core.JarSystem();

        Assert.Equal(WorldTimeState.NotStarted, sys.Time.State);
        Assert.Equal(0, sys.Time.Tick);
        Assert.False(sys.Time.IsFlowing);
        Assert.False(sys.IsTimePaused);
    }

    [Fact]
    public void Time_FirstAdvanceTurnsItRunning_AndTickOnlyGrows()
    {
        var sys = new JarSystem.Core.JarSystem();

        sys.Tick(1);
        Assert.Equal(WorldTimeState.Running, sys.Time.State);
        Assert.Equal(1, sys.Time.Tick);

        sys.Tick(3);
        Assert.Equal(4, sys.Time.Tick);

        // 只增：任何操作都不会把 tick 拨回去（Reset 是"世界重建"，不是"时间倒流"的特例）
        Assert.Equal(0, sys.Time.ElapsedSince(4));
        Assert.Equal(3, sys.Time.ElapsedSince(1));
    }

    [Fact]
    public void Time_PausedWorldDoesNotAdvance()
    {
        var sys = new JarSystem.Core.JarSystem();
        sys.Tick(2);
        Assert.Equal(2, sys.Time.Tick);

        Assert.True(sys.PauseTime());
        Assert.True(sys.IsTimePaused);
        Assert.Equal(2, sys.Time.FrozenAtTick);

        // 冻结期间推进请求一律不生效 —— 不是"少走几步"，是一步都不走
        sys.Tick(50);
        Assert.Equal(2, sys.Time.Tick);
        Assert.Equal(WorldTimeState.Paused, sys.Time.State);

        Assert.True(sys.ResumeTime());
        sys.Tick(1);
        Assert.Equal(3, sys.Time.Tick);
        Assert.Equal(WorldTimeState.Running, sys.Time.State);
    }

    [Fact]
    public void Time_PauseAndResumeAreIdempotentAndObservable()
    {
        var sys = new JarSystem.Core.JarSystem();
        var obs = new RecordingObserver();
        sys.Bus.Subscribe(obs);

        Assert.True(sys.PauseTime());
        Assert.False(sys.PauseTime());   // 已冻住 ⇒ 不重复发信号
        Assert.True(sys.ResumeTime());
        Assert.False(sys.ResumeTime());  // 本来就在走 ⇒ 不重复发信号

        Assert.Equal(2, obs.Seen.Count);
        Assert.Equal(WorldSignalKind.TimePaused, obs.Seen[0].Kind);
        Assert.Equal(WorldSignalKind.TimeResumed, obs.Seen[1].Kind);
    }

    [Fact]
    public void Time_PausedWorldWritesNoCommandLog()
    {
        // 这条是**回放安全**的机器化证明：冻结期间日志不增长，
        // 于是同一份命令日志在"停过"和"没停过"的两个世界里产生同一个世界。
        var sys = new JarSystem.Core.JarSystem();
        sys.Tick(1);

        var before = sys.CommandLog.Count;
        sys.PauseTime();
        sys.Tick(10);
        sys.Tick(10);
        Assert.Equal(before, sys.CommandLog.Count);

        sys.ResumeTime();
        sys.Tick(1);
        Assert.Equal(before + 1, sys.CommandLog.Count);
    }

    [Fact]
    public void Time_ElapsedSince_RejectsNegativeAndFuture()
    {
        var sys = new JarSystem.Core.JarSystem();
        sys.Tick(3);

        Assert.Throws<ArgumentOutOfRangeException>(() => sys.Time.ElapsedSince(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => sys.Time.ElapsedSince(4));
    }

    [Fact]
    public void Tick_RejectsNegativeCount()
    {
        // 负数 tick 曾经会静默写一条 `tick` 命令（intA = -1），凭空污染命令日志。
        var sys = new JarSystem.Core.JarSystem();
        var before = sys.CommandLog.Count;

        Assert.Throws<ArgumentOutOfRangeException>(() => sys.Tick(-1));
        Assert.Equal(before, sys.CommandLog.Count);
        Assert.Equal(0, sys.Time.Tick);
    }

    [Fact]
    public void Time_ResetReturnsToNotStarted()
    {
        var sys = new JarSystem.Core.JarSystem();
        sys.Tick(5);
        sys.PauseTime();

        sys.Reset();

        Assert.Equal(WorldTimeState.NotStarted, sys.Time.State);
        Assert.Equal(0, sys.Time.Tick);
    }
}

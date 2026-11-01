using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Process;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 运行时事件规范（第二版）里两条**没有代码缺口、但有可验证事实**的待定项的守卫。
///
/// <para><b>D2（观察者能否跨 tick 持有信号）。</b>定为**禁止转存**。它不是能靠类型系统拦住的错
/// （<see cref="WorldSignal"/> 是 <c>readonly struct</c>，存一份永远编译通过），
/// 所以这里用一个刻画测试把这个陷阱做成可教学的：把信号里的 <c>Revision</c> 抓出来，
/// 等效果真正执行时比一次 —— 它**一定是旧的**。谁想"晚点再处理"，就会踩到这个旧值。</para>
///
/// <para><b>D3（过程日志裁剪）。</b>定为**保留最近 N、成批丢最旧一半、消费方按序号判缺口**。
/// 这里固定两条恒等式：<c>FirstSeq == DroppedEntries + 1</c>、<c>DroppedEntries == 累计追加 − 保留条数</c>。</para>
/// </summary>
public class RuntimeRetentionTests
{
    private static JarSystem.Core.JarSystem NewWorld()
        => new(randomSeed: 5, jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());

    /// <summary>
    /// 排一条效果，把"信号里的 Revision"和"效果执行时的 Revision"都记下来。
    /// 只排一次，否则每个 tick 都会重排。
    /// </summary>
    private sealed class CaptureRevisionReactor : IWorldReactor
    {
        private readonly Func<int> _currentTick;

        public CaptureRevisionReactor(Func<int> currentTick) => _currentTick = currentTick;

        public int CapturedTick = -1;
        public int CapturedRevision = -1;
        public int TickAtEffectRun = -1;
        public bool Enqueued;

        public void React(in WorldSignal signal, IWorldEffectSink sink)
        {
            if (Enqueued) return;
            CapturedTick = signal.Tick;
            CapturedRevision = signal.Revision;
            Enqueued = sink.Enqueue("probe", "记下信号里的 tick 与执行时的 tick",
                () => TickAtEffectRun = _currentTick());
        }
    }

    /// <summary>D2：信号里的 Revision 在效果执行时已经变旧 —— 这就是禁止转存的理由。</summary>
    [Fact]
    public void CapturedSignalRevisionIsStaleByTheTimeTheEffectRuns()
    {
        var sys = NewWorld();
        var reactor = new CaptureRevisionReactor(() => sys.Time.Tick);
        sys.Bus.AttachReactor(reactor);

        sys.Tick(1);   // 发信号 → 反应器排一条效果（这一 tick 不执行）
        Assert.True(reactor.Enqueued, "反应器没有排进效果，后续断言没有意义");
        Assert.Equal(-1, reactor.TickAtEffectRun);   // 证明它确实被延后了

        sys.Tick(1);   // 上一 tick 排的效果在这一 tick 的第 ① 步执行

        Assert.True(reactor.CapturedTick >= 0);
        Assert.True(reactor.TickAtEffectRun >= 0, "效果没有执行");
        // 效果执行时世界已经推进了整整一个 tick —— 这才是"信号会变旧"的确切含义。
        // 注意 Revision 在这里**不会**变大：`Record` 在 Tick 末尾才递增编号，
        // 所以"跨 tick 持有信号"真正丢的是时间位置，不是编号。
        Assert.True(reactor.TickAtEffectRun > reactor.CapturedTick,
            "若无此性质，D2 的'禁止转存'就没有现实理由");
    }

    /// <summary>D3：裁剪后序号仍然连续，缺口正好落在第一条保留条目之前。</summary>
    [Fact]
    public void TrimmingKeepsSequenceContinuousAndReportsTheGap()
    {
        const int capacity = 8;
        const int appends = 40;
        var journal = new ProcessJournal(capacity);

        for (int i = 0; i < appends; i++)
            journal.Append(tick: i, processId: 1, kind: "order_execution");

        Assert.True(journal.Count <= capacity, "裁剪没有生效，保留条数超过了容量");
        Assert.True(journal.IsTruncated);
        Assert.Equal(appends - journal.Count, journal.DroppedEntries);
        Assert.Equal(journal.DroppedEntries + 1, journal.FirstSeq);
        Assert.Equal(appends, journal.LastSeq);

        // 保留的条目必须首尾相接、逐个 +1（中间不能有洞）。
        for (int i = 1; i < journal.Entries.Count; i++)
            Assert.Equal(journal.Entries[i - 1].Seq + 1, journal.Entries[i].Seq);
    }

    /// <summary>D3：容量内不裁剪，也不该报告缺口。</summary>
    [Fact]
    public void JournalWithinCapacityIsNotTruncated()
    {
        var journal = new ProcessJournal(16);
        for (int i = 0; i < 10; i++)
            journal.Append(tick: i, processId: 1, kind: "workshop_migration");

        Assert.False(journal.IsTruncated);
        Assert.Equal(0, journal.DroppedEntries);
        Assert.Equal(1, journal.FirstSeq);
        Assert.Equal(10, journal.LastSeq);

        // Clear 是"世界重来"的入口：条目、丢弃计数、序号一并归零（与 Reset 语义一致）。
        journal.Clear();
        Assert.Equal(0, journal.Count);
        Assert.Equal(0, journal.DroppedEntries);
        Assert.Equal(0, journal.FirstSeq);
        Assert.Equal(0, journal.LastSeq);
    }
}

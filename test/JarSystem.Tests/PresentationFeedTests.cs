using System;
using System.Collections.Generic;
using System.Text.Json;
using JarSystem.Core;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Presentation;
using JarSystem.Core.Services;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **表现层帧流**的契约测试（2026-11 审计的落地）。
///
/// 这批测试守的不是画面，而是核心对外的四条承诺：
/// <list type="number">
/// <item>**有顺序**：帧号单调、tick 不回头 —— 表现层因此不需要自己猜先后；</item>
/// <item>**有缺口可查**：被裁剪掉的帧数与首帧号满足恒等式，"没收到"不会被读成"没发生"；</item>
/// <item>**是投影不是真相**：挂不挂接收方，世界的最终状态逐字节相同；</item>
/// <item>**节奏在宿主**：核心只提供游标读取，从不睡眠、不排定时器。</item>
/// </list>
/// </summary>
public class PresentationFeedTests
{
    private sealed class RecordingSink : IPresentationSink
    {
        public List<PresentationFrame> Seen { get; } = new();
        public void OnFrame(in PresentationFrame frame) => Seen.Add(frame);
    }

    [Fact]
    public void EveryWorldChangedFrameMatchesOneCommandLogEntry()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);
        system.CreateOrder(new List<Requirement> { new() { Shape = "方罐" } }, Constants.OrderType.A);

        // 逐条对上命令日志：**一帧一意图、顺序一致** —— 这才是"帧流是世界日志的投影"的可检验形式。
        var frameOps = new List<string>();
        foreach (var f in system.Presentation.Frames)
            if (f.Kind == PresentationKind.WorldChanged) frameOps.Add(f.Op);

        var logOps = new List<string>();
        foreach (var c in system.CommandLog) logOps.Add(c.Op);

        Assert.Equal(logOps, frameOps);
        Assert.Equal(system.CommandLog.Count, frameOps.Count);   // 逐个意图一帧，不多不少
    }

    [Fact]
    public void FramesAreSequencedAndTicksNeverGoBack()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);
        system.StartOrder(0);
        system.Tick(3);

        var frames = system.Presentation.Frames;
        Assert.True(frames.Count > 3, "跑完这段脚本应该产生多帧，实际 " + frames.Count);

        // 帧号连续、从 1 开始（未裁剪时）
        Assert.Equal(1, frames[0].Seq);
        for (int i = 1; i < frames.Count; i++)
        {
            Assert.Equal(frames[i - 1].Seq + 1, frames[i].Seq);
            Assert.True(frames[i].Tick >= frames[i - 1].Tick,
                $"第 {frames[i].Seq} 帧的 tick 回退了：{frames[i - 1].Tick} → {frames[i].Tick}");
        }
    }

    [Fact]
    public void ProcessFramesCarryReasonAndSubject()
    {
        var system = NewSystem();
        system.SetMasterIndex(0);
        system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);
        system.StartOrder(0);
        system.Tick(3);

        bool sawStarted = false;
        foreach (var f in system.Presentation.Frames)
            if (f.Kind == PresentationKind.ProcessChanged && f.Op == "started") sawStarted = true;

        Assert.True(sawStarted, "订单开始执行必须留下一条 ProcessChanged:started 帧");
    }

    [Fact]
    public void PausingTimeAppearsAsItsOwnFrame()
    {
        var system = NewSystem();
        system.Tick(1);
        system.PauseTime();

        var last = system.Presentation.Frames[^1];
        Assert.Equal(PresentationKind.TimePaused, last.Kind);
    }

    [Fact]
    public void BufferIsBoundedAndReportsTheGap()
    {
        // 直接测帧流本身（容量可显式声明）—— 系统里的那条用默认容量，跑不满。
        var feed = new PresentationFeed(capacity: 8);
        for (int i = 0; i < 40; i++)
            feed.Append(PresentationKind.WorldChanged, "op" + i, null, null, i, i);

        Assert.True(feed.IsTruncated);
        Assert.Equal(8, feed.Count);
        // 恒等式：首帧号 = 被丢弃帧数 + 1 ⇒ 消费方能算出"我手上这段之前的缺口有多大"
        Assert.Equal(feed.DroppedFrames + 1, feed.FirstSeq);
        Assert.Equal(40, feed.LastSeq);
        // 保留的 8 帧首尾相接、无洞
        for (int i = 1; i < feed.Count; i++) Assert.Equal(feed.Frames[i - 1].Seq + 1, feed.Frames[i].Seq);
    }

    [Fact]
    public void SinceReturnsOnlyFramesNewerThanTheCursor()
    {
        var feed = new PresentationFeed(capacity: 0);   // 0 = 不限制（测试显式声明）
        var sink = new RecordingSink();
        feed.Subscribe(sink);
        for (int i = 0; i < 5; i++) feed.Append(PresentationKind.WorldChanged, "op", null, null, i, i);

        Assert.Equal(5, sink.Seen.Count);        // 接收方同步收到全部
        Assert.Empty(feed.Since(5));             // 已经播到最后一帧
        var fresh = feed.Since(3);               // 只取更新的两帧
        Assert.Equal(2, fresh.Count);
        Assert.Equal(4, fresh[0].Seq);
        Assert.Equal(5, fresh[1].Seq);
    }

    [Fact]
    public void ReadingADroppedFrameIsAFactNotAnException()
    {
        var feed = new PresentationFeed(capacity: 4);
        for (int i = 0; i < 20; i++) feed.Append(PresentationKind.WorldChanged, "op", null, null, i, i);

        Assert.False(feed.TryGet(1, out _));     // 第 1 帧已被裁掉
        Assert.True(feed.TryGet(feed.LastSeq, out var last));
        Assert.Equal(feed.LastSeq, last.Seq);
    }

    [Fact]
    public void ResetRestartsTheFeedButKeepsSinksConnected()
    {
        var system = NewSystem();
        var sink = new RecordingSink();
        system.Presentation.Subscribe(sink);

        system.Tick(1);
        Assert.NotEmpty(sink.Seen);

        system.Reset();
        // 帧流是旧世界的投影，清空；但 Reset 自己也是一次意图，于是紧接着留下**一帧** `reset`
        // （顺序是刻意的：先清空、再记账 —— 与 CommandLog 的做法一致）。
        Assert.Equal(1, system.Presentation.Count);
        Assert.Equal(1, system.Presentation.FirstSeq);
        Assert.Equal("reset", system.Presentation.Frames[0].Op);
        Assert.Equal(0, system.Presentation.DroppedFrames);

        int before = sink.Seen.Count;
        system.Tick(1);
        Assert.True(sink.Seen.Count > before, "重来之后接收方不应该掉线");
        Assert.Equal(1, system.Presentation.FirstSeq); // 帧号从头开始
    }

    [Fact]
    public void PresentationDoesNotChangeTheWorldOutcome()
    {
        // 与 WorldBusTests.Subscribers_DoNotChangeWorldOutcome 同一手法：
        // 唯一变量是"有没有人订阅帧流"，其余（种子、ID 源、命令序列）逐字相同。
        string Run(bool withSink)
        {
            var system = NewSystem();
            if (withSink) system.Presentation.Subscribe(new RecordingSink());

            system.SetMasterIndex(0);
            system.CreateOrder(new List<Requirement> { new() { Shape = "圆罐" } }, Constants.OrderType.A);
            system.StartOrder(0);
            system.Tick(3);
            system.CheckFeasibility(0);

            return System.Text.RegularExpressions.Regex.Replace(
                       JsonSerializer.Serialize(system.GetState()),
                       "\"timestamp\":\"[^\"]*\"",
                       "\"timestamp\":\"<volatile>\"")
                   + "|rev=" + system.WorldRevision
                   + "|tick=" + system.Processes.Clock.Tick;
        }

        Assert.Equal(Run(false), Run(true));
    }

    private static JarSystem.Core.JarSystem NewSystem() => new(
        randomSeed: 17,
        jarIds: new SequentialIdGenerator(),
        orderIds: new SequentialIdGenerator());
}

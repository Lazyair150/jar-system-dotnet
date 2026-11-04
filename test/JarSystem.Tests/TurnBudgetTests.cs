using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 回合上限（关卡规则）：一关最多推进几回合，到了就**硬停并判失败**。
///
/// 为什么这条规则必须住内核（而不是页面）：
/// 页面上的"推进"按钮只是转发命令 —— 别的客户端、脚本、回放都能直接把 <c>Tick</c> 发过来；
/// 只要判定在页面，绕过页面就等于绕过规则。同理，它是"玩家真正对抗的东西"，
/// 必须与"推了多少"同处一地，否则界面说没回合了、世界照走。
///
/// 钉住四件事：
/// 1 没上限（`turnLimit = 0`）的世界可以随便推 —— 出厂沙盒不该被关卡规则约束；
/// 2 上限 N ⇒ 正好能推 N 回合，第 N+1 回合被拒；
/// 3 一次推多回合时判定是**整体**的：只剩 1 回合时 `Tick(3)` 整个世界一动不动；
/// 4 被拒的那次推进**不留痕迹**（时钟、世界修订号、状态都不变）。
/// </summary>
public class TurnBudgetTests
{
    private static global::JarSystem.Core.JarSystem NewSystem(int turnLimit, int maxJars = 6)
    {
        // 单台、单线的小世界：这一组用例只关心"能推几回合"，与玩法无关。
        var world = WorldOptions.Default;
        world.DefaultMaxJars = maxJars;
        world.Workshops = new List<WorkshopOptions>
        {
            new() { Name = "主台", BaseMarks = new List<string> { "圆罐", "方罐" } }
        };
        world.TurnLimit = turnLimit;
        var report = world.Validate();
        Assert.True(report.IsValid, "用例前提：世界必须合法 —— " +
            string.Join("；", report.Errors));
        return new global::JarSystem.Core.JarSystem(randomSeed: 7, world: world);
    }

    [Fact]
    public void AnUnlimitedWorldCanAdvanceFreely()
    {
        var sys = NewSystem(turnLimit: 0);
        Assert.False(sys.Turns.IsLimited);
        Assert.Equal(int.MaxValue, sys.Turns.Remaining);
        Assert.True(sys.Turns.Allows(1000));

        sys.Tick(50);
        Assert.Equal(50, sys.Turns.Used);
        Assert.False(sys.Turns.IsExhausted);
    }

    [Fact]
    public void TheLimitBoundsTheTicksExactly()
    {
        var sys = NewSystem(turnLimit: 3);

        sys.Tick();                                   // 1
        sys.Tick();                                   // 2
        Assert.False(sys.Turns.IsExhausted);
        sys.Tick();                                   // 3 = 上限
        Assert.True(sys.Turns.IsExhausted);
        Assert.Equal(0, sys.Turns.Remaining);

        var ex = Assert.Throws<DomainValidationException>(() => sys.Tick());   // 第 4 回合
        Assert.Contains("回合已用完", ex.Message);
        Assert.Contains("3", ex.Message);
        Assert.Equal(3, sys.Turns.Used);              // 被拒的那次没有推进
    }

    [Fact]
    public void ABatchIsRefusedAsAWholeInsteadOfPartiallyApplied()
    {
        var sys = NewSystem(turnLimit: 3);
        sys.Tick(2);                                  // 还剩 1 回合

        var beforeTick = sys.Processes.Clock.Tick;
        var beforeRevision = sys.WorldRevision;

        Assert.Throws<DomainValidationException>(() => sys.Tick(3));

        // 走一半的世界最糟：玩家会以为推进成功了，世界却停在规则上不该到达的位置。
        Assert.Equal(beforeTick, sys.Processes.Clock.Tick);
        Assert.Equal(beforeRevision, sys.WorldRevision);
        Assert.Equal(2, sys.Turns.Used);
    }

    [Fact]
    public void TheStateCarriesTheBudgetForTheClient()
    {
        var sys = NewSystem(turnLimit: 3);
        sys.Tick(2);

        var st = sys.GetState();
        Assert.Equal(2, st.Turns.Used);
        Assert.Equal(3, st.Turns.Limit);
        Assert.False(st.Turns.Exhausted);

        sys.Tick();
        var done = sys.GetState();
        Assert.True(done.Turns.Exhausted);
        Assert.Equal(3, done.Turns.Used);
    }

    [Fact]
    public void ThePureValueAndTheLiveSystemAgree()
    {
        // 规则有唯一一份说法：`TurnBudget` 是判据，`JarSystem.Tick` 是执行者。
        // 这里对同一组 (上限, 已用, 请求) 逐格比对 —— 行为一旦分家，这条就红。
        var samples = new List<(int Limit, int Used, int Count)>();
        foreach (var limit in new[] { 0, 1, 3 })
        foreach (var used in new[] { 0, 1, 2, 3, 4 })
        foreach (var count in new[] { 0, 1, 2, 5 })
            samples.Add((limit, used, count));

        Assert.True(samples.Count >= 60, "对拍样本太少：" + samples.Count);

        foreach (var (limit, used, count) in samples)
        {
            // 活系统只能到"上限以内"去准备状态：used 超过 limit 的样本本来就推不出来，
            // 拿 start 去比 —— 这样纯值与执行者比较的仍是同一组真实可达状态。
            var start = limit > 0 ? Math.Min(used, limit) : used;
            var budget = new TurnBudget(limit, start);
            var sys = NewSystem(turnLimit: limit);
            if (start > 0) sys.Tick(start);
            Assert.Equal(start, sys.Turns.Used);

            var executed = true;
            try { sys.Tick(count); }
            catch (DomainValidationException) { executed = false; }

            Assert.True(budget.Allows(count) == executed,
                $"上限 {limit}／已用 {start}／请求 {count}：纯值说 " +
                (budget.Allows(count) ? "能" : "不能") + "，执行说 " +
                (executed ? "能" : "不能"));
            if (executed) Assert.Equal(start + count, sys.Turns.Used);
        }
    }

    [Fact]
    public void ANegativeLimitIsRefusedByTheWorldConfig()
    {
        var world = WorldOptions.Default;
        world.TurnLimit = -1;
        Assert.False(world.Validate().IsValid);
    }

    [Fact]
    public void HugeUsedCountCannotOverflowIntoAnotherAllowedTurn()
    {
        var budget = new TurnBudget(limit: int.MaxValue, used: int.MaxValue);

        Assert.False(budget.Allows(1));
    }

    [Fact]
    public void TickZeroDoesNotCreateAResourceFlowWithoutTimeAdvancing()
    {
        var sys = NewSystem(turnLimit: 0);
        sys.SetMasterIndex(0);
        var revision = sys.WorldRevision;

        sys.Tick(0);

        Assert.Equal(0, sys.Turns.Used);
        Assert.Equal(revision, sys.WorldRevision);
        Assert.All(sys.Workshops[0].LiquidStorage.Values, amount => Assert.Equal(0, amount));
    }
}

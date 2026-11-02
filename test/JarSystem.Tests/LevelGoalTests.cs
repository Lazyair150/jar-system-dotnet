using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using JarSystem.Core;
using JarSystem.Core.Config;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 判胜（关卡的目标规则）：做成 N 单算赢，回合用尽还没做成算输。
///
/// 为什么这条规则必须住内核（而不是页面）：
/// 页面上的"目标 1/1 · 本关通过"只是把结论显示出来 —— 别的客户端、脚本、回放都能直接读状态，
/// 判定一旦落在页面，不同客户端就会对"我赢了吗"各有一套说法。判据只有一处
/// （<see cref="LevelGoal.Judge"/>），页面只显示。
///
/// 钉住五件事：
/// 1 做够目标单数 ⇒ 赢；
/// 2 赢优先于回合用尽（最后一回合刚好做完，不能判输）；
/// 3 回合用尽且没做够 ⇒ 输，且理由写明还差几单；
/// 4 目标为 0（沙盒）⇒ 永不判胜，也永不因为回合判输（没有上限时更是永远进行中）；
/// 5 活世界报出来的结局与纯函数逐字一致（同一把尺子，不是两套算法）。
/// </summary>
public class LevelGoalTests
{
    private static global::JarSystem.Core.JarSystem NewSystem(int turnLimit, int goalOrders, int maxJars = 6)
    {
        var world = WorldOptions.Default;
        world.DefaultMaxJars = maxJars;
        world.Workshops = new List<WorkshopOptions>
        {
            new() { Name = "主台", BaseMarks = new List<string> { "圆罐", "方罐" } }
        };
        world.TurnLimit = turnLimit;
        world.GoalOrders = goalOrders;
        var report = world.Validate();
        Assert.True(report.IsValid, "用例前提：世界必须合法 —— " + string.Join("；", report.Errors));
        return new global::JarSystem.Core.JarSystem(randomSeed: 7, world: world);
    }

    [Fact]
    public void ReachingTheGoalWins()
    {
        var turns = new TurnBudget(3, used: 1);
        var outcome = LevelGoal.Judge(goalOrders: 1, completedOrders: 1, turns: turns);

        Assert.Equal(LevelOutcomeKind.Won, outcome.Kind);
        Assert.True(outcome.IsOver);
        Assert.True(outcome.IsWon);
        Assert.Equal("won", outcome.Name);
    }

    [Fact]
    public void WinningOutranksRunningOutOfTurns()
    {
        // 正好在最后一回合做完：预算已用尽（IsExhausted 为真），但目标达成了。
        var turns = new TurnBudget(limit: 3, used: 3);
        Assert.True(turns.IsExhausted);

        var outcome = LevelGoal.Judge(goalOrders: 2, completedOrders: 2, turns: turns);

        Assert.Equal(LevelOutcomeKind.Won, outcome.Kind);
    }

    [Fact]
    public void RunningOutWithoutEnoughOrdersLoses()
    {
        var turns = new TurnBudget(limit: 3, used: 3);
        var outcome = LevelGoal.Judge(goalOrders: 2, completedOrders: 1, turns: turns);

        Assert.Equal(LevelOutcomeKind.Lost, outcome.Kind);
        Assert.Equal("lost", outcome.Name);
        Assert.Contains("还差 1 单", outcome.Reason);

        // 纯函数不该因为"差得多"换个说法：差几就写几。
        var worse = LevelGoal.Judge(goalOrders: 3, completedOrders: 0, turns: turns);
        Assert.Contains("还差 3 单", worse.Reason);
    }

    [Fact]
    public void NoGoalNeverWins()
    {
        // 沙盒：goalOrders = 0 且不限回合 ⇒ 永远进行中，哪怕已经"完成"了若干单。
        var unlimited = LevelGoal.Judge(goalOrders: 0, completedOrders: 99, turns: new TurnBudget(0, used: 0));
        Assert.Equal(LevelOutcomeKind.InProgress, unlimited.Kind);

        // 目标为 0 但回合有限：回合用完时照样输（那一局确实没法继续了）。
        var exhausted = LevelGoal.Judge(goalOrders: 0, completedOrders: 1, turns: new TurnBudget(2, used: 2));
        Assert.Equal(LevelOutcomeKind.Lost, exhausted.Kind);
        Assert.Contains("回合已用完", exhausted.Reason);
    }

    [Fact]
    public void TheWorldReportsTheOutcomeOfTheSameRule()
    {
        const int limit = 3;
        var sys = NewSystem(turnLimit: limit, goalOrders: 1);

        // 一单都没做：推满上限之前一直在打，推满之后就输 —— 与纯函数逐字一致。
        Assert.Equal("inProgress", sys.Outcome.Name);
        Assert.Equal(0, sys.CompletedOrders);

        sys.Tick(limit);

        var expected = LevelGoal.Judge(1, sys.CompletedOrders, sys.Turns);
        Assert.Equal(expected.Kind, sys.Outcome.Kind);
        Assert.Equal(expected.Reason, sys.Outcome.Reason);
        Assert.Equal(LevelOutcomeKind.Lost, sys.Outcome.Kind);
        Assert.Contains("还差 1 单", sys.Outcome.Reason);
    }

    [Fact]
    public void AShippedLevelIsPlayableTowardsItsGoal()
    {
        // 关卡文件读出来的目标必须是真的目标：0 的话这一关永远赢不了，
        // 作者会以为自己在写关卡，实际写了个无解的沙盒。
        var root = RepoRoot();
        var dir = Path.Combine(root, "config", "levels");
        Assert.True(Directory.Exists(dir), "找不到 config/levels 目录");
        var files = Directory.EnumerateFiles(dir, "*.json")
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var el = doc.RootElement;
            var label = Path.GetFileName(file);

            Assert.True(el.TryGetProperty("goal", out var g) && g.ValueKind == JsonValueKind.Number,
                label + " 缺少 goal（做成几单算赢）");
            Assert.True(g.GetInt32() > 0, label + " 的 goal 必须是正数，实际 " + g.GetInt32());

            // 目标单数与回合上限一样只写在信封上：world 块里再写一遍，两边立刻会漂移。
            Assert.True(el.TryGetProperty("world", out var w) && w.ValueKind == JsonValueKind.Object,
                label + " 缺少 world 对象");
            Assert.False(w.TryGetProperty("goalOrders", out _),
                label + " 的 world 块里不该写 goalOrders：目标只写在信封的 goal 上");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JarSystem.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

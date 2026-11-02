using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JarSystem.Core;
using JarSystem.Core.Config;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **关卡可玩性**：每一关都得真的能赢，而且要留得出余量。
///
/// 为什么要这条测试：判胜规则（<see cref="JarSystem.Core.World.LevelGoal.Judge"/>）只回答
/// "做够了没有"，它不知道"做得到吗"。一关如果把回合写得比生产节奏紧，玩家永远赢不了，
/// 而所有单元测试都是绿的 —— 旧原型正是摔在"只报数字不断言"上（见 <c>demo/关卡可解性判定.md</c>）。
///
/// 做法：一个**基线打手**，它只会做四件事 —— 选主台/从台（布置）、分析、开始能做的单、推进一回合。
/// 打手对**每一种台的摆法**都试一遍；只要有一种摆法能赢，这一关就是可玩的
/// （真人当然会挑那个摆法，这已经是最宽松的下界）。
///
/// 断言两件事：
/// 1 每一关都至少有一种摆法能赢 —— 否则这一关是死的；
/// 2 最好的那种摆法要留出**至少 1 回合余量** —— 否则真人走错一步就输，这一关不公平。
/// </summary>
public class LevelWinnabilityTests
{
    private sealed record BotResult(int Master, int Slave, bool Won, int TurnsUsed, int Limit, string Outcome, string Reason)
    {
        public int Margin => Limit - TurnsUsed;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JarSystem.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static IReadOnlyList<(string File, string Name, int Goal, int Turns, WorldOptions World)> ShippedLevels()
    {
        var dir = Path.Combine(RepoRoot(), "config", "levels");
        Assert.True(Directory.Exists(dir), "找不到 config/levels 目录");

        var levels = new List<(string, string, int, int, WorldOptions)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var name = root.GetProperty("name").GetString()!;
            var goal = root.GetProperty("goal").GetInt32();
            var turns = root.GetProperty("turns").GetInt32();
            var world = WorldOptionsJson.FromJson(root.GetProperty("world").GetRawText());
            world.TurnLimit = turns;
            world.GoalOrders = goal;
            var report = world.Validate();
            Assert.True(report.IsValid, Path.GetFileName(file) + " 的世界不合法：" + string.Join("；", report.Errors));
            levels.Add((file, name, goal, turns, world));
        }
        return levels;
    }

    /// <summary>
    /// 用一种固定的台摆法打一遍：**只做四件事** —— 布置主从台、分析、开始能做的单、推进一回合。
    /// 不挑单、不看求解器路径 —— 这是"最笨的玩家也赢得了"这条下界的含义。
    /// </summary>
    private static BotResult Play(WorldOptions world, int master, int slave, int maxOrders)
    {
        var sys = new global::JarSystem.Core.JarSystem(randomSeed: 41, world: world);
        sys.SetMasterIndex(master);
        if (slave >= 0) sys.SetSlaveIndex(slave);

        // 派单：按模板顺序各派一张（顺序确定 ⇒ 打手行为可复现，不因随机抖动）。
        var templateNames = sys.OrderTemplates.Names.ToList();
        for (var i = 0; i < templateNames.Count && i < maxOrders; i++)
            _ = sys.CreateOrderFromTemplate(templateNames[i]);

        for (var guard = 0; guard < 400; guard++)
        {
            if (sys.Outcome.IsOver)
                return new BotResult(master, slave, sys.Outcome.IsWon, sys.Turns.Used, sys.Turns.Limit,
                    sys.Outcome.Name, sys.Outcome.Reason);

            for (var i = 0; i < sys.Orders.Count; i++)
            {
                if (sys.Orders[i].Status != Constants.OrderStatus.PENDING) continue;
                var feas = sys.CheckFeasibility(i);
                if (feas is { Feasible: true }) _ = sys.StartOrder(i);
            }

            if (!sys.Turns.Allows(1)) break;
            sys.Tick(1);
        }

        return new BotResult(master, slave, sys.Outcome.IsWon, sys.Turns.Used, sys.Turns.Limit,
            sys.Outcome.Name, sys.Outcome.Reason);
    }

    private static List<BotResult> PlayAllSetups(WorldOptions world, int maxOrders)
    {
        var results = new List<BotResult>();
        var n = world.Workshops.Count;
        for (var master = 0; master < n; master++)
        {
            results.Add(Play(world, master, slave: -1, maxOrders));
            for (var slave = 0; slave < n; slave++)
            {
                if (slave == master) continue;
                results.Add(Play(world, master, slave, maxOrders));
            }
        }
        return results;
    }

    [Fact]
    public void EveryShippedLevelIsWinnableByThePlainestPlayer()
    {
        var levels = ShippedLevels();
        Assert.NotEmpty(levels);

        var lines = new List<string>();
        var failures = new List<string>();
        foreach (var (file, name, goal, turns, world) in levels)
        {
            var results = PlayAllSetups(world, maxOrders: Math.Max(1, goal + 1));
            var wins = results.Where(r => r.Won).OrderByDescending(r => r.Margin).ToList();
            var label = Path.GetFileNameWithoutExtension(file);

            if (wins.Count == 0)
            {
                var best = results.OrderByDescending(r => r.TurnsUsed).First();
                lines.Add("  " + label + " | 上限 " + turns + " | 全部摆法都赢不了（最好的一次：" + best.Reason + "）");
                failures.Add("关卡「" + name + "」没有任何一种台的摆法能赢（试了 " + results.Count + " 种）");
                continue;
            }

            var top = wins[0];
            lines.Add("  " + label + " | 上限 " + turns + " | 打手 " + top.TurnsUsed + " | 余量 " + top.Margin +
                      " | 主台 #" + top.Master + (top.Slave >= 0 ? " 从台 #" + top.Slave : " 无当台") +
                      " | " + wins.Count + "/" + results.Count + " 种摆法能赢");

            if (top.Margin < 1)
                failures.Add("关卡「" + name + "」没有余量：上限 " + turns + " 回合，最好的摆法也要用 " +
                             top.TurnsUsed + " 回合 —— 真人走错一步就输，这一关不公平");
        }

        Assert.True(failures.Count == 0,
            "有 " + failures.Count + " 关不可玩：\n" + string.Join("\n", failures) +
            "\n打手成绩（每关最好的一种摆法）：\n" + string.Join("\n", lines));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **关卡可玩性**：每一关都得真的能赢，而且要留得出余量。
///
/// 为什么这条测试在：判胜规则（<see cref="LevelGoal.Judge"/>）只回答"做够了没有"，
/// 它不知道"做得到吗"。一关如果把回合写得比生产节奏紧，玩家永远赢不了，而所有单元测试都是绿的
/// （旧原型正是摔在"只报数字不断言"上，见 <c>demo/关卡可解性判定.md</c>）。
///
/// 打手与闸门**住在内核**（<see cref="LevelBaselineBot"/> / <see cref="LevelGates"/>），不在这条测试里：
/// 程序化关卡在运行期也要过同一道闸，两处各写一套迟早会分叉（测试放过的关卡生成器拒绝，或者反过来）。
/// 这条测试只负责"把每一关都推过那道闸"。
/// </summary>
public class LevelWinnabilityTests
{
    private static IReadOnlyList<LevelInfo> ShippedLevels()
    {
        // 关卡目录由宿主层解析（LevelCatalog）：测试不另写一份"怎么读关卡文件"。
        var levels = LevelCatalog.Discover();
        Assert.NotEmpty(levels);
        return levels;
    }

    [Fact]
    public void EveryShippedLevelPassesTheGateTheGeneratorAlsoUses()
    {
        var failures = new List<string>();
        foreach (var level in ShippedLevels())
        {
            var report = LevelGates.Check(level.World, level.Turns, level.World.GoalOrders, level.Orders);
            if (!report.Passed)
                failures.Add("关卡「" + level.Name + "」（" + Path.GetFileName(level.Path) + "）：" + report.Describe());
        }

        Assert.True(failures.Count == 0,
            "有 " + failures.Count + " 关过不了闸门：\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// 打手成绩单：每关打出**最好的一种摆法**。与上一条分开，是因为上一条断言"过闸"，
    /// 这一条把余量数字摆出来 —— 数字掉了但还 ≥ 1 时断言不会红，人却要看见。
    /// </summary>
    [Fact]
    public void TheBestSetupLeavesAtLeastOneTurnOfSlack()
    {
        var lines = new List<string>();
        var failures = new List<string>();

        foreach (var level in ShippedLevels())
        {
            var runs = LevelBaselineBot.PlayAllSetups(level.World, Math.Max(1, level.World.GoalOrders + 1));
            var wins = runs.Where(r => r.Won).OrderByDescending(r => r.Margin).ToList();
            var label = Path.GetFileNameWithoutExtension(level.Path);

            if (wins.Count == 0)
            {
                var closest = runs.OrderByDescending(r => r.TurnsUsed).First();
                lines.Add("  " + label + " | 上限 " + level.Turns + " | 全部摆法都赢不了（最好的一次：" + closest.Reason + "）");
                failures.Add("关卡「" + level.Name + "」没有任何一种台的摆法能赢（试了 " + runs.Count + " 种）");
                continue;
            }

            var top = wins[0];
            lines.Add("  " + label + " | 上限 " + level.Turns + " | 打手 " + top.TurnsUsed + " | 余量 " + top.Margin +
                      " | 主台 #" + top.Master + (top.Slave >= 0 ? " 从台 #" + top.Slave : " 无当台") +
                      " | " + wins.Count + "/" + runs.Count + " 种摆法能赢");

            if (top.Margin < LevelGates.MinimumSlack)
                failures.Add("关卡「" + level.Name + "」没有余量：上限 " + level.Turns + " 回合，最好的摆法也要用 " +
                             top.TurnsUsed + " 回合 —— 真人走错一步就输，这一关不公平");
        }

        Assert.True(failures.Count == 0,
            "有 " + failures.Count + " 关不可玩：\n" + string.Join("\n", failures) +
            "\n打手成绩（每关最好的一种摆法）：\n" + string.Join("\n", lines));
    }
}

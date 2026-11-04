using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;

namespace JarSystem.Core.World;

/// <summary>关卡闸门的结论：过了没有、没过的话每条原因是什么。</summary>
public sealed record LevelGateReport(bool Passed, IReadOnlyList<string> Failures)
{
    public static LevelGateReport Ok() => new(true, Array.Empty<string>());
    public static LevelGateReport Fail(params string[] failures) => new(false, failures);

    /// <summary>拼成一句给人看的话（失败时用；通过时返回空串）。</summary>
    public string Describe() => Passed ? "" : string.Join("；", Failures);
}

/// <summary>
/// **关卡闸门**：一关（手写的或程序生成的）在开局前必须过的机器可检查条件。
///
/// 为什么住内核：这些条件全是**玩法事实**（有没有来源、能不能赢、余量够不够），
/// 与"谁生成它"无关。手写关卡由测试把关、生成关卡由生成器把关 —— 若两边各写一套，
/// 迟早会分叉：测试放过的关卡生成器拒绝，或者反过来。
///
/// 覆盖 SPEC《Difficulty and Generation Gates》里可机器判定的部分：
/// <list type="number">
/// <item>世界合法（词表/工作台/生产者/容量，<see cref="WorldOptions.Validate"/>）；</item>
/// <item>回合与目标自洽（turns &gt; 0、goal ≥ 1、派得出的单够凑够目标）；</item>
/// <item>订单实例都指向存在的模板；</item>
/// <item>每个被要求的形状/液体都有**声明式来源**（产线、初始库存，或"能改形改出来"）；</item>
/// <item>基线打手至少赢一次，且**留出 ≥ <see cref="MinimumSlack"/> 回合余量**。</item>
/// </list>
///
/// 没覆盖的（诚实记录在 <c>SPEC-demo-campaign.md</c> 里）："恰好引入一个新机制""产出不超出资源预算"
/// 这两条需要人工审读作者的意图，机器只能给出线索，不能裁决。
/// </summary>
public static class LevelGates
{
    /// <summary>作者要留的最低余量（回合）。0 回合余量意味着走错一步就输。</summary>
    public const int MinimumSlack = 1;

    /// <summary>打手最多试这么大的订单数（目标 + 1 已经够证明"还有一单没做"是可能的）。</summary>
    private static int MaxOrders(int goal) => Math.Max(1, goal + 1);

    public static LevelGateReport Check(
        WorldOptions world,
        int turns,
        int goal,
        IReadOnlyList<string> orderInstances,
        bool checkWinnability = true)
    {
        var failures = new List<string>();

        // 1 世界合法
        var validity = world.Validate();
        if (!validity.IsValid)
            failures.Add("世界不合法：" + string.Join("，", validity.Errors));

        // 2 回合与目标自洽
        if (turns <= 0) failures.Add("回合上限必须 > 0（现在是 " + turns + "）");
        if (goal < 1) failures.Add("目标订单数必须 ≥ 1（现在是 " + goal + "）");
        if (goal > orderInstances.Count)
            failures.Add("目标 " + goal + " 单，但只派得出 " + orderInstances.Count + " 单 —— 永远赢不了");

        // 3 订单实例都指向存在的模板
        var templates = world.OrderTemplates.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var instance in orderInstances)
            if (!templates.Contains(instance))
                failures.Add("订单实例引用了不存在的模板「" + instance + "」");

        // 4 声明式来源
        failures.AddRange(CheckSources(world, orderInstances));

        // 5 可赢 + 余量
        if (checkWinnability && failures.Count == 0)
        {
            var probe = world.Clone();
            probe.TurnLimit = turns;
            probe.GoalOrders = goal;

            var runs = LevelBaselineBot.PlayAllSetups(probe, MaxOrders(goal));
            var best = LevelBaselineBot.Best(runs);
            if (best == null)
            {
                var closest = runs.OrderByDescending(r => r.TurnsUsed).FirstOrDefault();
                failures.Add("基线打手在 " + runs.Count + " 种摆法下一次都没赢（最接近的一次：" +
                             (closest?.Reason ?? "没有可打的摆法") + "）");
            }
            else if (best.Margin < MinimumSlack)
            {
                failures.Add("没有余量：上限 " + turns + " 回合，最好的摆法也要用 " + best.TurnsUsed +
                             " 回合 —— 真人走错一步就输");
            }
        }

        return failures.Count == 0 ? LevelGateReport.Ok() : LevelGateReport.Fail(failures.ToArray());
    }

    /// <summary>
    /// 每个被订单要求的形状/液体，都必须指得出来源：产线产出、初始库存，或"某个台能把基础标记改形成它"。
    /// 这一条是《No Mysterious Resources》在**开局前**的版本：开局前就没有来源的东西，局中也不会凭空出现。
    /// </summary>
    private static IEnumerable<string> CheckSources(WorldOptions world, IReadOnlyList<string> orderInstances)
    {
        var produced = world.Producers
            .SelectMany(p => p.Rates)
            .Where(kv => kv.Value > 0)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        var reshapeTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workshop in world.Workshops)
        {
            if (workshop.BaseMarks.Count == 0) continue;
            foreach (var mark in workshop.AdditionalMarks) reshapeTargets.Add(mark);
        }

        var templates = world.OrderTemplates.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var instance in orderInstances)
        {
            if (!templates.TryGetValue(instance, out var template)) continue;
            if (!seen.Add(instance)) continue;

            foreach (var requirement in template.Requirements)
            {
                var shape = requirement.Shape;
                if (!string.IsNullOrEmpty(shape) &&
                    !produced.Contains(shape) &&
                    !reshapeTargets.Contains(shape) &&
                    !IsBaseMark(world, shape))
                {
                    yield return "订单「" + instance + "」要的形状「" + shape +
                                 "」没有来源（既没有产线产出，也不是任何台的基础标记或可改形目标）";
                }

                var liquid = requirement.Liquid;
                if (string.IsNullOrEmpty(liquid)) continue;
                var stocked = world.InitialStock.GetValueOrDefault(liquid, 0) > 0;
                var flowed = world.Flow.LiquidRate.GetValueOrDefault(liquid, 0) > 0;
                var fromJars = world.Producers.SelectMany(p => p.Rates).Any(kv => kv.Value > 0);
                if (!stocked && !flowed && !fromJars)
                {
                    yield return "订单「" + instance + "」要的液体「" + liquid +
                                 "」没有来源（初始库存 0、流入速率 0、也没有任何产线）";
                }
            }
        }
    }

    private static bool IsBaseMark(WorldOptions world, string shape) =>
        world.Workshops.Any(w => w.BaseMarks.Contains(shape, StringComparer.Ordinal));
}

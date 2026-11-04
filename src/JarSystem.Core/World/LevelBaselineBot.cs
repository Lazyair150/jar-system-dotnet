using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;

namespace JarSystem.Core.World;

/// <summary>一次基线打手的结果：谁当主台/从台、赢没赢、用了几回合、余量多少。</summary>
public sealed record BotRun(int Master, int Slave, bool Won, int TurnsUsed, int Limit, string Outcome, string Reason)
{
    /// <summary>余量 = 回合上限 − 实际用了多少回合。&lt; 1 表示这一步都错不起。</summary>
    public int Margin => Limit - TurnsUsed;
}

/// <summary>
/// **基线打手**：用"最笨的玩家"给关卡定可玩性下界。
///
/// 它只会做四件事 —— 布置主从台、分析、开始能做的单、推进一回合；不看求解器路径、不挑单、
/// 不做任何"聪明"的安排。只要有一种台的摆法它能赢，这一关就是可玩的（真人当然会挑那种摆法，
/// 所以这是**最宽松的下界**）。
///
/// 为什么住在内核而不是测试里：程序化生成的关卡也要过同一道闸（<see cref="LevelGates"/>），
/// 而生成发生在宿主运行期 —— 若打手只存在于测试工程，运行期就没有"这一关真的能赢"的证据，
/// 生成器只能自说自话。**同一个owner、同一份判定**，测试与生成器共用。
/// </summary>
public static class LevelBaselineBot
{
    /// <summary>
    /// 用一种固定的台摆法打一遍。派单按模板顺序各派一张（顺序确定 ⇒ 结果可复现，不因随机抖动）。
    /// </summary>
    public static BotRun Play(WorldOptions world, int master, int slave, int maxOrders, int randomSeed = 41)
    {
        var sys = new JarSystem(log: Silent, randomSeed: randomSeed, world: world);
        sys.SetMasterIndex(master);
        if (slave >= 0) sys.SetSlaveIndex(slave);

        var templateNames = sys.OrderTemplates.Names.ToList();
        for (var i = 0; i < templateNames.Count && i < maxOrders; i++)
            _ = sys.CreateOrderFromTemplate(templateNames[i]);

        for (var guard = 0; guard < 400; guard++)
        {
            if (sys.Outcome.IsOver)
                return new BotRun(master, slave, sys.Outcome.IsWon, sys.Turns.Used, sys.Turns.Limit,
                    sys.Outcome.Name, sys.Outcome.Reason);

            for (var i = 0; i < sys.Orders.Count; i++)
            {
                if (sys.Orders[i].Status != Constants.OrderStatus.PENDING) continue;
                var feas = sys.CheckFeasibility(i);
                if (feas is { Feasible: true }) _ = sys.StartOrder(i);
            }

            // 直接交付没有 tick 过程；开始订单后本关可能已经结束。
            if (sys.Outcome.IsOver)
                return new BotRun(master, slave, sys.Outcome.IsWon, sys.Turns.Used, sys.Turns.Limit,
                    sys.Outcome.Name, sys.Outcome.Reason);

            if (!sys.Turns.Allows(1)) break;
            sys.Tick(1);
        }

        return new BotRun(master, slave, sys.Outcome.IsWon, sys.Turns.Used, sys.Turns.Limit,
            sys.Outcome.Name, sys.Outcome.Reason);
    }

    /// <summary>对每一种台的摆法都打一遍（主台 × (无当台 + 每一个从台)）。</summary>
    public static List<BotRun> PlayAllSetups(WorldOptions world, int maxOrders, int randomSeed = 41)
    {
        var results = new List<BotRun>();
        var n = world.Workshops.Count;
        for (var master = 0; master < n; master++)
        {
            results.Add(Play(world, master, slave: -1, maxOrders, randomSeed));
            for (var slave = 0; slave < n; slave++)
            {
                if (slave == master) continue;
                results.Add(Play(world, master, slave, maxOrders, randomSeed));
            }
        }
        return results;
    }

    /// <summary>挑出余量最大的那次胜利；一次都赢不了就返回 null。</summary>
    public static BotRun? Best(IEnumerable<BotRun> runs) =>
        runs.Where(r => r.Won).OrderByDescending(r => r.Margin).FirstOrDefault();

    /// <summary>
    /// 打手用的静默日志：一次闸门要试出 N×(N+1) 种摆法，每种都新建一个世界，
    /// 若沿用控制台日志，同一批世界配置告警会被重复打印十几遍（那是**闸门**的噪声，不是运行期事实）。
    /// 真实那一局仍用宿主给的日志如实打印。
    /// </summary>
    private sealed class SilentLog : Abstractions.ISystemLog
    {
        public void Error(string message, Exception? ex = null) { }
        public void Info(string message) { }
    }

    private static readonly SilentLog Silent = new();
}

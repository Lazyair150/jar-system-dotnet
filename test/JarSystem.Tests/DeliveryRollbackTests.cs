using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 从台 → 主台的回滚（<see cref="SlaveRollback"/>）的契约测试。
///
/// 为什么要有这一组：`规则.txt:26-28` 写着「B 类：主台预处理 → 选择性转移（只转移交付罐 + 从台阶段
/// 计划使用的工具罐）→ 从台继续 → 从台交付」「未交付的转移罐子与未用液体在订单结束后回流主台」，
/// `:22-23` 又写着「**禁液令不影响满罐转移**」。而 2026-11 之前
/// `grep Deliver|RollbackSlaveToMaster test/` **零命中** —— 这一层同样只有文档和实现，没有守门人。
///
/// 注意：`SlaveRollback` 是 internal，本组测试靠 `InternalsVisibleTo` 直接调它。
/// 从台是临时工具位，收尾必须排空；这里既覆盖普通转移物，也覆盖路径执行后
/// 才产生的游离液体，防止实现误把“转入账目”当成“从台剩余物全集”。
/// </summary>
public class DeliveryRollbackTests
{
    private static Workshop Bench(string name, int maxJars = 6, int maxLiquids = 30, params string[] ban) =>
        new(name, new List<string>(), new List<string>(), ban.ToList(), maxJars: maxJars, maxLiquids: maxLiquids);

    private static Jar MakeJar(int id, string shape, string? liquid = null) => new(shape, liquid, id);

    private static List<ExecutionLogEntry> Run(
        Workshop master, Workshop slave,
        System.Action<int>? countDiscardedJars = null,
        System.Action<string, int>? countDiscardedLiquids = null)
    {
        var log = new List<ExecutionLogEntry>();
        new SlaveRollback().Execute(master, slave, log.Add,
            countDiscardedJars, countDiscardedLiquids);
        return log;
    }

    [Fact]
    public void TransferredJarsGoBackToTheMaster()
    {
        var master = Bench("主台");
        var slave = Bench("从台");
        var jar = MakeJar(7, "圆罐");
        slave.AddJar(jar);

        var log = Run(master, slave);

        Assert.Empty(slave.Jars);
        Assert.Single(master.Jars);
        Assert.Equal(7, master.Jars[0].Id);
        Assert.Empty(log);                                  // 接得下就没有丢弃日志
    }

    [Fact]
    public void EveryRemainingJarReturnsToTheMaster()
    {
        var master = Bench("主台");
        var slave = Bench("从台");
        slave.AddJar(MakeJar(9, "方罐"));

        var log = Run(master, slave);

        Assert.Empty(slave.Jars);
        Assert.Single(master.Jars);
        Assert.Equal(9, master.Jars[0].Id);
        Assert.Empty(log);
    }

    [Fact]
    public void FullMasterDiscardsTheJarAndLogsIt()
    {
        var master = Bench("主台", maxJars: 1);
        master.AddJar(MakeJar(1, "圆罐"));                  // 主台只剩 0 个罐位
        var slave = Bench("从台");
        slave.AddJar(MakeJar(7, "高罐"));

        var discarded = 0;
        var log = Run(master, slave,
            countDiscardedJars: count => discarded += count);

        Assert.Single(master.Jars);                         // 没挤进去
        Assert.Empty(slave.Jars);                  // 已离开从台
        var discard = Assert.Single(log);
        Assert.Equal("discard_jar", discard.Command);
        Assert.Contains("不回收", discard.Reason);
        Assert.Equal(1, discarded);
    }

    [Fact]
    public void BannedLiquidOnAFullJarStillTravels()
    {
        // 规则.txt:22-23 禁液令不影响满罐转移：装被禁液体的罐子原样归还，不清空罐内液体
        var master = Bench("主台", ban: "酒");
        var slave = Bench("从台");
        var jar = MakeJar(7, "圆罐", "酒");
        slave.AddJar(jar);

        Run(master, slave);

        Assert.Single(master.Jars);
        Assert.Equal("酒", master.Jars[0].Liquid);          // 液体没被清掉
    }

    [Fact]
    public void EveryRemainingLiquidPartiallyFillsAndDiscardsTheRemainder()
    {
        var master = Bench("主台", maxLiquids: 1);
        var slave = Bench("从台");
        slave.AddLiquid("蜜", 3);

        var discarded = 0;
        var log = Run(master, slave,
            countDiscardedLiquids: (_, amount) => discarded += amount);

        Assert.Equal(1, master.LiquidStorage["蜜"]);         // 部分填充
        Assert.Equal(0, slave.LiquidStorage["蜜"]);          // 从台清空
        var discard = Assert.Single(log);
        Assert.Equal("discard_liquid", discard.Command);
        Assert.Contains("不回收", discard.Reason);
        Assert.Equal(2, discarded);
    }
}

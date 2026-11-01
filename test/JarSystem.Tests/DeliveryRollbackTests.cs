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
/// 不走端到端（`ExecuteOrder`）是刻意的：端到端测不到"**只**回滚本次转移来的罐子、
/// 从台原有罐子不动"这条 —— 那正是这段代码最容易写错的地方。
/// </summary>
public class DeliveryRollbackTests
{
    private static Workshop Bench(string name, int maxJars = 6, int maxLiquids = 30, params string[] ban) =>
        new(name, new List<string>(), new List<string>(), ban.ToList(), maxJars: maxJars, maxLiquids: maxLiquids);

    private static Jar MakeJar(int id, string shape, string? liquid = null) => new(shape, liquid, id);

    private static List<ExecutionLogEntry> Run(
        Workshop master, Workshop slave,
        IReadOnlyCollection<int> transferredJarIds,
        IReadOnlyCollection<int> slaveOriginalJarIds,
        IReadOnlyDictionary<string, int>? transferredLiquids = null)
    {
        var log = new List<ExecutionLogEntry>();
        new SlaveRollback().Execute(master, slave, transferredJarIds, slaveOriginalJarIds,
            transferredLiquids ?? new Dictionary<string, int>(), log.Add);
        return log;
    }

    [Fact]
    public void TransferredJarsGoBackToTheMaster()
    {
        var master = Bench("主台");
        var slave = Bench("从台");
        var jar = MakeJar(7, "圆罐");
        slave.AddJar(jar);

        var log = Run(master, slave, new[] { 7 }, System.Array.Empty<int>());

        Assert.Empty(slave.Jars);
        Assert.Single(master.Jars);
        Assert.Equal(7, master.Jars[0].Id);
        Assert.Empty(log);                                  // 接得下就没有丢弃日志
    }

    [Fact]
    public void SlaveOriginalJarsAreNotRolledBack()
    {
        var master = Bench("主台");
        var slave = Bench("从台");
        slave.AddJar(MakeJar(9, "方罐"));                   // 订单开始前就在从台的罐子

        var log = Run(master, slave, System.Array.Empty<int>(), new[] { 9 });

        Assert.Single(slave.Jars);                          // 原地不动
        Assert.Empty(master.Jars);
        Assert.Empty(log);
    }

    [Fact]
    public void FullMasterDiscardsTheJarAndLogsIt()
    {
        var master = Bench("主台", maxJars: 1);
        master.AddJar(MakeJar(1, "圆罐"));                  // 主台只剩 0 个罐位
        var slave = Bench("从台");
        slave.AddJar(MakeJar(7, "高罐"));

        var log = Run(master, slave, new[] { 7 }, System.Array.Empty<int>());

        Assert.Single(master.Jars);                         // 没挤进去
        Assert.Empty(slave.Jars);                  // 已离开从台
        var discard = Assert.Single(log);
        Assert.Equal("discard_jar", discard.Command);
        Assert.Contains("不回收", discard.Reason);
    }

    [Fact]
    public void BannedLiquidOnAFullJarStillTravels()
    {
        // 规则.txt:22-23 禁液令不影响满罐转移：装被禁液体的罐子原样归还，不清空罐内液体
        var master = Bench("主台", ban: "酒");
        var slave = Bench("从台");
        var jar = MakeJar(7, "圆罐", "酒");
        slave.AddJar(jar);

        Run(master, slave, new[] { 7 }, System.Array.Empty<int>());

        Assert.Single(master.Jars);
        Assert.Equal("酒", master.Jars[0].Liquid);          // 液体没被清掉
    }

    [Fact]
    public void TransferredLiquidPartiallyFillsAndDiscardsTheRemainder()
    {
        var master = Bench("主台", maxLiquids: 1);
        var slave = Bench("从台");
        slave.AddLiquid("蜜", 3);

        var log = Run(master, slave, System.Array.Empty<int>(), System.Array.Empty<int>(),
            new Dictionary<string, int> { ["蜜"] = 3 });

        Assert.Equal(1, master.LiquidStorage["蜜"]);         // 部分填充
        Assert.Equal(0, slave.LiquidStorage["蜜"]);          // 从台清空
        var discard = Assert.Single(log);
        Assert.Equal("discard_liquid", discard.Command);
        Assert.Contains("不回收", discard.Reason);
    }
}

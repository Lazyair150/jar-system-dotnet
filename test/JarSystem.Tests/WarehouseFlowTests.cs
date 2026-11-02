using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 中央仓库与「仓库 → 工作台」流动（<see cref="Warehouse"/>）的契约测试。
///
/// 为什么要有这一组：`规则.txt:31` 写着「**中央仓库单向流动：资源只从仓库流向工作台；
/// 工作台容量溢出与回滚溢出的资源直接丢弃（计入丢弃计数），不回收**」，
/// 而 2026-11 之前 `grep FlowToMaster\|StoreJars\|WAREHOUSE_MAX test/` **零命中** ——
/// 规则写了、代码实现了、检查缺席。于是原型侧可以"自由地"把产出实现成
/// "命令当前主台生产、形状不合格就丢弃"，没有任何东西会变红。
///
/// 本组测试钉住四件容易被想当然改写的事：
/// 1 **任何形状的罐子都能进工作台** —— 基础标记约束的是"能不能操作"，不是"能不能存"。
/// 2 优先送基础形状的罐子；**没有基础形状时取第一只**（不是"送不进去"）。
/// 3 工作台满了就把罐子**留在仓库**，不丢弃；只有仓库自身溢出才丢弃并计数。
/// 4 液体按"每种每次至多 liquidAmount 份 / 受工作台容量限制 / 被禁则一份不送"部分填充。
/// </summary>
public class WarehouseFlowTests
{
    private const int MasterMaxJars = 6;

    private static Workshop Bench(
        List<string> baseMarks,
        List<string>? additional = null,
        List<string>? ban = null,
        int maxLiquids = 5) =>
        new("主台", baseMarks, additional ?? new List<string>(), ban ?? new List<string>(),
            maxJars: MasterMaxJars, maxLiquids: maxLiquids);

    private static Workshop Xuanwu(int maxLiquids = 5) =>
        Bench(new List<string> { "圆罐", "方罐" }, new List<string> { "三角罐" }, new List<string> { "酒" }, maxLiquids);

    private static Jar J(string shape, string? liquid = null) => new(shape, liquid);

    [Fact]
    public void AnyShapeFlowsIntoTheMasterWhenNoBaseMarkedShapeExists()
    {
        var wh = new Warehouse();
        wh.StoreJars(new[] { J("三角罐") });          // 三角罐是玄武的**附加**标记，不是基础标记
        var master = Xuanwu();

        var (jars, _) = wh.FlowToMaster(master);

        Assert.Single(jars);
        Assert.Equal("三角罐", jars[0].Shape);
        Assert.Contains(master.Jars, j => j.Shape == "三角罐");
        Assert.Equal(0, wh.JarCount);                 // 已经流走了，不是被丢弃
        Assert.Equal(0, wh.DiscardedJars);
    }

    [Fact]
    public void BaseMarkedShapesArePickedFirstButOthersStillCome()
    {
        var wh = new Warehouse();
        wh.StoreJars(new[] { J("三角罐"), J("圆罐") });
        var master = Xuanwu();

        var (jars, _) = wh.FlowToMaster(master);

        Assert.Equal(2, jars.Count);
        Assert.Equal("圆罐", jars[0].Shape);           // 基础标记优先
        Assert.Equal("三角罐", jars[1].Shape);         // 剩下的照样送
    }

    [Fact]
    public void MasterCapacityFullLeavesJarsInTheWarehouse()
    {
        var wh = new Warehouse();
        wh.StoreJars(Enumerable.Range(0, MasterMaxJars + 3).Select(_ => J("圆罐")));
        var master = Xuanwu();

        var (jars, _) = wh.FlowToMaster(master, jarCount: 99);

        Assert.Equal(MasterMaxJars, jars.Count);
        Assert.Equal(MasterMaxJars, master.Jars.Count);
        Assert.Equal(3, wh.JarCount);                 // 留下的在仓库里等下一次
        Assert.Equal(0, wh.DiscardedJars);            // **不是丢弃**
    }

    [Fact]
    public void BannedLiquidFlowsInButCannotBeUsed()
    {
        // 2026-11 语义：禁令只约束**取用**，不约束**存储**。
        // 所以被禁的酒照样能流进玄武台（那一格仍然存在），但它取不出来 —— 这才是"禁"的意思。
        var wh = new Warehouse();
        wh.ProduceLiquid("酒", 5);                    // 玄武禁酒
        wh.ProduceLiquid("蜜", 5);
        var master = Xuanwu();

        var (_, liquids) = wh.FlowToMaster(master);

        Assert.Equal(2, master.LiquidStorage["酒"]);   // 流入（每种每次最多 2 份）
        Assert.Equal(3, wh.LiquidCount("酒"));
        Assert.Equal(2, liquids["酒"]);
        Assert.Equal(2, master.LiquidStorage["蜜"]);
        Assert.Equal(3, wh.LiquidCount("蜜"));

        // 关键区别：能存 ≠ 能用。
        Assert.False(master.HasUsableLiquid("酒"));
        Assert.Equal("banned", master.CanUseLiquid("酒", 1).Code);
        Assert.True(master.CanUseLiquid("蜜", 1).Granted);
    }

    [Fact]
    public void LiquidFlowRespectsWorkshopCapacity()
    {
        var wh = new Warehouse();
        wh.ProduceLiquid("蜜", 5);
        var master = Xuanwu(maxLiquids: 1);            // 只能收 1 份

        wh.FlowToMaster(master);

        Assert.Equal(1, master.LiquidStorage["蜜"]);
        Assert.Equal(4, wh.LiquidCount("蜜"));
    }

    [Fact]
    public void WarehouseOverflowDiscardsAndCounts()
    {
        var wh = new Warehouse();

        wh.StoreJars(Enumerable.Range(0, Constants.WAREHOUSE_MAX_JARS + 5).Select(_ => J("圆罐")));
        Assert.Equal(Constants.WAREHOUSE_MAX_JARS, wh.JarCount);
        Assert.Equal(5, wh.DiscardedJars);

        wh.ProduceLiquid("蜜", Constants.WAREHOUSE_MAX_LIQUIDS + 7);
        Assert.Equal(Constants.WAREHOUSE_MAX_LIQUIDS, wh.LiquidCount("蜜"));
        Assert.Equal(7, wh.DiscardedLiquids["蜜"]);
    }
}

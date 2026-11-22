using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Process;
using JarSystem.Core.Services;
using JarSystem.Core.Sessions;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 运行期更换工作台能力参数（`setWorkshopCapability` → `Workshop.ReplaceCapability`）的契约测试。
///
/// 为什么需要它：在 B1 之前，工作台的「基础标记 / 附加标记 / 禁令」只能在**装配期**
/// （createWorkshop 或 world.json）决定，玩家进了世界就再也换不了。而「换台/换卡」是玩法的一部分
/// （demo 的关卡正是靠换台把禁令绕开）。这条命令补上之后，有三件事必须钉住，
/// 否则它会变成一个能凭空毁掉世界的后门：
///
/// 1 **改了就生效** —— 不只是字段变了，实际操作判定跟着变（PourIn 的依据就是基础标记）。
/// 2 **拒绝不能靠嘴** —— 词表外的形状/液体、以及「新禁令会锁死池里已有的液体」都必须当场拒绝，
///    后者尤其关键：`IsBanned` 同时挡 PourIn / PourOut / 空罐改形成本 / 满罐自耗，
///    池里存着该液体时它就再也倒不出来，世界立刻违反 `BANNED-LIQUID-STORED`（WorldInvariants 会抓到）。
/// 3 **被拒绝的改动必须什么都没发生** —— 世界不变、命令日志也不留一条（否则回放会重放一次"没成功的意图"）。
/// </summary>
public class WorkshopCapabilityTests
{
    private static JarSystem.Core.JarSystem NewSystem()
    {
        var sys = new JarSystem.Core.JarSystem(
            randomSeed: 7,
            jarIds: new SequentialIdGenerator(),
            orderIds: new SequentialIdGenerator());
        sys.ClearWorkshops();
        sys.AddWorkshop(new Workshop("玄武台", new List<string> { "圆罐", "方罐" },
            new List<string> { "三角罐" }, new List<string> { "酒" }, maxJars: 6, maxLiquids: 5));
        sys.SetMasterIndex(0);
        return sys;
    }

    [Fact]
    public void ChangingTheCapabilityTakesEffectOnThePorts()
    {
        var sys = NewSystem();
        var bench = sys.Workshops[0];
        Assert.True(bench.IsBaseMarked("圆罐"));

        // 圆罐 从"基础"降级成"附加"，三角罐 升成"基础"。
        sys.SetWorkshopCapability(0,
            baseMarks: new List<string> { "三角罐" },
            additionalMarks: new List<string> { "圆罐" },
            ban: new List<string>());

        Assert.True(bench.IsBaseMarked("三角罐"));
        Assert.False(bench.IsBaseMarked("圆罐"));
        Assert.True(bench.IsAdditionalMarked("圆罐"));

        // 判定真的跟着变了：圆罐不再能灌液（PourIn 要基础标记），三角罐可以。
        var round = new Jar("圆罐", null, null, sys.Vocabulary);
        bench.AddJar(round);
        var (roundOk, roundWhy) = bench.PourIn(round, "蜜");
        Assert.False(roundOk);
        Assert.Contains("基础标记", roundWhy ?? "");

        var tri = new Jar("三角罐", null, null, sys.Vocabulary);
        bench.AddJar(tri);
        bench.AddLiquid("蜜", 2);
        var (triOk, triWhy) = bench.PourIn(tri, "蜜");
        Assert.True(triOk, triWhy);
    }

    [Fact]
    public void AnUnknownShapeIsRejected()
    {
        var sys = NewSystem();
        var ex = Assert.Throws<DomainValidationException>(() =>
            sys.SetWorkshopCapability(0, baseMarks: new List<string> { "香蕉罐" }));
        Assert.Contains("无效形状", ex.Message);
    }

    [Fact]
    public void AnUnknownLiquidInTheBanListIsRejected()
    {
        var sys = NewSystem();
        var ex = Assert.Throws<DomainValidationException>(() =>
            sys.SetWorkshopCapability(0, ban: new List<string> { "醋" }));
        Assert.Contains("无效液体", ex.Message);
    }

    [Fact]
    public void AnInvalidIndexIsRejectedInsteadOfSilentlyIgnored()
    {
        var sys = NewSystem();
        var ex = Assert.Throws<DomainValidationException>(() => sys.SetWorkshopCapability(9));
        Assert.Contains("无效工作台索引", ex.Message);
    }

    [Fact]
    public void BanningALiquidThatIsStillInThePoolIsRejectedAtomically()
    {
        var sys = NewSystem();
        var bench = sys.Workshops[0];
        Assert.True(bench.AddLiquid("蜜", 2));
        var beforeLog = sys.CommandLog.Count;

        var error = Assert.Throws<DomainValidationException>(() =>
            sys.SetWorkshopCapability(0, ban: new List<string> { "蜜" }));

        Assert.Contains("仍存有游离液体", error.Message);
        Assert.False(bench.IsBanned("蜜"));
        Assert.Equal(2, bench.LiquidStorage["蜜"]);
        Assert.True(bench.HasUsableLiquid("蜜"));
        Assert.Equal(beforeLog, sys.CommandLog.Count);
        Assert.Empty(WorldInvariants.Check(sys));
    }

    [Fact]
    public void BanningTheSameLiquidOnABenchWithoutItIsAllowed()
    {
        // 池子里没有该液体时禁令无害，必须放行 —— 否则这条命令就只能"永远失败"。
        var sys = NewSystem();
        sys.AddWorkshop(new Workshop("麒麟台", new List<string> { "尖罐", "圆罐" },
            new List<string> { "方罐" }, new List<string>(), maxJars: 6, maxLiquids: 5));

        sys.SetWorkshopCapability(1, ban: new List<string> { "蜜" });
        Assert.True(sys.Workshops[1].IsBanned("蜜"));
    }

    [Fact]
    public void ASuccessfulChangeIsRecordedAndKeepsTheWorldConsistent()
    {
        var sys = NewSystem();
        var before = sys.CommandLog.Count;

        sys.SetWorkshopCapability(0,
            baseMarks: new List<string> { "圆罐", "方罐", "三角罐" },
            ban: new List<string> { "酒" });

        Assert.Equal(before + 1, sys.CommandLog.Count);
        Assert.Equal(WorldOps.SetWorkshopCapability, sys.CommandLog[sys.CommandLog.Count - 1].Op);
        // 改完世界必须仍然自洽：这是"只换规则参数、不动资源"的直接证据。
        Assert.Empty(WorldInvariants.Check(sys));
    }

    [Fact]
    public void ARejectedChangeLeavesBothTheWorldAndTheLogUntouched()
    {
        var sys = NewSystem();
        var beforeLog = sys.CommandLog.Count;
        var beforeBase = sys.Workshops[0].Capability.BaseMarks.ToList();

        Assert.Throws<DomainValidationException>(() =>
            sys.SetWorkshopCapability(0, baseMarks: new List<string> { "香蕉罐" }));

        Assert.Equal(beforeLog, sys.CommandLog.Count);                 // 日志里不留"没成功的意图"
        Assert.Equal(beforeBase, sys.Workshops[0].Capability.BaseMarks.ToList());  // 世界没被改
    }
}

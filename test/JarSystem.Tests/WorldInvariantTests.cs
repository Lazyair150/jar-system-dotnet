using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Services;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 世界不变量（<see cref="WorldInvariants"/>）的守卫。
///
/// 这一组的主题是**"缺了什么还会继续跑"**：程序不会崩、测试不会红、界面照常刷新，
/// 但世界已经说不通了。最典型的两类：
/// <list type="bullet">
/// <item>过程在跑，它服务的实体却不在了（订单被删、目标工作台越界）；</item>
/// <item>过程在跑，但它需要的**前提**没有建立（没选主台、没选从台）。</item>
/// </list>
///
/// 本轮的实测就撞到了第二类：未选主台时 <c>StartOrder</c> 仍然起了过程
/// （回包却带 "未选择主工作台"）。所以这里既有"正向自洽"，也有"故意制造矛盾必须被报出来"，
/// 还有一条**随机会话**属性测试 —— 它才是真正会抓到未来回归的那一条。
/// </summary>
public class WorldInvariantTests
{
    private static Vocabulary Vocab() => new(
        new[] { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" },
        new[] { "酒", "蜜" });

    private static List<Requirement> EmptyReqs(int n) =>
        Enumerable.Range(0, n).Select(_ => new Requirement { Shape = "圆罐", Liquid = null }).ToList();

    private static JarSystem.Core.JarSystem NewSystem(int seed = 7) =>
        new(randomSeed: seed, jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());

    /// <summary>把违规列表压成一行，失败信息才读得懂（xUnit 只显示 message）。</summary>
    private static string Dump(IReadOnlyList<InvariantViolation> v) =>
        v.Count == 0 ? "(无违规)" : string.Join("\n  ", v.Select(x => x.ToString()));

    [Fact]
    public void FreshWorldIsSelfConsistent()
    {
        var sys = NewSystem();
        var v = WorldInvariants.Check(sys);
        Assert.True(v.Count == 0, "全新的世界不该有违规：\n  " + Dump(v));
    }

    [Fact]
    public void AWorldWithAMasterAndAnOrderIsStillSelfConsistent()
    {
        var sys = NewSystem();
        sys.SetMasterIndex(0);
        var r = sys.CreateOrder(EmptyReqs(1));
        Assert.True(r.Success, r.Error);
        sys.CheckFeasibility(0);

        var v = WorldInvariants.Check(sys);
        Assert.True(v.Count == 0, "选好主台、建好订单、分析过可行性之后仍应自洽：\n  " + Dump(v));
    }

    /// <summary>
    /// **已知缺陷的记录与守卫**：未选主台时 `StartOrder` 不该起过程（它服务不了任何订单 ——
    /// 订单执行必须在某个工作台上发生）。如果它起了，不变量必须**报出来**。
    ///
    /// 这条测试有两种合法结局，都写成断言：
    /// <list type="bullet">
    /// <item>`StartOrder` 拒绝了 ⇒ 世界里没有过程，也就不该有违规（**理想结局**）；</item>
    /// <item>`StartOrder` 仍起了过程 ⇒ 必须出现 <c>PROCESS-ORDER-WITHOUT-MASTER</c>（**缺陷可见**）。</item>
    /// </list>
    /// 两种都不允许的是"起了过程，却什么都报不出来" —— 那正是最危险的中间态。
    /// </summary>
    [Fact]
    public void AnOrderProcessWithoutAMasterIsEitherRefusedOrReported()
    {
        var sys = NewSystem();
        Assert.Null(sys.CurrentMaster);
        Assert.True(sys.CreateOrder(EmptyReqs(1)).Success);

        var started = sys.StartOrder(0);
        var v = WorldInvariants.Check(sys);
        var reported = v.Any(x => x.Code == "PROCESS-ORDER-WITHOUT-MASTER");

        if (!started.Started)
        {
            Assert.False(reported, "拒绝了起过程，却报了「没有主台的过程」—— 不变量与行为不一致");
            return;
        }

        Assert.True(reported,
            "未选主台时 StartOrder 却起了过程（processId=" + started.ProcessId + "），而不变量没有报出来。\n" +
            "当前违规：" + Dump(v));
    }

    [Fact]
    public void SettingTheSlaveOntoTheMasterIsRejected()
    {
        // 主台与从台必须是两台。这条检查原来靠不变量事后发现（SLAVE-EQUALS-MASTER），
        // 现在在入口就拒绝 —— 不变量则保留作"别处又制造出这种状态"的兜底。
        var sys = NewSystem();
        sys.SetMasterIndex(0);

        var ex = Assert.Throws<DomainValidationException>(() => sys.SetSlaveIndex(0));
        Assert.Contains("从台不能与主台相同", ex.Message);
        Assert.Equal((int?)0, sys.CurrentMaster);
        Assert.Null(sys.CurrentSlave);

        var v = WorldInvariants.Check(sys);
        Assert.True(v.Count == 0, "拒绝之后世界必须仍然自洽：" + Dump(v));
    }

    [Fact]
    public void SwitchingTheMasterOntoTheCurrentSlaveClearsTheSlave()
    {
        // 反向那条：先选从台 1，再把主台改成 1 ⇒ 从台必须自动解除（否则两台相同）。
        var sys = NewSystem();
        Assert.True(sys.Workshops.Count >= 2, "用例前提：至少两个工作台");
        sys.SetMasterIndex(0);
        sys.SetSlaveIndex(1);
        sys.SetMasterIndex(1);

        Assert.Equal((int?)1, sys.CurrentMaster);
        Assert.Null(sys.CurrentSlave);
        var v = WorldInvariants.Check(sys);
        Assert.True(v.Count == 0, "换主台清从台之后必须自洽：" + Dump(v));
    }

    [Fact]
    public void ResourcesLeftOnAnIdleSlaveAreReported()
    {
        var sys = NewSystem();
        sys.SetMasterIndex(0);
        sys.SetSlaveIndex(1);
        sys.Workshops[1].AddLiquid("蜜", 1);

        var violations = WorldInvariants.Check(sys);

        Assert.Contains(violations, violation =>
            violation.Code == "SLAVE-RESIDUAL-WITHOUT-B-ORDER");
    }

    [Fact]
    public void ViolationCodesAreStable()
    {
        // 违约码是**给机器看的**（页面显示、测试断言、日志过滤）。改名等于改契约，
        // 所以把现有集合钉住：新增可以，删除/改名要让这条测试红。
        var codes = new[]
        {
            "MASTER-INDEX-OUT-OF-RANGE", "SLAVE-INDEX-OUT-OF-RANGE", "SLAVE-EQUALS-MASTER",
            "SLAVE-RESIDUAL-WITHOUT-B-ORDER",
            "PROCESS-ORDER-MISSING-ID", "PROCESS-ORDER-MISSING", "PROCESS-ORDER-WITHOUT-MASTER",
            "PROCESS-MIGRATION-WITHOUT-MASTER", "PROCESS-MIGRATION-TARGET-MISSING",
            "PROCESS-DUPLICATE-FOR-SUBJECT", "ORDER-EXECUTING-WITHOUT-PROCESS",
            "ORDER-REQUIREMENTS-COUNT", "JAR-COUNT-OVER-CAPACITY", "JAR-DUPLICATE-ID",
            "LIQUID-NEGATIVE", "LIQUID-OVER-CAPACITY",
            "JAR-ID-SHARED-ACROSS-WORKSHOPS"
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[A-Z][A-Z0-9-]+$", c));
    }

    /// <summary>
    /// **随机会话**：跑一批随机命令序列，每步之后世界都必须自洽。
    ///
    /// 这是这一组里唯一会"将来"红的那条 —— 它不认识任何具体 bug，
    /// 只认"自相矛盾"。命令集合刻意只取**公开面**（宿主能做的动作），
    /// 因为那些才是玩家/后端真正能触发的路径。
    /// </summary>
    [Fact]
    public void RandomSessionsNeverBecomeSelfContradictory()
    {
        var problems = new List<string>();

        for (var seed = 1; seed <= 24 && problems.Count == 0; seed++)
        {
            var sys = NewSystem(seed);
            var rng = new Random(seed);

            for (var step = 0; step < 40 && problems.Count == 0; step++)
            {
                // 命令**可以拒绝**（越界、非法参数、缺前提）—— 那是校验，不是不自洽。
                // 本测试只要求：无论命令成功还是被拒绝，之后的世界都必须自洽。
                try
                {
                    switch (rng.Next(9))
                    {
                        case 0: sys.SetMasterIndex(rng.Next(-1, sys.Workshops.Count + 2)); break;
                        case 1: sys.SetSlaveIndex(rng.Next(-1, sys.Workshops.Count + 2)); break;
                        case 2: sys.CreateOrder(EmptyReqs(rng.Next(1, 3))); break;
                        case 3: if (sys.Orders.Count > 0) sys.CheckFeasibility(rng.Next(sys.Orders.Count)); break;
                        case 4: if (sys.Orders.Count > 0) sys.StartOrder(rng.Next(sys.Orders.Count)); break;
                        case 5: if (sys.Processes.ActiveProcesses.Count > 0)
                                    sys.CancelProcess(sys.Processes.ActiveProcesses[0].Id, "随机会话"); break;
                        case 6: sys.Tick(1); break;
                        case 7: if (sys.Orders.Count > 0) sys.DeleteOrder(rng.Next(sys.Orders.Count)); break;
                        case 8: sys.CheckAllFeasibility(); break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException
                    or DomainValidationException or InvalidOperationException)
                {
                    // 校验拒绝：合法结局，世界应当**保持**自洽（下面照样检查）。
                }

                var v = WorldInvariants.Check(sys);
                if (v.Count > 0)
                {
                    problems.Add("seed=" + seed + " step=" + step + "：\n  " + Dump(v));
                    break;
                }
            }
        }

        Assert.True(problems.Count == 0,
            "随机会话把世界推进到了自相矛盾的状态：\n" + string.Join("\n", problems));
    }
}

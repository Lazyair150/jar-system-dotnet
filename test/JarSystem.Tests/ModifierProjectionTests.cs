using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Rules;
using JarSystem.Core.Solver;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **修饰器投影的健全性**：投影必须是"原规则的**精确**表达"，不许把"说不准"折成"不可行"。
///
/// <para>要守的是什么：<see cref="ModifierAwareSolver"/> 在求解前会把修饰器"折进输入"
/// （被抑制的液体视作被禁、被授予的形状视作附加标记、被禁的形状从池里移除）。
/// 这个折叠只有在**两种说法等价**时才成立。一旦某个修饰器只作用于操作的一部分，
/// 「把这种液体整体视作被禁」就会**多禁**——于是求解器报"不可行"，而真正执行是能做的。</para>
///
/// <para>为什么值得单独守：它与史上那 105 例错杀是同一个形状——把一条不完整的推理当成完备的，然后据此否定。
/// 而且它不会让任何既有测试变红，只会让"自动检验"在某类订单上凭空说不可行。</para>
/// </summary>
public class ModifierProjectionTests
{
    private static WorkshopCapability Capability() =>
        new(new[] { "圆罐", "方罐" }, new[] { "三角罐" }, System.Array.Empty<string>());

    private static ModifierStack SuppressPourInOfWine()
    {
        var stack = new ModifierStack();
        stack.Push(new SuppressModifier
        {
            Name = "此刻不得往罐里灌酒",
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "此刻不得灌酒"
        });
        return stack;
    }

    /// <summary>
    /// 真执行是可行的：圆罐不在基础标记里，但三角罐可以改形到圆罐之外的**方罐**，
    /// 而满罐改形只用罐内的酒做自耗——酒只是在"灌入"上被抑制，没有被禁。
    /// </summary>
    [Fact]
    public void PourInOnlySuppressionStillAllowsFullReshapeUsingThatLiquid()
    {
        var stack = SuppressPourInOfWine();
        var workshop = new Workshop("自由台", new List<string> { "圆罐", "方罐" },
            new List<string> { "三角罐" }, new List<string>(), maxJars: 8, maxLiquids: 3,
            modifiers: stack);
        var jar = new Jar("三角罐", "酒");
        Assert.True(workshop.AddJar(jar), "罐子放不进工作台");

        var (ok, reason) = workshop.TryReshape(jar, "方罐", ReshapeMode.Full);
        Assert.True(ok, "真执行应当允许满罐改形（抑制只针对灌入）：" + reason);
        Assert.Equal("方罐", jar.Shape);
        Assert.Null(jar.Liquid);
    }

    /// <summary>
    /// 所以求解器**不许**说"不可行"。它只有两种合法答案：
    /// <list type="bullet">
    /// <item>精确表达（本可用例做不到，因为求解器只有"整种液体被禁"这一种投影）；</item>
    /// <item>如实说"未判定"（<c>BudgetExceeded</c> + <c>InconclusiveReason</c> 指到那条规则）。</item>
    /// </list>
    /// 「说不可行」是被明确禁止的：那是把一条不完整的推理当成完备的，然后据此否定。
    /// </summary>
    [Fact]
    public void SolverNeverCallsAnInexpressibleRuleInfeasible()
    {
        var stack = SuppressPourInOfWine();
        var solver = new ModifierAwareSolver(new StateSpaceSolver(), stack);
        solver.BeginCheck(400_000);

        var requirements = new List<Requirement> { new() { Shape = "方罐", Liquid = null } };
        var info = new WorkshopInfo { Capability = Capability(), MaxJars = 8, MaxLiquids = 3 };
        var jars = new List<JarState> { new() { Shape = "三角罐", Liquid = "酒" } };

        var result = solver.Solve(requirements, info, jars, new Dictionary<string, int>());

        Assert.True(solver.BudgetExceeded || result.BudgetExceeded,
            "只抑制一种操作的规则无法折进求解器输入，必须如实回未判定，而不是给一个结论");
        Assert.Contains("此刻不得往罐里灌酒", solver.InconclusiveReason ?? "");
        Assert.False(result.Feasible);
    }

    /// <summary>
    /// 反向：整体抑制（<c>Liquid</c> 且不限操作）时，把它折成"被禁"是**精确**的，应当维持原有能力。
    /// </summary>
    [Fact]
    public void WholeLiquidSuppressionIsProjectedExactly()
    {
        var stack = new ModifierStack();
        stack.Push(new SuppressModifier { Name = "此刻酒不可用", Liquid = "酒", Reason = "此刻酒不可用" });
        var solver = new ModifierAwareSolver(new StateSpaceSolver(), stack);
        solver.BeginCheck(400_000);

        var requirements = new List<Requirement> { new() { Shape = "三角罐", Liquid = null } };
        var info = new WorkshopInfo { Capability = Capability(), MaxJars = 8, MaxLiquids = 3 };
        var jars = new List<JarState> { new() { Shape = "三角罐", Liquid = "酒" } };

        var result = solver.Solve(requirements, info, jars, new Dictionary<string, int>());

        Assert.False(result.BudgetExceeded);
        Assert.False(result.Feasible, "满罐里的酒被整体禁止时，连自耗改形都不该被允许");
    }

    /// <summary>
    /// **禁令与抑制是两条不同的通道**，这条测试把区别钉死。
    ///
    /// <para>禁令（<c>WorkshopCapability.Ban</c>，基础法则）让一种液体在**所有**用法上都不可用——
    /// 包括倒出、作空罐改形的成本、以及满罐改形的自耗。所以"这种液体被禁"与"这种液体整种不可用"
    /// 是同一件事，把它折进求解器输入是**精确**的（这正是 <c>Operation is null</c> 那条分支的依据）。</para>
    ///
    /// <para>抑制（<c>SuppressModifier</c>，修饰器）只封它点名的那个操作：<c>Workshop.IsBanned</c>
    /// 走的是 <c>_capability.IsBanned</c>，**完全不读修饰器栈**。所以"只抑制灌入"绝不能折成"被禁"。</para>
    ///
    /// <para>为什么必须单独守：这两者的差别只写在两处代码里（<c>Workshop.cs:119</c> 的委托链与
    /// <c>ModifierStack.cs:89</c> 的操作过滤）。将来若有人把 <c>IsBanned</c> 改成"也看修饰器栈"，
    /// 或把抑制实现成"往 Ban 里加一项"，两个概念就会被合并，而既有测试全都还是绿的。</para>
    /// </summary>
    [Fact]
    public void BanBlocksStagingFlowsButNotJarInternalSelfConsumeWhileSuppressionIsOperationScoped()
    {
        // 同形状配置、同罐子；唯一差别是 Ban 列表里有没有酒。
        static Workshop Make(List<string> ban) => new("自由台", new List<string> { "圆罐", "方罐" },
            new List<string> { "三角罐" }, ban, maxJars: 8, maxLiquids: 3);

        static (bool Reshape, bool PourOut, string? ReshapeReason) Run(List<string> ban)
        {
            var w = Make(ban);
            var a = new Jar("三角罐", "酒");
            var b = new Jar("三角罐", "酒");
            Assert.True(w.AddJar(a), "罐子放不进工作台");
            Assert.True(w.AddJar(b), "罐子放不进工作台");
            var (reshapeOk, reshapeReason) = w.TryReshape(a, "方罐", ReshapeMode.Full);
            var (pourOutOk, _, _) = w.PourOut(b);
            return (reshapeOk, pourOutOk, reshapeReason);
        }

        var free = Run(new List<string>());
        Assert.True(free.Reshape, "不禁酒时，满罐改形自耗罐内的酒应当合法（否则这条测试的对照就立不住）");
        Assert.True(free.PourOut, "不禁酒时，倒出应当合法");

        var banned = Run(new List<string> { "酒" });
        Assert.True(banned.Reshape, "Jar 内部液体自耗不读取工作台 staging Ban");
        Assert.True(string.IsNullOrEmpty(banned.ReshapeReason));
        Assert.False(banned.PourOut, "倒出会把液体送入 staging，必须受 Ban 约束");
    }
}

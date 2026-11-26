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
    /// Ban is a free-liquid staging policy, not a Jar polarity or operation policy.
    /// Modifier suppression remains independently scoped to the named operation.
    /// </summary>
    [Fact]
    public void BanDoesNotDefineJarPolarityOrSuppressJarOperations()
    {
        static Workshop Make(List<string> ban) => new("自由台", new List<string> { "圆罐", "方罐" },
            new List<string> { "三角罐" }, ban, maxJars: 8, maxLiquids: 3);

        var unrestricted = Make(new List<string>());
        var banned = Make(new List<string> { "酒" });
        Assert.Equal(WorkshopPolarityMode.Unrestricted, unrestricted.Capability.PolarityMode);
        Assert.Equal(WorkshopPolarityMode.Unrestricted, banned.Capability.PolarityMode);

        var jar = new Jar("三角罐", "酒");
        Assert.True(banned.AddJar(jar));
        Assert.True(WorkshopRulebook.IsAllowed(
            banned.Rules, banned.MaxLiquids, JarFacts.Of(jar), WorkshopOperation.PourOut(),
            banned.LiquidStorage));
        var reshape = new ReshapeRules(banned.Capability, new LiquidRules(banned.Capability, banned.MaxLiquids));
        Assert.True(reshape.SelfConsume("酒").Granted);
    }

}

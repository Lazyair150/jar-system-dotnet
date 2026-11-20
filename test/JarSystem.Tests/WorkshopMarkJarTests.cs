using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class WorkshopMarkJarTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static Workshop CreateWorkshop(string name) =>
        new(name, new List<string> { "圆罐", "方罐" },
            ids: new SequentialIdGenerator(), vocabulary: Words);

    [Fact]
    public void MarkJarMarksOnlyAJarOwnedByThisWorkshop()
    {
        var workshop = CreateWorkshop("主台");
        var jar = new Jar("圆罐", vocabulary: Words);
        workshop.AddJar(jar);

        var result = workshop.MarkJar(jar);

        Assert.True(result.Success);
        Assert.Null(result.Reason);
        Assert.True(jar.IsMarked);
    }

    [Fact]
    public void MarkJarRejectsJarOwnedByAnotherWorkshopWithoutMutation()
    {
        var owner = CreateWorkshop("主台");
        var other = CreateWorkshop("从台");
        var jar = new Jar("圆罐", vocabulary: Words);
        owner.AddJar(jar);

        var result = other.MarkJar(jar);

        Assert.False(result.Success);
        Assert.Contains("不属于", result.Reason);
        Assert.False(jar.IsMarked);
        Assert.Single(owner.Jars);
        Assert.Empty(other.Jars);
    }

    [Fact]
    public void MarkJarRejectsAlreadyMarkedJarWithoutChangingIt()
    {
        var workshop = CreateWorkshop("主台");
        var jar = new Jar("圆罐", vocabulary: Words);
        workshop.AddJar(jar);

        Assert.True(workshop.MarkJar(jar).Success);
        var result = workshop.MarkJar(jar);

        Assert.False(result.Success);
        Assert.Contains("已被操作", result.Reason);
        Assert.True(jar.IsMarked);
    }
}

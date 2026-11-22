using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.World.Execution;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldPlanStepApplierTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static Workshop CreateWorkshop()
    {
        return new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxJars: 8, maxLiquids: 8,
            ids: new SequentialIdGenerator(), vocabulary: Words);
    }

    private static WorldPlanStep Step(
        Jar jar, string type, string? targetShape = null, string? targetLiquid = null,
        string? costLiquid = null, bool fullReshape = false,
        string? afterShape = null, string? afterLiquid = null,
        Dictionary<string, int>? consumed = null)
    {
        var before = new WorldPlanJarState(jar.Id, jar.Shape, jar.Liquid, false, false);
        var after = new WorldPlanJarState(jar.Id, afterShape ?? jar.Shape, afterLiquid, true, false);
        var operation = new WorldPlanOperation(
            type, type, 0, targetShape, targetLiquid, costLiquid,
            consumed ?? new Dictionary<string, int>(), fullReshape);
        return new WorldPlanStep(1, null, null, operation,
            new[] { before }, new[] { after },
            new Dictionary<string, int>());
    }

    [Fact]
    public void PourInUsesTheStableJarIdAndConsumesExactlyOneFreeLiquid()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", vocabulary: Words);
        workshop.AddJar(jar);
        workshop.AddLiquid("酒", 2);
        var step = Step(jar, "POUR_IN", targetLiquid: "酒",
            afterLiquid: "酒", consumed: new Dictionary<string, int> { ["酒"] = 1 });

        var result = new WorldPlanStepApplier().Apply(workshop, step);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(jar.Id, result.JarId);
        Assert.Equal("酒", jar.Liquid);
        Assert.True(jar.IsMarked);
        Assert.Equal(1, workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void PourOutReturnsLiquidToTheFreePoolAlthoughConsumedIsEmpty()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", "酒", vocabulary: Words);
        workshop.AddJar(jar);
        var step = Step(jar, "POUR_OUT", afterLiquid: null);

        var result = new WorldPlanStepApplier().Apply(workshop, step);

        Assert.True(result.Success, result.Reason);
        Assert.Null(jar.Liquid);
        Assert.True(jar.IsMarked);
        Assert.Equal(1, workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void EmptyReshapeConsumesTheNamedFreeLiquid()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", vocabulary: Words);
        workshop.AddJar(jar);
        workshop.AddLiquid("蜜", 2);
        var step = Step(jar, "RESHAPE", targetShape: "方罐",
            costLiquid: "蜜", afterShape: "方罐",
            consumed: new Dictionary<string, int> { ["蜜"] = 1 });

        var result = new WorldPlanStepApplier().Apply(workshop, step);

        Assert.True(result.Success, result.Reason);
        Assert.Equal("方罐", jar.Shape);
        Assert.True(jar.IsMarked);
        Assert.Equal(1, workshop.LiquidStorage["蜜"]);
    }

    [Fact]
    public void FullReshapeConsumesJarLiquidButDoesNotDebitFreePool()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", "酒", vocabulary: Words);
        workshop.AddJar(jar);
        workshop.AddLiquid("酒", 2);
        var step = Step(jar, "RESHAPE", targetShape: "方罐",
            costLiquid: "酒", fullReshape: true,
            afterShape: "方罐", afterLiquid: null,
            consumed: new Dictionary<string, int> { ["酒"] = 1 });

        var result = new WorldPlanStepApplier().Apply(workshop, step);

        Assert.True(result.Success, result.Reason);
        Assert.Equal("方罐", jar.Shape);
        Assert.Null(jar.Liquid);
        Assert.True(jar.IsMarked);
        Assert.Equal(2, workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void StaleExpectedStateIsRejectedWithoutChangingAnyResource()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", vocabulary: Words);
        workshop.AddJar(jar);
        workshop.AddLiquid("酒", 1);
        var step = Step(jar, "POUR_IN", targetLiquid: "酒",
            afterLiquid: "酒", consumed: new Dictionary<string, int> { ["酒"] = 1 });

        // 计划声称目标罐子原本是方罐，但真实罐子仍是圆罐。
        var before = new WorldPlanJarState(jar.Id, "方罐", null, false, false);
        var after = new WorldPlanJarState(jar.Id, "方罐", "酒", true, false);
        step = step with { Before = new[] { before }, After = new[] { after } };

        var result = new WorldPlanStepApplier().Apply(workshop, step);

        Assert.False(result.Success);
        Assert.Contains("执行前状态", result.Reason);
        Assert.Null(jar.Liquid);
        Assert.False(jar.IsMarked);
        Assert.Equal(1, workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void ConsumedMustMatchTheOperationAccountingBeforeMutation()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", vocabulary: Words);
        workshop.AddJar(jar);
        workshop.AddLiquid("酒", 1);
        var step = Step(jar, "POUR_IN", targetLiquid: "酒",
            afterLiquid: "酒", consumed: new Dictionary<string, int> { ["蜜"] = 1 });

        var result = new WorldPlanStepApplier().Apply(workshop, step);

        Assert.False(result.Success);
        Assert.Contains("Consumed", result.Reason);
        Assert.Null(jar.Liquid);
        Assert.False(jar.IsMarked);
        Assert.Equal(1, workshop.LiquidStorage["酒"]);
    }
    [Fact]
    public void MatchMarksTheSelectedJarWithoutChangingItsContents()
    {
        var workshop = CreateWorkshop();
        var jar = new Jar("圆罐", "酒", vocabulary: Words);
        workshop.AddJar(jar);
        var step = Step(jar, "MATCH", afterLiquid: "酒");

        var result = new WorldPlanStepApplier().Apply(workshop, step);

        Assert.True(result.Success, result.Reason);
        Assert.True(jar.IsMarked);
        Assert.Equal("圆罐", jar.Shape);
        Assert.Equal("酒", jar.Liquid);
    }

}

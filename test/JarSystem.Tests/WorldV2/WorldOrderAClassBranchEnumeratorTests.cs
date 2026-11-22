using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.World;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderAClassBranchEnumeratorTests
{
    private static WorldOrderAClassBranchEnumerationResult Solve(
        Workshop master,
        IReadOnlyList<Requirement> requirements,
        int maxBranches = 4096,
        int maxNodes = 100_000)
    {
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(new[] { master }, master));
        var specification = new JarOrderSpecification(requirements, "A");
        var profile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        return WorldOrderAClassBranchEnumerator.Enumerate(
            701, profile, world, maxBranches, maxNodes);
    }

    [Fact]
    public void ProducerWithHigherRequirementIndexRunsBeforeConsumer()
    {
        var master = new Workshop("主台",
            new List<string> { "圆罐", "方罐" });
        master.AddJar(new Jar("圆罐", id: 7101));
        master.AddJar(new Jar("方罐", "酒", id: 7102));

        var result = Solve(master, new[]
        {
            new Requirement { Shape = "圆罐", Liquid = "酒" },
            new Requirement { Shape = "方罐" }
        });

        Assert.Equal(WorldOrderAClassBranchEnumerationStatus.Feasible, result.Status);
        Assert.NotNull(result.Assembly);
        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Assembly!.Status);
        var steps = result.Assembly.CandidatePlan!.MasterSteps;
        Assert.Equal(new[] { "POUR_OUT", "POUR_IN" },
            steps.Select(x => x.Operation!.Type));
        Assert.Equal(0, result.Assembly.Manifest!.MasterInitialLiquidAmounts.GetValueOrDefault("蜜"));
        Assert.Equal(0, result.Assembly.Manifest.MasterFinalLiquidBalances.GetValueOrDefault("蜜"));
        Assert.Empty(result.Assembly.Manifest.TransferLiquids);
        Assert.Equal(7102, steps[0].Before![steps[0].Operation!.JarIndex!.Value].Id);
        Assert.Equal(7101, steps[1].Before![steps[1].Operation!.JarIndex!.Value].Id);
    }

    [Fact]
    public void IndependentPreparationJarCanFundARequirementJar()
    {
        var modifiers = new ModifierStack();
        modifiers.Push(new SuppressModifier
        {
            Operation = "RESHAPE_FULL",
            Reason = "测试：禁止直接满罐改形"
        });
        var master = new Workshop("主台",
            new List<string> { "圆罐", "方罐" }, modifiers: modifiers);
        master.AddJar(new Jar("圆罐", id: 7201));
        master.AddJar(new Jar("圆罐", "蜜", id: 7202));

        var result = Solve(master, new[]
        {
            new Requirement { Shape = "方罐" }
        });

        Assert.Equal(WorldOrderAClassBranchEnumerationStatus.Feasible, result.Status);
        Assert.NotNull(result.Assembly);
        var steps = result.Assembly!.CandidatePlan!.MasterSteps;
        Assert.Equal(new[] { "POUR_OUT", "RESHAPE" },
            steps.Select(x => x.Operation!.Type));
        Assert.Equal(7202, steps[0].Before![steps[0].Operation!.JarIndex!.Value].Id);
        Assert.Equal(7201, steps[1].Before![steps[1].Operation!.JarIndex!.Value].Id);
        Assert.Equal(1, steps[0].AfterLiquids.GetValueOrDefault("蜜"));
        Assert.Equal(0, steps[1].AfterLiquids.GetValueOrDefault("蜜"));
        Assert.Equal(0, result.Assembly.Manifest!.MasterInitialLiquidAmounts.GetValueOrDefault("酒"));
        Assert.Equal(0, result.Assembly.Manifest.MasterFinalLiquidBalances.GetValueOrDefault("酒"));
    }

    [Fact]
    public void MasterBanBlocksFullJarSelfConsumeAtSolverCandidateBoundary()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", "酒", id: 7301));

        var result = Solve(master, new[]
        {
            new Requirement { Shape = "方罐" }
        });

        Assert.Equal(WorldOrderAClassBranchEnumerationStatus.Infeasible, result.Status);
        Assert.Null(result.Assembly);
    }

    [Fact]
    public void BudgetExhaustionIsUndeterminedRatherThanInfeasible()
    {
        var master = new Workshop("主台",
            new List<string> { "圆罐", "方罐" });
        master.AddJar(new Jar("圆罐", id: 7401));
        master.AddJar(new Jar("方罐", "酒", id: 7402));

        var result = Solve(master, new[]
        {
            new Requirement { Shape = "圆罐", Liquid = "酒" },
            new Requirement { Shape = "方罐" }
        }, maxBranches: 1, maxNodes: 1);

        Assert.Equal(WorldOrderAClassBranchEnumerationStatus.Undetermined, result.Status);
    }
}

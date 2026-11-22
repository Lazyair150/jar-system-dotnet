using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Orders;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderSolvePlanTests
{
    [Fact]
    public void SinglePhasePlanDeepCopiesMutableSolverPath()
    {
        var consumed = new Dictionary<string, int> { ["酒"] = 2 };
        var after = new List<JarState>
        {
            new() { Id = 7, Shape = "方罐", Liquid = "酒", IsMarked = true }
        };
        var source = new SolverStep
        {
            Step = 1,
            Requirement = new Requirement { Shape = "方罐", Liquid = "酒" },
            Operation = new SolverOperation
            {
                Type = "RESHAPE",
                JarIndex = 0,
                TargetShape = "方罐",
                Consumed = consumed
            },
            Before = new List<JarState> { new() { Id = 7, Shape = "圆罐", IsMarked = false } },
            After = after,
            AfterLiquids = new Dictionary<string, int> { ["酒"] = 3 }
        };

        var plan = WorldOrderSolvePlan.SinglePhase(new[] { source });

        source.Operation.Type = "MUTATED";
        source.After[0].Shape = "被篡改";
        consumed["酒"] = 99;
        after.Clear();

        var step = Assert.Single(plan.MasterSteps);
        Assert.Equal("RESHAPE", step.Operation!.Type);
        Assert.Equal("方罐", Assert.Single(step.After).Shape);
        Assert.Equal(2, step.Operation.Consumed["酒"]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<WorldPlanStep>)plan.MasterSteps).Add(step));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, int>)step.Operation.Consumed)["酒"] = 100);
    }

    [Fact]
    public void SinglePhasePlanRejectsTwoOperationsOnTheSameJar()
    {
        static SolverStep Step(int number, string targetShape) => new()
        {
            Step = number,
            Operation = new SolverOperation
            {
                Type = "RESHAPE",
                JarIndex = 0,
                TargetShape = targetShape,
                Consumed = new Dictionary<string, int> { ["酒"] = 1 }
            },
            Before = new List<JarState> { new() { Id = 7, Shape = "圆罐" } },
            After = new List<JarState> { new() { Id = 7, Shape = targetShape, IsMarked = true } },
            AfterLiquids = new Dictionary<string, int>()
        };

        Assert.Throws<System.ArgumentException>(() =>
            WorldOrderSolvePlan.SinglePhase(new[] { Step(0, "方罐"), Step(1, "三角罐") }));
    }

    [Fact]
    public void PartitionedPlanRejectsReusingAMasterOperatedJarOnSlave()
    {
        static SolverStep Step(string operation, string beforeShape, string afterShape) => new()
        {
            Operation = new SolverOperation
            {
                Type = operation,
                JarIndex = 0,
                TargetShape = afterShape,
                TargetLiquid = operation == "POUR_IN" ? "酒" : null,
                Consumed = operation == "POUR_IN"
                    ? new Dictionary<string, int> { ["酒"] = 1 }
                    : new Dictionary<string, int>()
            },
            Before = new List<JarState> { new() { Id = 7, Shape = beforeShape } },
            After = new List<JarState> { new() { Id = 7, Shape = afterShape, Liquid = operation == "POUR_IN" ? "酒" : null, IsMarked = true } },
            AfterLiquids = new Dictionary<string, int>()
        };

        var outcome = new PartitionSearchOutcome
        {
            MasterPath = new List<SolverStep> { Step("POUR_OUT", "圆罐", "圆罐") },
            SlavePath = new List<SolverStep> { Step("POUR_IN", "圆罐", "圆罐") },
            TransferJarIds = new List<int> { 7 },
            TransferLiquids = new Dictionary<string, int>()
        };

        Assert.Throws<System.ArgumentException>(() => WorldOrderSolvePlan.Partitioned(outcome));
    }

    [Fact]
    public void PartitionedPlanCopiesBothPhasesAndTransferLedger()
    {
        var transferLiquids = new Dictionary<string, int> { ["酒"] = 4 };
        var outcome = new PartitionSearchOutcome
        {
            MasterPath = new List<SolverStep>
            {
                new() { Step = 1, Operation = new SolverOperation { Type = "MATCH" } }
            },
            SlavePath = new List<SolverStep>
            {
                new() { Step = 1, Operation = new SolverOperation { Type = "POUR_IN" } }
            },
            TransferJarIds = new List<int> { 7, 9 },
            TransferLiquids = transferLiquids
        };

        var plan = WorldOrderSolvePlan.Partitioned(outcome);

        outcome.TransferJarIds.Clear();
        transferLiquids["酒"] = 100;
        outcome.SlavePath[0].Operation!.Type = "MUTATED";

        Assert.True(plan.IsPartitioned);
        Assert.Single(plan.MasterSteps);
        Assert.Equal("POUR_IN", Assert.Single(plan.SlaveSteps).Operation!.Type);
        Assert.Equal(new[] { 7, 9 }, plan.TransferJarIds);
        Assert.Equal(4, plan.TransferLiquids["酒"]);
    }
}

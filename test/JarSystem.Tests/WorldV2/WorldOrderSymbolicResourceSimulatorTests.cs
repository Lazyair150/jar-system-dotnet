using System;
using System.Collections.Generic;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderSymbolicResourceSimulatorTests
{
    private static WorldOrderRuleCandidate Candidate(
        int requirementIndex,
        string sourceShape,
        string? sourceLiquid,
        string operation,
        string targetShape,
        string? targetLiquid,
        string? consumedFree = null,
        string? producedFree = null,
        string? consumedJar = null) =>
        new(
            requirementIndex, sourceShape, sourceLiquid, operation,
            "test.rule", 1, targetShape, targetLiquid,
            null, consumedFree, producedFree, consumedJar);

    private static WorldOrderSymbolicOperationProgram BProgram() =>
        WorldOrderSymbolicOperationProgramBuilder.BuildDirectCandidateProjection(
            new WorldOrderSymbolicSolution(new[]
            {
                new WorldOrderAssignedRuleCandidate(
                    "Master", Candidate(0, "圆罐", null, "MASTER_RULE", "方罐", null, producedFree: "酒")),
                new WorldOrderAssignedRuleCandidate(
                    "Slave", Candidate(1, "圆罐", null, "SLAVE_RULE", "三角罐", null, consumedFree: "酒"))
            }),
            true, "Master", "Slave");

    private static WorldOrderSymbolicOperationProgram EmptyBProgram()
    {
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 0, "Master", "Slave", Array.Empty<WorldOrderSymbolicJarVersion>());
        return new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { handoff },
            Array.Empty<WorldOrderSymbolicOperationDependency>());
    }

    [Fact]
    public void MasterProductionIsTransferredOnceAndConsumedBySlave()
    {
        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            BProgram(),
            new WorldOrderSymbolicResourceSimulationRequest(
                1, 1,
                new Dictionary<string, int>(),
                new Dictionary<string, int> { ["酒"] = 1 }));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Feasible, result.Status);
        Assert.True(result.HandoffApplied);
        Assert.Equal(1, result.HandoffTransferredFreeLiquid["酒"]);
        Assert.Empty(result.Roles["Master"].FinalFreeLiquid);
        Assert.Empty(result.Roles["Slave"].FinalFreeLiquid);
        Assert.Equal(1, result.Roles["Master"].AcceptedFreeLiquidProduction["酒"]);
        Assert.Equal(1, result.Roles["Master"].PeakStagingBalance);
        Assert.Equal(1, result.Roles["Slave"].PeakStagingBalance);
        Assert.Equal(1, result.Roles["Slave"].ConsumedFreeLiquid["酒"]);
    }

    [Fact]
    public void StagingCapacityIsSharedAcrossLiquidsAndOverflowIsDiscarded()
    {
        var firstInput = new WorldOrderSymbolicJarVersion("jar-1", 0, "Master", "圆罐", "酒");
        var firstOutput = new WorldOrderSymbolicJarVersion("jar-1", 1, "Master", "圆罐", null);
        var secondInput = new WorldOrderSymbolicJarVersion("jar-2", 0, "Master", "圆罐", "蜜");
        var secondOutput = new WorldOrderSymbolicJarVersion("jar-2", 1, "Master", "圆罐", null);
        var first = WorldOrderSymbolicOperationNode.Rule(
            "pour-out-wine", 0, "Master", null,
            Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒"),
            firstInput, firstOutput);
        var second = WorldOrderSymbolicOperationNode.Rule(
            "pour-out-honey", 1, "Master", null,
            Candidate(-1, "圆罐", "蜜", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "蜜"),
            secondInput, secondOutput);
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { first, second },
            Array.Empty<WorldOrderSymbolicOperationDependency>());

        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            program,
            new WorldOrderSymbolicResourceSimulationRequest(
                1, 0,
                new Dictionary<string, int>(),
                new Dictionary<string, int>()));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Feasible, result.Status);
        Assert.Equal(1, result.Roles["Master"].AcceptedFreeLiquidProduction["酒"]);
        Assert.Equal(1, result.Roles["Master"].DiscardedFreeLiquidProduction["蜜"]);
        Assert.Equal(1, result.Roles["Master"].PeakStagingBalance);
        Assert.Single(result.Roles);
    }

    [Fact]
    public void ProductionOverflowCannotBeUsedByTheLaterHandoff()
    {
        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            BProgram(),
            new WorldOrderSymbolicResourceSimulationRequest(
                0, 1,
                new Dictionary<string, int>(),
                new Dictionary<string, int> { ["酒"] = 1 }));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Infeasible, result.Status);
        Assert.Equal("master-to-slave-handoff", result.FailureNodeId);
        Assert.False(result.HandoffApplied);
        Assert.Empty(result.HandoffTransferredFreeLiquid);
        Assert.Equal(1, result.Roles["Master"].DiscardedFreeLiquidProduction["酒"]);
        Assert.Empty(result.Roles["Slave"].FinalFreeLiquid);
    }

    [Fact]
    public void HandoffCapacityFailureIsAtomicForBothRoleAccounts()
    {
        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            EmptyBProgram(),
            new WorldOrderSymbolicResourceSimulationRequest(
                2, 1,
                new Dictionary<string, int> { ["酒"] = 2 },
                new Dictionary<string, int> { ["酒"] = 2 }));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Infeasible, result.Status);
        Assert.False(result.HandoffApplied);
        Assert.Empty(result.HandoffTransferredFreeLiquid);
        Assert.Equal(2, result.Roles["Master"].FinalFreeLiquid["酒"]);
        Assert.Empty(result.Roles["Slave"].FinalFreeLiquid);
    }

    [Fact]
    public void HandoffInsufficientMasterBalanceDoesNotPartiallyTransfer()
    {
        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            EmptyBProgram(),
            new WorldOrderSymbolicResourceSimulationRequest(
                2, 2,
                new Dictionary<string, int> { ["酒"] = 1 },
                new Dictionary<string, int> { ["酒"] = 2 }));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Infeasible, result.Status);
        Assert.False(result.HandoffApplied);
        Assert.Empty(result.HandoffTransferredFreeLiquid);
        Assert.Equal(1, result.Roles["Master"].FinalFreeLiquid["酒"]);
        Assert.Empty(result.Roles["Slave"].FinalFreeLiquid);
    }

    [Fact]
    public void JarInternalLiquidConsumptionDoesNotConsumeFreeLiquidStaging()
    {
        var input = new WorldOrderSymbolicJarVersion("jar-A", 0, "Master", "圆罐", "酒");
        var output = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "方罐", null);
        var node = WorldOrderSymbolicOperationNode.Rule(
            "reshape-full", 0, "Master", 0,
            Candidate(0, "圆罐", "酒", "RESHAPE_FULL", "方罐", null, consumedJar: "酒"),
            input, output);
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { node },
            Array.Empty<WorldOrderSymbolicOperationDependency>());

        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            program,
            new WorldOrderSymbolicResourceSimulationRequest(
                0, 0,
                new Dictionary<string, int>(),
                new Dictionary<string, int>()));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Feasible, result.Status);
        Assert.Empty(result.Roles["Master"].ConsumedFreeLiquid);
        Assert.Equal(1, result.Roles["Master"].ConsumedJarLiquid["酒"]);
    }

    [Fact]
    public void AClassRejectsAnyHandoffLiquidVector()
    {
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", Array.Empty<WorldOrderSymbolicOperationNode>(),
            Array.Empty<WorldOrderSymbolicOperationDependency>());

        Assert.Throws<ArgumentException>(() =>
            WorldOrderSymbolicResourceSimulator.Simulate(
                program,
                new WorldOrderSymbolicResourceSimulationRequest(
                    1, 1,
                    new Dictionary<string, int>(),
                    new Dictionary<string, int> { ["酒"] = 1 })));
    }

    [Fact]
    public void MasterInitialBalanceMustFitItsAggregateStagingCapacity()
    {
        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicResourceSimulationRequest(
                1, 0,
                new Dictionary<string, int> { ["酒"] = 1, ["蜜"] = 1 },
                new Dictionary<string, int>()));
    }
    [Fact]
    public void RejectedProductionIsDiscardedAndCannotFundTheHandoff()
    {
        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            BProgram(),
            new WorldOrderSymbolicResourceSimulationRequest(
                1, 1,
                new Dictionary<string, int>(),
                new Dictionary<string, int> { ["酒"] = 1 },
                masterRejectedLiquids: new[] { "酒" }));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Infeasible, result.Status);
        Assert.Equal(1, result.Roles["Master"].RejectedFreeLiquidProduction["酒"]);
        Assert.Equal(1, result.Roles["Master"].DiscardedFreeLiquidProduction["酒"]);
        Assert.Empty(result.Roles["Master"].FinalFreeLiquid);
        Assert.False(result.HandoffApplied);
    }

    [Fact]
    public void SlaveRejectingTheTransferredLiquidFailsTheWholeHandoffAtomically()
    {
        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            EmptyBProgram(),
            new WorldOrderSymbolicResourceSimulationRequest(
                1, 1,
                new Dictionary<string, int> { ["酒"] = 1 },
                new Dictionary<string, int> { ["酒"] = 1 },
                slaveRejectedLiquids: new[] { "酒" }));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Infeasible, result.Status);
        Assert.False(result.HandoffApplied);
        Assert.Empty(result.HandoffTransferredFreeLiquid);
        Assert.Equal(1, result.Roles["Master"].FinalFreeLiquid["酒"]);
        Assert.Empty(result.Roles["Slave"].FinalFreeLiquid);
    }

}

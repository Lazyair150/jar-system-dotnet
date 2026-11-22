using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderPreparatoryProgramBuilderTests
{
    private static WorldOrderRuleCandidate Candidate(
        int requirementIndex,
        string sourceShape,
        string? sourceLiquid,
        string operation,
        string targetShape,
        string? targetLiquid,
        string? consumedFree = null,
        string? producedFree = null) =>
        new(requirementIndex, sourceShape, sourceLiquid, operation,
            "test.rule", 1, targetShape, targetLiquid,
            null, consumedFree, producedFree, null);

    private static WorldOrderSymbolicSolution Solution(
        string role, WorldOrderRuleCandidate candidate) =>
        new(new[] { new WorldOrderAssignedRuleCandidate(role, candidate) });

    [Fact]
    public void SlavePreparationRunsAfterTheSingleHandoffAndFundsItsRequirement()
    {
        var requirement = Candidate(
            0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒");
        var preparation = Candidate(
            -1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒");

        var program = WorldOrderPreparatoryProgramBuilder.Build(
            Solution("Slave", requirement),
            new[]
            {
                new WorldOrderPreparatoryOperationAssignment(
                    "Slave", "requirement-0", preparation)
            },
            true, "Master", "Slave");

        Assert.Equal(
            new[] { "master-to-slave-handoff", "prepare-requirement-0", "requirement-0-slave" },
            program.Nodes.Select(x => x.NodeId));
        Assert.Single(program.Nodes, x => x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff);

        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            program,
            new WorldOrderSymbolicResourceSimulationRequest(
                0, 1,
                new Dictionary<string, int>(),
                new Dictionary<string, int>()));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Feasible, result.Status);
        Assert.True(result.HandoffApplied);
        Assert.Equal(1, result.Roles["Slave"].AcceptedFreeLiquidProduction["酒"]);
        Assert.Equal(1, result.Roles["Slave"].ConsumedFreeLiquid["酒"]);
        Assert.Empty(result.Roles["Slave"].FinalFreeLiquid);
    }

    [Fact]
    public void MasterPreparationCanProduceLiquidForTheOneTimeSlaveHandoff()
    {
        var requirement = Candidate(
            0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒");
        var preparation = Candidate(
            -1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒");

        var program = WorldOrderPreparatoryProgramBuilder.Build(
            Solution("Slave", requirement),
            new[]
            {
                new WorldOrderPreparatoryOperationAssignment(
                    "Master", "requirement-0", preparation)
            },
            true, "Master", "Slave");

        Assert.Equal(
            new[] { "prepare-requirement-0", "master-to-slave-handoff", "requirement-0-slave" },
            program.Nodes.Select(x => x.NodeId));

        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            program,
            new WorldOrderSymbolicResourceSimulationRequest(
                1, 1,
                new Dictionary<string, int>(),
                new Dictionary<string, int> { ["酒"] = 1 }));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Feasible, result.Status);
        Assert.True(result.HandoffApplied);
        Assert.Equal(1, result.HandoffTransferredFreeLiquid["酒"]);
        Assert.Equal(1, result.Roles["Slave"].ConsumedFreeLiquid["酒"]);
        Assert.Empty(result.Roles["Master"].FinalFreeLiquid);
    }

    [Fact]
    public void AClassPreparationRunsOnMasterWithoutCreatingAHandoff()
    {
        var requirement = Candidate(
            0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒");
        var preparation = Candidate(
            -1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒");

        var program = WorldOrderPreparatoryProgramBuilder.Build(
            Solution("Master", requirement),
            new[]
            {
                new WorldOrderPreparatoryOperationAssignment(
                    "Master", "requirement-0", preparation)
            },
            false, "Master", "Slave");

        Assert.Equal(new[] { "prepare-requirement-0", "requirement-0-master" },
            program.Nodes.Select(x => x.NodeId));
        Assert.DoesNotContain(program.Nodes,
            x => x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff);

        var result = WorldOrderSymbolicResourceSimulator.Simulate(
            program,
            new WorldOrderSymbolicResourceSimulationRequest(
                1, 0,
                new Dictionary<string, int>(),
                new Dictionary<string, int>()));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Feasible, result.Status);
        Assert.False(result.HandoffApplied);
        Assert.Equal(1, result.Roles["Master"].ConsumedFreeLiquid["酒"]);
    }

    [Fact]
    public void PreparationCandidateMustMatchTheSelectedJarAndBePourOut()
    {
        var requirement = Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒");
        var wrongInput = Candidate(-1, "方罐", "酒", "PREPARE_POUR_OUT", "方罐", null, producedFree: "酒");
        var wrongOperation = Candidate(-1, "圆罐", null, "RESHAPE_FULL", "方罐", null);

        Assert.Throws<ArgumentException>(() =>
            WorldOrderPreparatoryProgramBuilder.Build(
                Solution("Master", requirement),
                new[] { new WorldOrderPreparatoryOperationAssignment("Master", "requirement-0", wrongInput) },
                false, "Master", "Slave"));

        Assert.Throws<ArgumentException>(() =>
            WorldOrderPreparatoryProgramBuilder.Build(
                Solution("Master", requirement),
                new[] { new WorldOrderPreparatoryOperationAssignment("Master", "other-jar", wrongOperation) },
                false, "Master", "Slave"));
    }

    [Fact]
    public void PreparationJarIdentityCannotBeAssignedTwice()
    {
        var requirement = Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒");
        var preparation = Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒");
        var duplicate = new[]
        {
            new WorldOrderPreparatoryOperationAssignment("Master", "jar-A", preparation),
            new WorldOrderPreparatoryOperationAssignment("Master", "jar-A", preparation)
        };

        Assert.Throws<ArgumentException>(() =>
            WorldOrderPreparatoryProgramBuilder.Build(
                Solution("Master", requirement), duplicate, false, "Master", "Slave"));
    }

    [Fact]
    public void OneJarCanRunAnOrderedMultiStepPreparationChainBeforeItsRequirement()
    {
        var requirement = Candidate(
            0, "圆罐", "酒", "MATCH", "圆罐", "酒");
        var pourOut = Candidate(
            -1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null,
            producedFree: "酒");
        var pourIn = Candidate(
            -1, "圆罐", null, "PREPARE_POUR_IN", "圆罐", "酒",
            consumedFree: "酒");

        var program = WorldOrderPreparatoryProgramBuilder.Build(
            Solution("Slave", requirement),
            new[]
            {
                new WorldOrderPreparatoryOperationAssignment(
                    "Slave", "requirement-0", pourOut, Order: 0),
                new WorldOrderPreparatoryOperationAssignment(
                    "Slave", "requirement-0", pourIn, Order: 1)
            },
            true, "Master", "Slave");

        Assert.Equal(
            new[]
            {
                "master-to-slave-handoff",
                "prepare-requirement-0-0",
                "prepare-requirement-0-1",
                "requirement-0-slave"
            },
            program.Nodes.Select(x => x.NodeId));
        Assert.Contains(program.Dependencies, edge =>
            edge.Kind == WorldOrderSymbolicDependencyKind.JarState &&
            edge.FromNodeId == "prepare-requirement-0-0" &&
            edge.ToNodeId == "prepare-requirement-0-1" &&
            edge.JarRevision == 1);
        Assert.Contains(program.Dependencies, edge =>
            edge.Kind == WorldOrderSymbolicDependencyKind.JarState &&
            edge.FromNodeId == "prepare-requirement-0-1" &&
            edge.ToNodeId == "requirement-0-slave" &&
            edge.JarRevision == 2);

        var simulation = WorldOrderSymbolicResourceSimulator.Simulate(
            program,
            new WorldOrderSymbolicResourceSimulationRequest(
                0, 1,
                new Dictionary<string, int>(),
                new Dictionary<string, int>()));

        Assert.Equal(WorldOrderSymbolicResourceSimulationStatus.Feasible, simulation.Status);
        Assert.Equal(1, simulation.Roles["Slave"].AcceptedFreeLiquidProduction["酒"]);
        Assert.Equal(1, simulation.Roles["Slave"].ConsumedFreeLiquid["酒"]);
        Assert.Empty(simulation.Roles["Slave"].FinalFreeLiquid);
    }

    [Fact]
    public void MultiStepPreparationChainMustBeStateContinuous()
    {
        var requirement = Candidate(0, "圆罐", null, "MATCH", "圆罐", null);
        var first = Candidate(
            -1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null,
            producedFree: "酒");
        var disconnected = Candidate(
            -1, "方罐", null, "PREPARE_POUR_IN", "方罐", "酒",
            consumedFree: "酒");

        Assert.Throws<ArgumentException>(() =>
            WorldOrderPreparatoryProgramBuilder.Build(
                Solution("Slave", requirement),
                new[]
                {
                    new WorldOrderPreparatoryOperationAssignment(
                        "Slave", "requirement-0", first, Order: 0),
                    new WorldOrderPreparatoryOperationAssignment(
                        "Slave", "requirement-0", disconnected, Order: 1)
                },
                true, "Master", "Slave"));
    }

    [Fact]
    public void SlaveCannotPrepareItsOwnInitialJarBeforeHandoff()
    {
        var requirement = Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒");
        var preparation = Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒");

        var program = WorldOrderPreparatoryProgramBuilder.Build(
            Solution("Slave", requirement),
            new[] { new WorldOrderPreparatoryOperationAssignment("Slave", "requirement-0", preparation) },
            true, "Master", "Slave");

        Assert.Equal(WorldOrderSymbolicOperationKind.MasterToSlaveHandoff, program.Nodes[0].Kind);
        Assert.Equal("Master", program.Nodes[0].InputJars[0].WorkshopRole);
        Assert.Equal("Slave", program.Nodes[0].OutputJars[0].WorkshopRole);
        Assert.Equal("Slave", program.Nodes[1].WorkshopRole);
    }
}

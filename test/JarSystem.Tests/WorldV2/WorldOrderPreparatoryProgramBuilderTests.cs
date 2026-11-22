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
                    "Slave", "preparation-resource-0", preparation)
            },
            true, "Master", "Slave");

        Assert.Equal(
            new[] { "master-to-slave-handoff", "prepare-preparation-resource-0", "requirement-0-slave" },
            program.Nodes.Select(x => x.NodeId));
        Assert.Single(program.Nodes, x => x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff);
        Assert.Equal("preparation-resource-0",
            program.Nodes.Single(x => x.NodeId == "prepare-preparation-resource-0").InputJars[0].JarKey);

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
    public void ExplicitSlaveOperationOrderIsIndependentOfRequirementIndex()
    {
        var consumer = Candidate(
            0, "consumer-jar", null, "RESHAPE_EMPTY", "consumer-finished", null,
            consumedFree: "X");
        var producer = Candidate(
            1, "producer-jar", null, "RESHAPE_EMPTY", "producer-finished", null,
            producedFree: "X");
        var solution = new WorldOrderSymbolicSolution(new[]
        {
            new WorldOrderAssignedRuleCandidate("Slave", consumer),
            new WorldOrderAssignedRuleCandidate("Slave", producer)
        });

        var program = WorldOrderPreparatoryProgramBuilder.Build(
            solution,
            Array.Empty<WorldOrderPreparatoryOperationAssignment>(),
            true, "Master", "Slave",
            new[] { "requirement-1-slave", "requirement-0-slave" });

        Assert.Equal(
            new[] { "master-to-slave-handoff", "requirement-1-slave", "requirement-0-slave" },
            program.Nodes.Select(x => x.NodeId));
        Assert.Contains(program.Dependencies, edge =>
            edge.Kind == WorldOrderSymbolicDependencyKind.FreeLiquid &&
            edge.FromNodeId == "requirement-1-slave" &&
            edge.ToNodeId == "requirement-0-slave" &&
            edge.Liquid == "X");
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
                    "Master", "preparation-resource-0", preparation)
            },
            true, "Master", "Slave");

        Assert.Equal(
            new[] { "prepare-preparation-resource-0", "master-to-slave-handoff", "requirement-0-slave" },
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
                    "Master", "preparation-resource-0", preparation)
            },
            false, "Master", "Slave");

        Assert.Equal(new[] { "prepare-preparation-resource-0", "requirement-0-master" },
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
    public void ExplicitMasterOperationOrderIsIndependentOfRequirementIndex()
    {
        var consumer = Candidate(
            0, "consumer-jar", null, "RESHAPE_EMPTY", "consumer-finished", null,
            consumedFree: "X");
        var solution = new WorldOrderSymbolicSolution(new[]
        {
            new WorldOrderAssignedRuleCandidate("Master",
                Candidate(0, "consumer-jar", null, "RESHAPE_EMPTY", "consumer-finished", null,
                    consumedFree: "X")),
            new WorldOrderAssignedRuleCandidate("Master",
                Candidate(1, "producer-jar", "X", "POUR_OUT", "producer-finished", null,
                    producedFree: "X"))
        });

        var program = WorldOrderPreparatoryProgramBuilder.Build(
            solution,
            Array.Empty<WorldOrderPreparatoryOperationAssignment>(),
            false, "Master", "Slave",
            masterOperationOrder: new[]
            {
                "requirement-1-master",
                "requirement-0-master"
            });

        Assert.Equal(
            new[] { "requirement-1-master", "requirement-0-master" },
            program.Nodes.Select(x => x.NodeId));
        Assert.Contains(program.Dependencies, edge =>
            edge.Kind == WorldOrderSymbolicDependencyKind.FreeLiquid &&
            edge.FromNodeId == "requirement-1-master" &&
            edge.ToNodeId == "requirement-0-master" &&
            edge.Liquid == "X");
    }

    [Fact]
    public void ExplicitMasterOperationOrderMustBeExactAndUnique()
    {
        var first = Candidate(0, "a", null, "MATCH", "a", null);
        var second = Candidate(1, "b", null, "MATCH", "b", null);
        var solution = new WorldOrderSymbolicSolution(new[]
        {
            new WorldOrderAssignedRuleCandidate("Master", first),
            new WorldOrderAssignedRuleCandidate("Master", second)
        });

        Assert.Throws<ArgumentException>(() =>
            WorldOrderPreparatoryProgramBuilder.Build(
                solution, Array.Empty<WorldOrderPreparatoryOperationAssignment>(),
                false, "Master", "Slave",
                masterOperationOrder: new[] { "requirement-0-master" }));
        Assert.Throws<ArgumentException>(() =>
            WorldOrderPreparatoryProgramBuilder.Build(
                solution, Array.Empty<WorldOrderPreparatoryOperationAssignment>(),
                false, "Master", "Slave",
                masterOperationOrder: new[] { "requirement-0-master", "requirement-0-master" }));
    }

    [Fact]
    public void PreparationCandidateMustBeCataloguedPourOut()
    {
        var requirement = Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒");
        var unsupportedPreparation = Candidate(
            -1, "圆罐", null, "PREPARE_POUR_IN", "圆罐", "酒", consumedFree: "酒");

        Assert.Throws<ArgumentException>(() =>
            WorldOrderPreparatoryProgramBuilder.Build(
                Solution("Master", requirement),
                new[] { new WorldOrderPreparatoryOperationAssignment(
                    "Master", "preparation-resource-0", unsupportedPreparation) },
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
    public void PreparationJarCannotAlsoBeUsedByARequirement()
    {
        var requirement = Candidate(0, "圆罐", "酒", "MATCH", "圆罐", "酒");
        var preparation = Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null,
            producedFree: "酒");

        Assert.Throws<ArgumentException>(() =>
            WorldOrderPreparatoryProgramBuilder.Build(
                Solution("Slave", requirement),
                new[] { new WorldOrderPreparatoryOperationAssignment(
                    "Slave", "requirement-0", preparation) },
                true, "Master", "Slave"));
    }

    [Fact]
    public void OneJarCannotReceiveTwoPreparationAssignments()
    {
        var requirement = Candidate(0, "圆罐", null, "MATCH", "圆罐", null);
        var preparation = Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null,
            producedFree: "酒");

        Assert.Throws<ArgumentException>(() =>
            WorldOrderPreparatoryProgramBuilder.Build(
                Solution("Slave", requirement),
                new[]
                {
                    new WorldOrderPreparatoryOperationAssignment("Slave", "jar-A", preparation),
                    new WorldOrderPreparatoryOperationAssignment("Slave", "jar-A", preparation)
                },
                true, "Master", "Slave"));
    }

    [Fact]
    public void PreparationJarIsNotTheRequirementJarAtHandoff()
    {
        var requirement = Candidate(0, "圆罐", null, "MATCH", "圆罐", null);
        var preparation = Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null,
            producedFree: "酒");

        var program = WorldOrderPreparatoryProgramBuilder.Build(
            Solution("Slave", requirement),
            new[] { new WorldOrderPreparatoryOperationAssignment(
                "Master", "preparation-resource-0", preparation) },
            true, "Master", "Slave");

        var prep = Assert.Single(program.Nodes, x => x.NodeId == "prepare-preparation-resource-0");
        Assert.False(prep.InputJars[0].IsMarked);
        Assert.True(prep.OutputJars[0].IsMarked);
        var handoff = Assert.Single(program.Nodes,
            x => x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff);
        Assert.DoesNotContain(handoff.InputJars, x => x.JarKey == "preparation-resource-0");
        var handedOff = Assert.Single(handoff.InputJars,
            x => x.JarKey == "requirement-0");
        Assert.False(handedOff.IsMarked);
    }

    [Fact]
    public void SlaveCannotPrepareItsOwnInitialJarBeforeHandoff()
    {
        var requirement = Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒");
        var preparation = Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒");

        var program = WorldOrderPreparatoryProgramBuilder.Build(
            Solution("Slave", requirement),
            new[] { new WorldOrderPreparatoryOperationAssignment("Slave", "preparation-resource-0", preparation) },
            true, "Master", "Slave");

        Assert.Equal(WorldOrderSymbolicOperationKind.MasterToSlaveHandoff, program.Nodes[0].Kind);
        Assert.Equal("Master", program.Nodes[0].InputJars[0].WorkshopRole);
        Assert.Equal("Slave", program.Nodes[0].OutputJars[0].WorkshopRole);
        Assert.Equal("Slave", program.Nodes[1].WorkshopRole);
    }
}

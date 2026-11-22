using System;
using System.Collections.Generic;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderSymbolicOperationProgramTests
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
        new(
            requirementIndex, sourceShape, sourceLiquid, operation,
            "test.rule", 1, targetShape, targetLiquid,
            null, consumedFree, producedFree, null);

    [Fact]
    public void BClassProgramUsesDistinctJarsForMasterAndSlaveOperations()
    {
        var masterInput = new WorldOrderSymbolicJarVersion("jar-master-resource", 0, "Master", "圆罐", "酒");
        var masterOutput = new WorldOrderSymbolicJarVersion(
            "jar-master-resource", 1, "Master", "圆罐", null, isMarked: true);
        var slaveInitial = new WorldOrderSymbolicJarVersion("jar-slave-requirement", 0, "Master", "圆罐", null);
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 1, "Master", "Slave", new[] { slaveInitial });
        var slaveInput = handoff.OutputJars[0];
        var slaveOutput = new WorldOrderSymbolicJarVersion(
            "jar-slave-requirement", 2, "Slave", "方罐", null, isMarked: true);

        var master = WorldOrderSymbolicOperationNode.Rule(
            "master-pour-out", 0, "Master", null,
            Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒"),
            masterInput, masterOutput);
        var slave = WorldOrderSymbolicOperationNode.Rule(
            "slave-reshape", 2, "Slave", 0,
            Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒"),
            slaveInput, slaveOutput);

        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave",
            new[] { slave, handoff, master },
            new[]
            {
                WorldOrderSymbolicOperationDependency.FreeLiquid("master-pour-out", "handoff", "酒"),
                WorldOrderSymbolicOperationDependency.FreeLiquid("handoff", "slave-reshape", "酒"),
                WorldOrderSymbolicOperationDependency.JarState(
                    "handoff", "slave-reshape", "jar-slave-requirement", 1)
            });

        Assert.Equal(new[] { "master-pour-out", "handoff", "slave-reshape" },
            new List<string>(System.Linq.Enumerable.Select(program.Nodes, x => x.NodeId)));
        Assert.Single(program.Nodes, x => x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff);
        Assert.Equal("jar-slave-requirement", handoff.InputJars[0].JarKey);
        Assert.Equal("Slave", handoff.OutputJars[0].WorkshopRole);
        Assert.True(masterOutput.IsMarked);
        Assert.False(handoff.OutputJars[0].IsMarked);
        Assert.True(slaveOutput.IsMarked);
    }

    [Fact]
    public void AClassProgramDoesNotContainHandoff()
    {
        var input = new WorldOrderSymbolicJarVersion("jar-A", 0, "Master", "圆罐", null);
        var output = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "方罐", null, isMarked: true);
        var node = WorldOrderSymbolicOperationNode.Rule(
            "master-step", 0, "Master", 0,
            Candidate(0, "圆罐", null, "RESHAPE_FULL", "方罐", null),
            input, output);

        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { node },
            Array.Empty<WorldOrderSymbolicOperationDependency>());

        Assert.False(program.IsBClass);
        Assert.Empty(program.Dependencies);
    }

    [Fact]
    public void BClassRejectsMissingOrRepeatedHandoff()
    {
        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicOperationProgram(
                true, "Master", "Slave", Array.Empty<WorldOrderSymbolicOperationNode>(),
                Array.Empty<WorldOrderSymbolicOperationDependency>()));

        var first = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff-1", 0, "Master", "Slave", Array.Empty<WorldOrderSymbolicJarVersion>());
        var second = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff-2", 1, "Master", "Slave", Array.Empty<WorldOrderSymbolicJarVersion>());

        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicOperationProgram(
                true, "Master", "Slave", new[] { first, second },
                Array.Empty<WorldOrderSymbolicOperationDependency>()));
    }

    [Fact]
    public void SlaveOperationMustFollowTheOnlyHandoff()
    {
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 1, "Master", "Slave", Array.Empty<WorldOrderSymbolicJarVersion>());
        var input = new WorldOrderSymbolicJarVersion("jar-A", 0, "Slave", "圆罐", null);
        var output = new WorldOrderSymbolicJarVersion("jar-A", 1, "Slave", "方罐", null, isMarked: true);
        var slave = WorldOrderSymbolicOperationNode.Rule(
            "slave-step", 0, "Slave", 0,
            Candidate(0, "圆罐", null, "RESHAPE_FULL", "方罐", null),
            input, output);

        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicOperationProgram(
                true, "Master", "Slave", new[] { slave, handoff },
                Array.Empty<WorldOrderSymbolicOperationDependency>()));
    }

    [Fact]
    public void FreeLiquidCannotBypassTheSingleHandoff()
    {
        var masterInput = new WorldOrderSymbolicJarVersion("jar-master-resource", 0, "Master", "圆罐", "酒");
        var masterOutput = new WorldOrderSymbolicJarVersion(
            "jar-master-resource", 1, "Master", "圆罐", null, isMarked: true);
        var master = WorldOrderSymbolicOperationNode.Rule(
            "master-step", 0, "Master", null,
            Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒"),
            masterInput, masterOutput);
        var slaveInitial = new WorldOrderSymbolicJarVersion("jar-slave-resource", 0, "Master", "圆罐", null);
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 1, "Master", "Slave", new[] { slaveInitial });
        var slaveInput = handoff.OutputJars[0];
        var slaveOutput = new WorldOrderSymbolicJarVersion(
            "jar-slave-resource", 2, "Slave", "方罐", null, isMarked: true);
        var slave = WorldOrderSymbolicOperationNode.Rule(
            "slave-step", 2, "Slave", 0,
            Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒"),
            slaveInput, slaveOutput);

        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicOperationProgram(
                true, "Master", "Slave", new[] { master, handoff, slave },
                new[]
                {
                    WorldOrderSymbolicOperationDependency.FreeLiquid("master-step", "slave-step", "酒"),
                    WorldOrderSymbolicOperationDependency.JarState(
                        "handoff", "slave-step", "jar-slave-resource", 1)
                }));
    }

    [Fact]
    public void OneJarVersionCannotBeConsumedByTwoOperations()
    {
        var input = new WorldOrderSymbolicJarVersion("jar-A", 0, "Master", "圆罐", null);
        var firstOutput = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "方罐", null, isMarked: true);
        var secondOutput = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "三角罐", null, isMarked: true);
        var first = WorldOrderSymbolicOperationNode.Rule(
            "first", 0, "Master", 0,
            Candidate(0, "圆罐", null, "RESHAPE_FULL", "方罐", null),
            input, firstOutput);
        var second = WorldOrderSymbolicOperationNode.Rule(
            "second", 1, "Master", 1,
            Candidate(1, "圆罐", null, "RESHAPE_FULL", "三角罐", null),
            input, secondOutput);

        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicOperationProgram(
                false, "Master", "Slave", new[] { first, second },
                Array.Empty<WorldOrderSymbolicOperationDependency>()));
    }

    [Fact]
    public void RuleCandidateMustMatchItsJarFactsAndPreparationUsesNoRequirementIndex()
    {
        var input = new WorldOrderSymbolicJarVersion("jar-A", 0, "Master", "圆罐", "酒");
        var output = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "圆罐", null, isMarked: true);
        var candidate = Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒");

        var node = WorldOrderSymbolicOperationNode.Rule(
            "prepare", 0, "Master", null, candidate, input, output);

        Assert.Null(node.RequirementIndex);
        Assert.Equal("PREPARE_POUR_OUT", node.Candidate!.Operation);
        Assert.Throws<ArgumentException>(() =>
            WorldOrderSymbolicOperationNode.Rule(
                "bad", 1, "Master", 0, candidate, input, output));
    }
    [Fact]
    public void DirectCandidateBuilderCreatesExactlyOneBClassHandoffAndRoutesSlaveLiquidThroughIt()
    {
        var solution = new WorldOrderSymbolicSolution(new[]
        {
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(0, "圆罐", null, "MASTER_RULE", "方罐", null, producedFree: "酒")),
            new WorldOrderAssignedRuleCandidate(
                "Slave", Candidate(1, "圆罐", null, "SLAVE_RULE", "三角罐", null, consumedFree: "酒"))
        });

        var program = WorldOrderSymbolicOperationProgramBuilder.BuildDirectCandidateProjection(
            solution, true, "Master", "Slave");

        Assert.Equal(
            new[] { "requirement-0-master", "master-to-slave-handoff", "requirement-1-slave" },
            System.Linq.Enumerable.Select(program.Nodes, x => x.NodeId));
        Assert.Single(program.Nodes, x => x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff);
        Assert.Contains(program.Dependencies, x =>
            x.Kind == WorldOrderSymbolicDependencyKind.FreeLiquid &&
            x.FromNodeId == "requirement-0-master" &&
            x.ToNodeId == "master-to-slave-handoff" && x.Liquid == "酒");
        Assert.Contains(program.Dependencies, x =>
            x.Kind == WorldOrderSymbolicDependencyKind.FreeLiquid &&
            x.FromNodeId == "master-to-slave-handoff" &&
            x.ToNodeId == "requirement-1-slave" && x.Liquid == "酒");
    }

    [Fact]
    public void DirectCandidateBuilderKeepsAClassOnMasterWithoutHandoff()
    {
        var solution = new WorldOrderSymbolicSolution(new[]
        {
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(0, "圆罐", null, "MASTER_RULE", "方罐", null))
        });

        var program = WorldOrderSymbolicOperationProgramBuilder.BuildDirectCandidateProjection(
            solution, false, "Master", "Slave");

        Assert.Single(program.Nodes);
        Assert.Empty(program.Dependencies);
        Assert.DoesNotContain(program.Nodes, x =>
            x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff);
    }

    [Fact]
    public void DirectCandidateBuilderRejectsPreparationCandidatesAndDuplicateRequirements()
    {
        var preparation = new WorldOrderSymbolicSolution(new[]
        {
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒"))
        });
        Assert.Throws<ArgumentException>(() =>
            WorldOrderSymbolicOperationProgramBuilder.BuildDirectCandidateProjection(
                preparation, false, "Master", "Slave"));

        var duplicate = new WorldOrderSymbolicSolution(new[]
        {
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(0, "圆罐", null, "MASTER_RULE", "方罐", null)),
            new WorldOrderAssignedRuleCandidate(
                "Slave", Candidate(0, "圆罐", null, "SLAVE_RULE", "三角罐", null))
        });
        Assert.Throws<ArgumentException>(() =>
            WorldOrderSymbolicOperationProgramBuilder.BuildDirectCandidateProjection(
                duplicate, true, "Master", "Slave"));
    }

    [Fact]
    public void SlaveCannotUseAnInitialJarOrUnfundedFreeLiquid()
    {
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 0, "Master", "Slave", Array.Empty<WorldOrderSymbolicJarVersion>());
        var slaveInitial = new WorldOrderSymbolicJarVersion("jar-A", 0, "Slave", "圆罐", null);
        var slaveOutput = new WorldOrderSymbolicJarVersion("jar-A", 1, "Slave", "方罐", null, isMarked: true);
        var slaveFromInitial = WorldOrderSymbolicOperationNode.Rule(
            "slave-step", 1, "Slave", 0,
            Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒"),
            slaveInitial, slaveOutput);

        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicOperationProgram(
                true, "Master", "Slave", new[] { handoff, slaveFromInitial },
                new[]
                {
                    WorldOrderSymbolicOperationDependency.FreeLiquid("handoff", "slave-step", "酒")
                }));

        var masterJar = new WorldOrderSymbolicJarVersion("jar-B", 0, "Master", "圆罐", null);
        var secondHandoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff-2", 0, "Master", "Slave", new[] { masterJar });
        var receivedJar = secondHandoff.OutputJars[0];
        var slaveAfterHandoff = WorldOrderSymbolicOperationNode.Rule(
            "slave-after-handoff", 1, "Slave", 0,
            Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒"),
            receivedJar,
            new WorldOrderSymbolicJarVersion("jar-B", 2, "Slave", "方罐", null, isMarked: true));

        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicOperationProgram(
                true, "Master", "Slave", new[] { secondHandoff, slaveAfterHandoff },
                new[]
                {
                    WorldOrderSymbolicOperationDependency.JarState(
                        "handoff-2", "slave-after-handoff", "jar-B", 1)
                }));
    }

    [Fact]
    public void FreeLiquidDependencyMustMatchTheProducerAndConsumerEffects()
    {
        var producerInput = new WorldOrderSymbolicJarVersion("jar-producer", 0, "Master", "圆罐", "酒");
        var producerOutput = new WorldOrderSymbolicJarVersion("jar-producer", 1, "Master", "圆罐", null, isMarked: true);
        var producer = WorldOrderSymbolicOperationNode.Rule(
            "producer", 0, "Master", null,
            Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒"),
            producerInput, producerOutput);

        var consumerInput = new WorldOrderSymbolicJarVersion("jar-consumer", 0, "Master", "圆罐", null);
        var consumerOutput = new WorldOrderSymbolicJarVersion("jar-consumer", 1, "Master", "方罐", null, isMarked: true);
        var consumer = WorldOrderSymbolicOperationNode.Rule(
            "consumer", 1, "Master", 0,
            Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "蜜"),
            consumerInput, consumerOutput);

        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicOperationProgram(
                false, "Master", "Slave", new[] { producer, consumer },
                new[]
                {
                    WorldOrderSymbolicOperationDependency.FreeLiquid(
                        "producer", "consumer", "蜜")
                }));
    }

}

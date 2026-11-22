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
    public void BClassProgramHasOneHandoffBetweenMasterAndSlaveOperations()
    {
        var masterInput = new WorldOrderSymbolicJarVersion("jar-A", 0, "Master", "圆罐", "酒");
        var masterOutput = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "圆罐", null);
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 1, "Master", "Slave", new[] { masterOutput });
        var slaveInput = handoff.OutputJars[0];
        var slaveOutput = new WorldOrderSymbolicJarVersion("jar-A", 3, "Slave", "方罐", null);

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
                WorldOrderSymbolicOperationDependency.JarState("master-pour-out", "handoff", "jar-A", 1),
                WorldOrderSymbolicOperationDependency.JarState("handoff", "slave-reshape", "jar-A", 2),
                WorldOrderSymbolicOperationDependency.FreeLiquid("master-pour-out", "handoff", "酒"),
                WorldOrderSymbolicOperationDependency.FreeLiquid("handoff", "slave-reshape", "酒")
            });

        Assert.Equal(new[] { "master-pour-out", "handoff", "slave-reshape" },
            new List<string>(System.Linq.Enumerable.Select(program.Nodes, x => x.NodeId)));
        Assert.Single(program.Nodes, x => x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff);
        Assert.Equal("jar-A", handoff.InputJars[0].JarKey);
        Assert.Equal("Slave", handoff.OutputJars[0].WorkshopRole);
    }

    [Fact]
    public void AClassProgramDoesNotContainHandoff()
    {
        var input = new WorldOrderSymbolicJarVersion("jar-A", 0, "Master", "圆罐", null);
        var output = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "方罐", null);
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
        var output = new WorldOrderSymbolicJarVersion("jar-A", 1, "Slave", "方罐", null);
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
        var masterInput = new WorldOrderSymbolicJarVersion("jar-A", 0, "Master", "圆罐", "酒");
        var masterOutput = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "圆罐", null);
        var master = WorldOrderSymbolicOperationNode.Rule(
            "master-step", 0, "Master", null,
            Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒"),
            masterInput, masterOutput);
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 1, "Master", "Slave", new[] { masterOutput });
        var slaveInput = handoff.OutputJars[0];
        var slaveOutput = new WorldOrderSymbolicJarVersion("jar-A", 3, "Slave", "方罐", null);
        var slave = WorldOrderSymbolicOperationNode.Rule(
            "slave-step", 2, "Slave", 0,
            Candidate(0, "圆罐", null, "RESHAPE_EMPTY", "方罐", null, consumedFree: "酒"),
            slaveInput, slaveOutput);

        Assert.Throws<ArgumentException>(() =>
            new WorldOrderSymbolicOperationProgram(
                true, "Master", "Slave", new[] { master, handoff, slave },
                new[]
                {
                    WorldOrderSymbolicOperationDependency.JarState("master-step", "handoff", "jar-A", 1),
                    WorldOrderSymbolicOperationDependency.JarState("handoff", "slave-step", "jar-A", 2),
                    WorldOrderSymbolicOperationDependency.FreeLiquid("master-step", "slave-step", "酒")
                }));
    }

    [Fact]
    public void OneJarVersionCannotBeConsumedByTwoOperations()
    {
        var input = new WorldOrderSymbolicJarVersion("jar-A", 0, "Master", "圆罐", null);
        var firstOutput = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "方罐", null);
        var secondOutput = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "三角罐", null);
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
        var output = new WorldOrderSymbolicJarVersion("jar-A", 1, "Master", "圆罐", null);
        var candidate = Candidate(-1, "圆罐", "酒", "PREPARE_POUR_OUT", "圆罐", null, producedFree: "酒");

        var node = WorldOrderSymbolicOperationNode.Rule(
            "prepare", 0, "Master", null, candidate, input, output);

        Assert.Null(node.RequirementIndex);
        Assert.Equal("PREPARE_POUR_OUT", node.Candidate!.Operation);
        Assert.Throws<ArgumentException>(() =>
            WorldOrderSymbolicOperationNode.Rule(
                "bad", 1, "Master", 0, candidate, input, output));
    }
}

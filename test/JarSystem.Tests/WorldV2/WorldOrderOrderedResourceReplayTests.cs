using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.World;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderOrderedResourceReplayTests
{
    private static WorldSnapshot AWorld(bool banWine = false, bool withProducerJar = true)
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            ban: banWine ? new List<string> { "酒" } : null,
            liquidStagingCapacity: 1);
        if (withProducerJar) master.AddJar(new Jar("圆罐", "酒", id: 921));
        master.AddJar(new Jar("圆罐", id: 922));
        return WorldSnapshotFactory.Capture(new WorkshopRoster(new[] { master }, master));
    }

    private static WorldOrderSymbolicOperationProgram ProducerThenConsumer(bool consumerFirst)
    {
        var producerCandidate = new WorldOrderRuleCandidate(
            -1, "圆罐", "酒", "PREPARE_POUR_OUT", "pour-out.rule", 1,
            "圆罐", null, null, null, "酒", null);
        var consumerCandidate = new WorldOrderRuleCandidate(
            0, "圆罐", null, "POUR_IN", "pour-in.rule", 1,
            "圆罐", "酒", null, "酒", null, null);
        var producerInput = new WorldOrderSymbolicJarVersion("producer", 0, "Master", "圆罐", "酒");
        var producerOutput = new WorldOrderSymbolicJarVersion("producer", 1, "Master", "圆罐", null, true);
        var consumerInput = new WorldOrderSymbolicJarVersion("consumer", 0, "Master", "圆罐", null);
        var consumerOutput = new WorldOrderSymbolicJarVersion("consumer", 1, "Master", "圆罐", "酒", true);
        var producer = WorldOrderSymbolicOperationNode.Rule(
            "producer", consumerFirst ? 1 : 0, "Master", null,
            producerCandidate, producerInput, producerOutput);
        var consumer = WorldOrderSymbolicOperationNode.Rule(
            "consumer", consumerFirst ? 0 : 1, "Master", 0,
            consumerCandidate, consumerInput, consumerOutput);
        return new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { producer, consumer },
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());
    }

    [Fact]
    public void ProducerBeforeConsumerNeedsNoInitialFreeLiquid()
    {
        var result = WorldOrderOrderedResourceReplay.Replay(
            ProducerThenConsumer(false), AWorld());

        Assert.Equal(WorldOrderOrderedResourceReplayStatus.Complete, result.Status);
        Assert.Empty(result.MasterInitialLiquids);
        Assert.Empty(result.MasterFinalLiquids);
        Assert.Equal(1, result.MasterStagingPeakCapacity);
    }

    [Fact]
    public void ConsumerBeforeProducerRequiresInitialLiquidAndPreservesSequence()
    {
        var result = WorldOrderOrderedResourceReplay.Replay(
            ProducerThenConsumer(true), AWorld());

        Assert.Equal(WorldOrderOrderedResourceReplayStatus.Complete, result.Status);
        Assert.Equal(1, result.MasterInitialLiquids["酒"]);
        Assert.Equal(1, result.MasterFinalLiquids["酒"]);
        Assert.Equal(1, result.MasterStagingPeakCapacity);
    }

    [Fact]
    public void BannedMasterStagingLiquidCannotBeProducedAndThenConsumed()
    {
        var result = WorldOrderOrderedResourceReplay.Replay(
            ProducerThenConsumer(false), AWorld(banWine: true));

        Assert.Equal(WorldOrderOrderedResourceReplayStatus.Infeasible, result.Status);
        Assert.Contains("ban", result.Reason);
    }

    [Fact]
    public void MasterBanDoesNotApplyToLiquidInsideATransferredJar()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", "酒", id: 931));
        var slave = new Workshop("从台", new List<string> { "圆罐" });
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(
            new[] { master, slave }, master, slave));

        var candidate = new WorldOrderRuleCandidate(
            0, "圆罐", "酒", "MATCH", "match.full.jar", 1,
            "圆罐", "酒", null, null, null, null);
        var initial = new WorldOrderSymbolicJarVersion("delivery", 0, "Master", "圆罐", "酒");
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 0, "Master", "Slave", new[] { initial });
        var received = handoff.OutputJars[0];
        var output = new WorldOrderSymbolicJarVersion(
            "delivery", 2, "Slave", "圆罐", "酒", true);
        var slaveNode = WorldOrderSymbolicOperationNode.Rule(
            "slave-match", 1, "Slave", 0, candidate, received, output);
        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { handoff, slaveNode },
            new[] { WorldOrderSymbolicOperationDependency.JarState(
                handoff.NodeId, slaveNode.NodeId, "delivery", 1) });

        var result = WorldOrderOrderedResourceReplay.Replay(program, world);

        Assert.Equal(WorldOrderOrderedResourceReplayStatus.Complete, result.Status);
        Assert.Empty(result.TransferLiquids);
        Assert.Single(result.SlaveBoundaryGroup!.Requirements);
        Assert.Equal(WorldOrderBoundaryResourceKind.JarState,
            result.SlaveBoundaryGroup.Requirements[0].Kind);
    }

    [Fact]
    public void SlaveRejectBlocksRequiredFreeLiquidButNotJarState()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐", id: 941));
        var slave = new Workshop("从台", new List<string> { "圆罐" });
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(
            new[] { master, slave }, master, slave));

        var candidate = new WorldOrderRuleCandidate(
            0, "圆罐", null, "RESHAPE_EMPTY", "reshape.empty", 1,
            "方罐", null, "酒", "酒", null, null);
        var initial = new WorldOrderSymbolicJarVersion("tool", 0, "Master", "圆罐", null);
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 0, "Master", "Slave", new[] { initial });
        var received = handoff.OutputJars[0];
        var output = new WorldOrderSymbolicJarVersion("tool", 2, "Slave", "方罐", null, true);
        var slaveNode = WorldOrderSymbolicOperationNode.Rule(
            "slave-reshape", 1, "Slave", 0, candidate, received, output);
        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { handoff, slaveNode },
            new[] { WorldOrderSymbolicOperationDependency.JarState(
                handoff.NodeId, slaveNode.NodeId, "tool", 1) });

        var result = WorldOrderOrderedResourceReplay.Replay(
            program, world, new[] { "酒" });

        Assert.Equal(WorldOrderOrderedResourceReplayStatus.Infeasible, result.Status);
        Assert.Contains("拒收", result.Reason);
    }

    [Fact]
    public void MasterBanDoesNotBlockFullReshapeUsingJarInternalLiquid()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("方罐", "酒", id: 951));
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(new[] { master }, master));
        var candidate = new WorldOrderRuleCandidate(
            0, "方罐", "酒", "RESHAPE_FULL", "reshape.full", 1,
            "圆罐", null, null, null, null, "酒");
        var input = new WorldOrderSymbolicJarVersion("full-jar", 0, "Master", "方罐", "酒");
        var output = new WorldOrderSymbolicJarVersion("full-jar", 1, "Master", "圆罐", null, true);
        var node = WorldOrderSymbolicOperationNode.Rule(
            "full-reshape", 0, "Master", 0, candidate, input, output);
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { node },
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());

        var result = WorldOrderOrderedResourceReplay.Replay(program, world);

        Assert.Equal(WorldOrderOrderedResourceReplayStatus.Complete, result.Status);
        Assert.Empty(result.MasterInitialLiquids);
        Assert.Empty(result.MasterFinalLiquids);
    }
}

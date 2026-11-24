using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.World;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderConcretePlanAssemblerTests
{
    [Fact]
    public void AClassReturnsFeasibleOnlyAfterMaterializationReplayAndManifestAgree()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐", id: 1001));
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(new[] { master }, master));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐" } }, "A");
        var profile = WorldOrderRuleCandidateCatalog.Build(world, world.Master!, "Master", specification);
        var candidate = profile.Requirements.Single().Candidates.Single(x => x.Operation == "MATCH");
        var input = new WorldOrderSymbolicJarVersion("a-jar", 0, "Master", "圆罐", null);
        var output = new WorldOrderSymbolicJarVersion("a-jar", 1, "Master", "圆罐", null, true);
        var node = WorldOrderSymbolicOperationNode.Rule(
            "a-match", 0, "Master", 0, candidate, input, output);
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { node },
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcretePlanAssembler.Assemble(
            100, program, binding, world, profile);

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.NotNull(result.SolveResult);
        Assert.True(result.SolveResult!.IsFeasible);
        Assert.NotNull(result.Manifest);
        Assert.Single(result.CandidatePlan!.MasterSteps);
    }

    [Fact]
    public void BClassJarInternalLiquidPassesMasterBanWithoutFreeLiquidTransfer()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", "酒", id: 1011));
        var slave = new Workshop("从台", new List<string> { "圆罐" });
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(
            new[] { master, slave }, master, slave));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } }, "B");
        var masterProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Slave!, "Slave", specification);
        var candidate = slaveProfile.Requirements.Single().Candidates.Single(x => x.Operation == "MATCH");
        var input = new WorldOrderSymbolicJarVersion("delivery", 0, "Master", "圆罐", "酒");
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 0, "Master", "Slave", new[] { input });
        var received = handoff.OutputJars.Single();
        var output = new WorldOrderSymbolicJarVersion("delivery", 2, "Slave", "圆罐", "酒", true);
        var slaveNode = WorldOrderSymbolicOperationNode.Rule(
            "slave-match", 1, "Slave", 0, candidate, received, output);
        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { handoff, slaveNode },
            new[] { WorldOrderSymbolicOperationDependency.JarState(
                handoff.NodeId, slaveNode.NodeId, "delivery", 1) });
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcretePlanAssembler.Assemble(
            101, program, binding, world, masterProfile, slaveProfile);

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Empty(result.Manifest!.TransferLiquids);
        Assert.Equal(new[] { 1011 }, result.Manifest.TransferJarIds);
        Assert.Empty(result.ResourceReplay!.TransferLiquids);
    }

    [Fact]
    public void ManifestUsesReplayWitnessWhenSlaveRejectsProducedLiquid()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐", "酒", id: 1021));
        var slave = new Workshop("从台", new List<string> { "圆罐" });
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(
            new[] { master, slave }, master, slave));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐" } }, "B");
        var masterProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Slave!, "Slave", specification);
        var candidate = slaveProfile.PreparatoryOperations.Single(x =>
            x.Operation == "PREPARE_POUR_OUT" && x.SourceShape == "圆罐" && x.SourceLiquid == "酒");
        var input = new WorldOrderSymbolicJarVersion("tool", 0, "Master", "圆罐", "酒");
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 0, "Master", "Slave", new[] { input });
        var received = handoff.OutputJars.Single();
        var output = new WorldOrderSymbolicJarVersion("tool", 2, "Slave", "圆罐", null, true);
        var slaveNode = WorldOrderSymbolicOperationNode.Rule(
            "slave-pour-out", 1, "Slave", null, candidate, received, output);
        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { handoff, slaveNode },
            new[] { WorldOrderSymbolicOperationDependency.JarState(
                handoff.NodeId, slaveNode.NodeId, "tool", 1) });
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcretePlanAssembler.Assemble(
            102, program, binding, world, masterProfile, slaveProfile,
            slaveRejectedLiquids: new[] { "酒" });

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.NotNull(result.SolveResult);
        Assert.Empty(result.Manifest!.TransferLiquids);
        Assert.Equal(0, result.Manifest.SlaveStagingPeakCapacity);
        Assert.Empty(result.CandidatePlan!.SlaveSteps.Single().AfterLiquids);
        Assert.Null(result.ResourceReplay!.SlaveAcceptedProducedLiquids.Single());
    }

    [Fact]
    public void ForgedCandidateCannotTurnProfileMismatchIntoAnInfeasibleResourceVerdict()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", id: 1031));
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(new[] { master }, master));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } }, "A");
        var profile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        var forged = new WorldOrderRuleCandidate(
            0, "圆罐", null, "POUR_IN", "forged.pour-in", 1,
            "圆罐", "酒", null, "酒", null, null);
        var input = new WorldOrderSymbolicJarVersion("forged-jar", 0, "Master", "圆罐", null);
        var output = new WorldOrderSymbolicJarVersion(
            "forged-jar", 1, "Master", "圆罐", "酒", true);
        var node = WorldOrderSymbolicOperationNode.Rule(
            "forged-pour-in", 0, "Master", 0, forged, input, output);
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { node },
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcretePlanAssembler.Assemble(
            103, program, binding, world, profile);

        Assert.Equal(WorldOrderSolveStatus.Undetermined, result.Status);
        Assert.Contains("不属于当前规则 Profile", result.Reason);
        Assert.Null(result.ResourceReplay);
    }

    [Fact]
    public void MasterStagingBanDoesNotExcludeFullReshapeCandidateForJarInternalLiquid()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", "酒", id: 1041));
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(new[] { master }, master));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "方罐" } }, "A");
        var profile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);

        Assert.Contains(profile.Requirements.Single().Candidates,
            x => x.Operation == "RESHAPE_FULL" && x.SourceShape == "圆罐" && x.SourceLiquid == "酒");
    }

    [Fact]
    public void BClassCanTransferJarAndSelfConsumeInternalLiquidEvenWhenBothRolesBanStaging()
    {
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", "酒", id: 1042));
        var slave = new Workshop("从台", new List<string> { "圆罐", "方罐" });
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(
            new[] { master, slave }, master, slave));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "方罐" } }, "B");
        var masterProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Slave!, "Slave", specification);

        Assert.Contains(masterProfile.Requirements.Single().Candidates,
            x => x.Operation == "RESHAPE_FULL" && x.SourceLiquid == "酒");
        var candidate = Assert.Single(slaveProfile.Requirements.Single().Candidates,
            x => x.Operation == "RESHAPE_FULL" && x.SourceShape == "圆罐" && x.SourceLiquid == "酒");

        var input = new WorldOrderSymbolicJarVersion("full-jar", 0, "Master", "圆罐", "酒");
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff-full-jar", 0, "Master", "Slave", new[] { input });
        var received = handoff.OutputJars.Single();
        var output = new WorldOrderSymbolicJarVersion(
            "full-jar", 2, "Slave", "方罐", null, true);
        var slaveNode = WorldOrderSymbolicOperationNode.Rule(
            "slave-full-reshape", 1, "Slave", 0, candidate, received, output);
        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { handoff, slaveNode },
            new[] { WorldOrderSymbolicOperationDependency.JarState(
                handoff.NodeId, slaveNode.NodeId, "full-jar", 1) });
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcretePlanAssembler.Assemble(
            105, program, binding, world, masterProfile, slaveProfile);

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Empty(result.Manifest!.TransferLiquids);
        Assert.Equal(new[] { 1042 }, result.Manifest.TransferJarIds);
        var step = Assert.Single(result.CandidatePlan!.SlaveSteps);
        Assert.Contains(step.Before!, x => x.Id == 1042);
        Assert.False(step.Before!.Single(x => x.Id == 1042).IsMarked);
        Assert.Equal("酒", step.Operation!.CostLiquid);
        Assert.Equal(1, step.Operation!.Consumed!["酒"]);
        Assert.Equal("方罐", step.After!.Single(x => x.Id == 1042).Shape);
        Assert.Null(step.After.Single(x => x.Id == 1042).Liquid);
    }
}

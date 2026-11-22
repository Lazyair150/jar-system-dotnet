using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.World;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderConcreteOperationMaterializerTests
{
    private static (WorldSnapshot World, JarOrderSpecification Specification,
        WorldOrderRuleCandidateProfile Master, WorldOrderRuleCandidateProfile Slave) CreateWorld(bool bClass)
    {
        var masterWorkshop = new Workshop("主台", new List<string> { "圆罐" });
        masterWorkshop.AddJar(new Jar("圆罐", id: 901));
        var slaveWorkshop = new Workshop("从台", new List<string> { "圆罐" });
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(
            new[] { masterWorkshop, slaveWorkshop }, masterWorkshop, slaveWorkshop));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐" } },
            bClass ? "B" : "A");
        var master = WorldOrderRuleCandidateCatalog.Build(world, world.Master!, "Master", specification);
        var slave = WorldOrderRuleCandidateCatalog.Build(world, world.Slave!, "Slave", specification);
        return (world, specification, master, slave);
    }

    [Fact]
    public void AClassMaterializesExactPhysicalJarAndRuleIdentity()
    {
        var (world, _, masterProfile, _) = CreateWorld(false);
        var candidate = masterProfile.Requirements.Single().Candidates.Single(x => x.Operation == "MATCH");
        var input = new WorldOrderSymbolicJarVersion("jar-a", 0, "Master", "圆罐", null);
        var output = new WorldOrderSymbolicJarVersion("jar-a", 1, "Master", "圆罐", null, true);
        var node = WorldOrderSymbolicOperationNode.Rule(
            "match-a", 0, "Master", 0, candidate, input, output);
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { node },
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcreteOperationMaterializer.Materialize(
            program, binding, world, masterProfile);

        Assert.Equal(WorldOrderConcreteMaterializationStatus.Complete, result.Status);
        var step = Assert.Single(result.MasterPath);
        Assert.Equal(901, step.Before![step.Operation!.JarIndex!.Value].Id);
        Assert.Equal("MATCH", step.Operation.Type);
        Assert.Equal(candidate.RuleId, step.Operation.RuleId);
        Assert.Equal(candidate.RuleVersion, step.Operation.RuleVersion);
        Assert.True(step.After![step.Operation.JarIndex!.Value].IsMarked);
        Assert.Empty(result.SlavePath);
        Assert.Empty(result.TransferJarIds);
    }

    [Fact]
    public void BClassMaterializesSlaveJarOnlyFromMasterHandoff()
    {
        var (world, _, masterProfile, slaveProfile) = CreateWorld(true);
        var candidate = slaveProfile.Requirements.Single().Candidates.Single(x => x.Operation == "MATCH");
        var initial = new WorldOrderSymbolicJarVersion("jar-b", 0, "Master", "圆罐", null);
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 0, "Master", "Slave", new[] { initial });
        var received = handoff.OutputJars.Single();
        var output = new WorldOrderSymbolicJarVersion("jar-b", 2, "Slave", "圆罐", null, true);
        var slaveNode = WorldOrderSymbolicOperationNode.Rule(
            "slave-match", 1, "Slave", 0, candidate, received, output);
        var dependency = WorldOrderSymbolicOperationDependency.JarState(
            handoff.NodeId, slaveNode.NodeId, "jar-b", 1);
        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { handoff, slaveNode }, new[] { dependency });
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcreteOperationMaterializer.Materialize(
            program, binding, world, masterProfile, slaveProfile);

        Assert.Equal(WorldOrderConcreteMaterializationStatus.Complete, result.Status);
        Assert.Empty(result.MasterPath);
        var step = Assert.Single(result.SlavePath);
        Assert.Equal(901, step.Before![step.Operation!.JarIndex!.Value].Id);
        Assert.True(step.Before[0].MasterOrigin);
        Assert.True(step.After![0].IsMarked);
        Assert.Equal(new[] { 901 }, result.TransferJarIds);
        Assert.Equal(candidate.RuleId, step.Operation.RuleId);
    }

    [Fact]
    public void HandoffDoesNotResetAnAlreadyUsedJarsOrderMark()
    {
        var (world, _, masterProfile, slaveProfile) = CreateWorld(true);
        var candidate = masterProfile.Requirements.Single().Candidates.Single(x => x.Operation == "MATCH");
        var initial = new WorldOrderSymbolicJarVersion("jar-c", 0, "Master", "圆罐", null);
        var operated = new WorldOrderSymbolicJarVersion("jar-c", 1, "Master", "圆罐", null, true);
        var masterNode = WorldOrderSymbolicOperationNode.Rule(
            "master-match", 0, "Master", 0, candidate, initial, operated);
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 1, "Master", "Slave", new[] { operated });
        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { masterNode, handoff },
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcreteOperationMaterializer.Materialize(
            program, binding, world, masterProfile, slaveProfile);

        Assert.Equal(WorldOrderConcreteMaterializationStatus.Complete, result.Status);
        Assert.True(Assert.Single(result.MasterPath).After![0].IsMarked);
        Assert.Equal(new[] { 901 }, result.TransferJarIds);
        Assert.Empty(result.SlavePath);
    }

    [Fact]
    public void RejectsCandidateNotPresentInTheCapturedRuleProfile()
    {
        var (world, _, masterProfile, _) = CreateWorld(false);
        var candidate = masterProfile.Requirements.Single().Candidates.Single(x => x.Operation == "MATCH")
            with { RuleId = "forged.rule" };
        var input = new WorldOrderSymbolicJarVersion("jar-forged", 0, "Master", "圆罐", null);
        var output = new WorldOrderSymbolicJarVersion("jar-forged", 1, "Master", "圆罐", null, true);
        var node = WorldOrderSymbolicOperationNode.Rule(
            "forged-match", 0, "Master", 0, candidate, input, output);
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { node },
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcreteOperationMaterializer.Materialize(
            program, binding, world, masterProfile);

        Assert.Equal(WorldOrderConcreteMaterializationStatus.Undetermined, result.Status);
        Assert.Contains("不属于当前规则 Profile", result.Reason);
    }

    [Fact]
    public void SlaveSnapshotCarriesMasterOperatedTransferredJarAtItsPostMasterState()
    {
        var masterWorkshop = new Workshop("主台", new List<string> { "圆罐" });
        masterWorkshop.AddJar(new Jar("圆罐", "酒", id: 911));
        masterWorkshop.AddJar(new Jar("圆罐", id: 912));
        var slaveWorkshop = new Workshop("从台", new List<string> { "圆罐" });
        var world = WorldSnapshotFactory.Capture(new WorkshopRoster(
            new[] { masterWorkshop, slaveWorkshop }, masterWorkshop, slaveWorkshop));
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐" } }, "B");
        var masterProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Master!, "Master", specification);
        var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
            world, world.Slave!, "Slave", specification);
        var prepare = masterProfile.PreparatoryOperations.Single(x =>
            x.Operation == "PREPARE_POUR_OUT" && x.SourceShape == "圆罐" && x.SourceLiquid == "酒");
        var match = slaveProfile.Requirements.Single().Candidates.Single(x => x.Operation == "MATCH");

        var jarAInput = new WorldOrderSymbolicJarVersion("jar-a", 0, "Master", "圆罐", "酒");
        var jarAOutput = new WorldOrderSymbolicJarVersion("jar-a", 1, "Master", "圆罐", null, true);
        var masterNode = WorldOrderSymbolicOperationNode.Rule(
            "prepare-a", 0, "Master", null, prepare, jarAInput, jarAOutput);
        var jarBInput = new WorldOrderSymbolicJarVersion("jar-b", 0, "Master", "圆罐", null);
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 1, "Master", "Slave", new[] { jarAOutput, jarBInput });
        var slaveInput = handoff.OutputJars.Single(x => x.JarKey == "jar-b");
        var slaveOutput = new WorldOrderSymbolicJarVersion("jar-b", 2, "Slave", "圆罐", null, true);
        var slaveNode = WorldOrderSymbolicOperationNode.Rule(
            "match-b", 2, "Slave", 0, match, slaveInput, slaveOutput);
        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { masterNode, handoff, slaveNode },
            new[] { WorldOrderSymbolicOperationDependency.JarState(
                handoff.NodeId, slaveNode.NodeId, "jar-b", 1) });
        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);

        var result = WorldOrderConcreteOperationMaterializer.Materialize(
            program, binding, world, masterProfile, slaveProfile);

        Assert.Equal(WorldOrderConcreteMaterializationStatus.Complete, result.Status);
        var slaveStep = Assert.Single(result.SlavePath);
        var slaveBefore = slaveStep.Before!;
        Assert.Equal(new[] { 911, 912 }, slaveBefore.Select(x => x.Id!.Value));
        Assert.True(slaveBefore[0].IsMarked);
        Assert.True(slaveBefore[0].MasterOrigin);
        Assert.False(slaveBefore[1].IsMarked);
        Assert.True(slaveBefore[1].MasterOrigin);
    }
}

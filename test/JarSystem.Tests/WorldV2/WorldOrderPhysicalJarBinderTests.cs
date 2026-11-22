using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.World;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderPhysicalJarBinderTests
{
    private static WorldOrderSymbolicOperationProgram SingleRuleProgram(
        string jarKey = "jar-a",
        string shape = "圆罐",
        string? liquid = null)
    {
        var input = new WorldOrderSymbolicJarVersion(jarKey, 0, "Master", shape, liquid);
        var candidate = new WorldOrderRuleCandidate(
            0, shape, liquid, "MATCH", "match-rule", 1,
            shape, liquid, null, null, null, null);
        var output = new WorldOrderSymbolicJarVersion(
            jarKey, 1, "Master", shape, liquid, isMarked: true);
        var operation = WorldOrderSymbolicOperationNode.Rule(
            "requirement-0-master", 0, "Master", 0, candidate, input, output);
        return new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { operation },
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());
    }

    private static WorldOrderSymbolicOperationProgram SlaveProgram()
    {
        var initial = new WorldOrderSymbolicJarVersion(
            "requirement-0", 0, "Master", "圆罐", "酒");
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "master-to-slave-handoff", 0, "Master", "Slave", new[] { initial });
        var slaveInput = handoff.OutputJars.Single();
        var candidate = new WorldOrderRuleCandidate(
            0, "圆罐", "酒", "MATCH", "match-rule", 1,
            "圆罐", "酒", null, null, null, null);
        var slaveOutput = new WorldOrderSymbolicJarVersion(
            "requirement-0", 2, "Slave", "圆罐", "酒", isMarked: true);
        var operation = WorldOrderSymbolicOperationNode.Rule(
            "requirement-0-slave", 1, "Slave", 0, candidate, slaveInput, slaveOutput);
        var dependency = WorldOrderSymbolicOperationDependency.JarState(
            handoff.NodeId, operation.NodeId, "requirement-0", 1);
        return new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", new[] { handoff, operation }, new[] { dependency });
    }

    private static WorkshopSolveSnapshot Snapshot(params Jar[] jars)
    {
        var master = new Workshop("主台");
        foreach (var jar in jars) master.AddJar(jar);
        return WorldSnapshotFactory.Capture(new WorkshopRoster(
            new List<Workshop> { master }, master)).Master!;
    }

    [Fact]
    public void BindsSymbolicJarToExactUnmarkedPhysicalState()
    {
        var snapshot = Snapshot(
            new Jar("圆罐", "酒"),
            new Jar("方罐", "蜜"),
            new Jar("圆罐", "酒"));
        var program = SingleRuleProgram(shape: "圆罐", liquid: "酒");

        var result = WorldOrderPhysicalJarBinder.Bind(program, snapshot);

        Assert.Equal(WorldOrderPhysicalJarBindingStatus.Complete, result.Status);
        Assert.Equal(1, Assert.Single(result.JarIds).Value);
    }

    [Fact]
    public void DistinctSymbolicJarsRequireDistinctPhysicalIdentities()
    {
        var first = new WorldOrderSymbolicJarVersion("jar-a", 0, "Master", "圆罐", null);
        var second = new WorldOrderSymbolicJarVersion("jar-b", 0, "Master", "圆罐", null);
        var firstCandidate = new WorldOrderRuleCandidate(
            0, "圆罐", null, "MATCH", "match-a", 1, "圆罐", null, null, null, null, null);
        var secondCandidate = firstCandidate with { RequirementIndex = 1, RuleId = "match-b" };
        var firstNode = WorldOrderSymbolicOperationNode.Rule(
            "requirement-0-master", 0, "Master", 0, firstCandidate, first,
            new WorldOrderSymbolicJarVersion("jar-a", 1, "Master", "圆罐", null, true));
        var secondNode = WorldOrderSymbolicOperationNode.Rule(
            "requirement-1-master", 1, "Master", 1, secondCandidate, second,
            new WorldOrderSymbolicJarVersion("jar-b", 1, "Master", "圆罐", null, true));
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave", new[] { firstNode, secondNode },
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());

        var enough = WorldOrderPhysicalJarBinder.Bind(
            program, Snapshot(new Jar("圆罐"), new Jar("圆罐")));
        var insufficient = WorldOrderPhysicalJarBinder.Bind(
            program, Snapshot(new Jar("圆罐")));

        Assert.Equal(WorldOrderPhysicalJarBindingStatus.Complete, enough.Status);
        Assert.Equal(2, enough.JarIds.Values.Distinct().Count());
        Assert.Equal(WorldOrderPhysicalJarBindingStatus.Infeasible, insufficient.Status);
        Assert.Empty(insufficient.JarIds);
    }

    [Fact]
    public void MarkedPhysicalJarCannotBeBoundForAnotherOperation()
    {
        var jar = new Jar("圆罐") { IsMarked = true };
        var result = WorldOrderPhysicalJarBinder.Bind(
            SingleRuleProgram(), Snapshot(jar));

        Assert.Equal(WorldOrderPhysicalJarBindingStatus.Infeasible, result.Status);
    }

    [Fact]
    public void SlaveJarIsBoundToMasterOriginBeforeHandoffAndKeepsSameIdentity()
    {
        var result = WorldOrderPhysicalJarBinder.Bind(
            SlaveProgram(), Snapshot(new Jar("圆罐", "酒")));

        Assert.Equal(WorldOrderPhysicalJarBindingStatus.Complete, result.Status);
        Assert.Equal(1, Assert.Single(result.JarIds).Value);
        Assert.Equal("requirement-0", result.JarIds.Keys.Single());
    }

    [Fact]
    public void ProgramWithNoRuleOperationsProducesAnEmptyCompleteBinding()
    {
        var program = new WorldOrderSymbolicOperationProgram(
            false, "Master", "Slave",
            System.Array.Empty<WorldOrderSymbolicOperationNode>(),
            System.Array.Empty<WorldOrderSymbolicOperationDependency>());

        var result = WorldOrderPhysicalJarBinder.Bind(program, Snapshot());

        Assert.Equal(WorldOrderPhysicalJarBindingStatus.Complete, result.Status);
        Assert.Empty(result.JarIds);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderSlaveBoundaryProjectorTests
{
    private sealed record StepSpec(string? Consumed, string? Produced, string? JarLiquid = null);

    private static WorldOrderSymbolicOperationProgram Program(params StepSpec[] specs)
    {
        var inputs = specs.Select((spec, index) =>
            new WorldOrderSymbolicJarVersion(
                "jar-" + index, 0, "Master", "shape-" + index, spec.JarLiquid)).ToArray();
        var handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
            "handoff", 0, "Master", "Slave", inputs);
        var nodes = new List<WorldOrderSymbolicOperationNode> { handoff };
        var edges = new List<WorldOrderSymbolicOperationDependency>();
        var earlierProducers = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < specs.Length; index++)
        {
            var spec = specs[index];
            var jarKey = "jar-" + index;
            var input = new WorldOrderSymbolicJarVersion(
                jarKey, 1, "Slave", "shape-" + index, spec.JarLiquid);
            var output = new WorldOrderSymbolicJarVersion(
                jarKey, 2, "Slave", "shape-" + index, spec.JarLiquid);
            var candidate = new WorldOrderRuleCandidate(
                index, input.Shape, input.Liquid,
                "TEST", "test-rule-" + index, 1,
                output.Shape, output.Liquid, null,
                spec.Consumed, spec.Produced, null);
            var nodeId = "slave-op-" + index;
            nodes.Add(WorldOrderSymbolicOperationNode.Rule(
                nodeId, index + 1, "Slave", index, candidate, input, output));
            edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                handoff.NodeId, nodeId, jarKey, input.Revision));

            if (spec.Consumed != null)
            {
                if (earlierProducers.TryGetValue(spec.Consumed, out var producer))
                    edges.Add(WorldOrderSymbolicOperationDependency.FreeLiquid(
                        producer, nodeId, spec.Consumed));
                else
                    edges.Add(WorldOrderSymbolicOperationDependency.FreeLiquid(
                        handoff.NodeId, nodeId, spec.Consumed));
            }

            if (spec.Produced != null)
                earlierProducers[spec.Produced] = nodeId;
        }

        return new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave", nodes, edges);
    }

    [Fact]
    public void DirectSlaveConsumptionBecomesOneFreeLiquidBoundaryRequirement()
    {
        var result = WorldOrderSlaveBoundaryProjector.Project(
            Program(new StepSpec("X", null)),
            "direct", 4);

        Assert.True(result.IsProjected);
        Assert.Equal(1, result.Group!.RequiredFreeLiquid["X"]);
        Assert.Equal("slave-op-0", result.Group.Requirements.Single(x =>
            x.Kind == WorldOrderBoundaryResourceKind.FreeLiquid).SourceOperationId);
    }

    [Fact]
    public void EarlierSlaveProductionSatisfiesLaterConsumptionWithoutMasterSupply()
    {
        var result = WorldOrderSlaveBoundaryProjector.Project(
            Program(new StepSpec(null, "X"), new StepSpec("X", null)),
            "local-production", 1,
            masterBannedLiquids: new[] { "X" });

        Assert.True(result.IsProjected);
        Assert.Empty(result.Group!.RequiredFreeLiquid);
        Assert.Equal(2, result.Group.Requirements.Count);
        Assert.All(result.Group.Requirements, x =>
            Assert.Equal(WorldOrderBoundaryResourceKind.JarState, x.Kind));
    }

    [Fact]
    public void MasterBanRejectsOnlyRequiredFreeLiquidAndDoesNotRejectJarInternalLiquid()
    {
        var freeLiquid = WorldOrderSlaveBoundaryProjector.Project(
            Program(new StepSpec("forbidden", null)),
            "banned-free", 2,
            masterBannedLiquids: new[] { "forbidden" });

        Assert.Equal(WorldOrderSlaveBoundaryProjectionStatus.Infeasible, freeLiquid.Status);
        Assert.Contains("主台 staging", freeLiquid.Reason);

        var jar = WorldOrderSlaveBoundaryProjector.Project(
            Program(new StepSpec(null, null, "forbidden")),
            "jar-is-independent", 2,
            masterBannedLiquids: new[] { "forbidden" });

        Assert.True(jar.IsProjected);
        Assert.Equal("forbidden", Assert.Single(jar.Group!.Requirements).JarLiquid);
        Assert.Empty(jar.Group.RequiredFreeLiquid);
    }

    [Fact]
    public void SlaveRejectedLiquidCannotBeSatisfiedByBoundaryOrLocalProduction()
    {
        var result = WorldOrderSlaveBoundaryProjector.Project(
            Program(new StepSpec("X", null)),
            "slave-rejects", 2,
            slaveRejectedLiquids: new[] { "X" });

        Assert.Equal(WorldOrderSlaveBoundaryProjectionStatus.Infeasible, result.Status);
        Assert.Contains("从台拒收", result.Reason);
    }

    [Fact]
    public void AggregateStagingCapacityCanMakeTheDerivedBoundaryInfeasible()
    {
        var result = WorldOrderSlaveBoundaryProjector.Project(
            Program(
                new StepSpec(null, "Y"),
                new StepSpec("X", null),
                new StepSpec("Y", null)),
            "capacity-conflict", 1);

        Assert.Equal(WorldOrderSlaveBoundaryProjectionStatus.Infeasible, result.Status);
        Assert.Contains("暂存容量", result.Reason);
    }

    [Fact]
    public void BranchWithNoBoundaryResourcesProducesAnEmptyConjunction()
    {
        var program = new WorldOrderSymbolicOperationProgram(
            true, "Master", "Slave",
            new[] { WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
                "handoff", 0, "Master", "Slave",
                Array.Empty<WorldOrderSymbolicJarVersion>()) },
            Array.Empty<WorldOrderSymbolicOperationDependency>());

        var result = WorldOrderSlaveBoundaryProjector.Project(program, "empty", 0);

        Assert.True(result.IsProjected);
        Assert.Empty(result.Group!.Requirements);
        Assert.Equal("Master", result.Group.ProviderRole);
        Assert.Equal("Slave", result.Group.ConsumerRole);
    }
}

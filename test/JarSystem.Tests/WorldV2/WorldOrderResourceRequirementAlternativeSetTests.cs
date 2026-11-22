using System;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderResourceRequirementAlternativeSetTests
{
    [Fact]
    public void Groups_AreAlternatives_AndRequirementsInsideEachGroupAreConjunctive()
    {
        var xy = new WorldOrderResourceRequirementGroup("path-xy", new[]
        {
            WorldOrderBoundaryRequirement.FreeLiquid("Master", "Slave", "X", 3, "slave-op-1", 0),
            WorldOrderBoundaryRequirement.FreeLiquid("Master", "Slave", "Y", 2, "slave-op-2", 1)
        });
        var z = new WorldOrderResourceRequirementGroup("path-z", new[]
        {
            WorldOrderBoundaryRequirement.FreeLiquid("Master", "Slave", "Z", 5, "slave-op-3", 1)
        });

        var alternatives = new WorldOrderResourceRequirementAlternativeSet(
            WorldOrderResourceRequirementSetStatus.Complete,
            "rules-v1", "workshops-v1", "spec-v1", "Master", "Slave", new[] { xy, z });

        Assert.True(alternatives.IsComplete);
        Assert.Equal(2, alternatives.Groups.Count);
        Assert.Equal(2, alternatives.Groups[0].Requirements.Count);
        Assert.Equal("X", alternatives.Groups[0].Requirements[0].Liquid);
        Assert.Equal("Y", alternatives.Groups[0].Requirements[1].Liquid);
        Assert.Equal("Z", alternatives.Groups[1].Requirements[0].Liquid);
    }

    [Fact]
    public void JarBoundary_PreservesJarStateInsteadOfFlatteningItIntoLiquidAmount()
    {
        var requirement = WorldOrderBoundaryRequirement.JarState(
            "Master", "Slave", "wide", "blue", "slave-prepare-1", 0);

        Assert.Equal(WorldOrderBoundaryResourceKind.JarState, requirement.Kind);
        Assert.Equal("wide", requirement.JarShape);
        Assert.Equal("blue", requirement.JarLiquid);
        Assert.Equal(1, requirement.Amount);
        Assert.Null(requirement.Liquid);
    }


    [Fact]
    public void GroupAggregatesRepeatedLiquidRequirementsAgainstOneSharedBalance()
    {
        var group = new WorldOrderResourceRequirementGroup("path", new[]
        {
            WorldOrderBoundaryRequirement.FreeLiquid("Master", "Slave", "X", 3, "op-1", 0),
            WorldOrderBoundaryRequirement.FreeLiquid("Master", "Slave", "X", 2, "op-2", 1),
            WorldOrderBoundaryRequirement.JarState("Master", "Slave", "wide", "blue", "op-3", 2),
            WorldOrderBoundaryRequirement.JarState("Master", "Slave", "wide", "blue", "op-4", 3)
        });

        Assert.Equal(5, group.RequiredFreeLiquid["X"]);
        Assert.Equal(2, group.RequiredJarStates[new WorldOrderJarStateKey("wide", "blue")]);
        Assert.Equal(4, group.Requirements.Count);
    }

    [Fact]
    public void AlternativeSet_RejectsMixedBoundaryDirections()
    {
        var masterToSlave = new WorldOrderResourceRequirementGroup("a", new[]
        {
            WorldOrderBoundaryRequirement.FreeLiquid("Master", "Slave", "X", 1, "op-a")
        });
        var otherDirection = new WorldOrderResourceRequirementGroup("b", new[]
        {
            WorldOrderBoundaryRequirement.FreeLiquid("Slave", "Master", "Y", 1, "op-b")
        });

        Assert.Throws<ArgumentException>(() => new WorldOrderResourceRequirementAlternativeSet(
            WorldOrderResourceRequirementSetStatus.Complete,
            "rules-v1", "workshops-v1", "spec-v1", "Master", "Slave",
            new[] { masterToSlave, otherDirection }));
    }

    [Fact]
    public void CompleteEmptySet_IsNotSilentlyTreatedAsInfeasible()
    {
        Assert.Throws<ArgumentException>(() => new WorldOrderResourceRequirementAlternativeSet(
            WorldOrderResourceRequirementSetStatus.Complete,
            "rules-v1", "workshops-v1", "spec-v1", "Master", "Slave",
            Array.Empty<WorldOrderResourceRequirementGroup>()));
    }

    [Fact]
    public void UndeterminedMayCarryPartialGroups_ButDoesNotClaimCompleteness()
    {
        var partial = new WorldOrderResourceRequirementGroup("partial", new[]
        {
            WorldOrderBoundaryRequirement.FreeLiquid("Master", "Slave", "X", 1, "slave-op")
        });

        var alternatives = new WorldOrderResourceRequirementAlternativeSet(
            WorldOrderResourceRequirementSetStatus.Undetermined,
            "rules-v1", "workshops-v1", "spec-v1", "Master", "Slave", new[] { partial },
            "decomposition budget exhausted");

        Assert.True(alternatives.IsUndetermined);
        Assert.False(alternatives.IsComplete);
        Assert.Single(alternatives.Groups);
    }
}

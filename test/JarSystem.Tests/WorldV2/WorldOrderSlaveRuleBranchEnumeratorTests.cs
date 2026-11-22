using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderSlaveRuleBranchEnumeratorTests
{
    private static WorldOrderRuleCandidate Candidate(
        int requirement,
        string sourceShape,
        string? sourceLiquid,
        string targetShape,
        string? targetLiquid,
        string? consumedFree = null,
        string? producedFree = null) =>
        new(requirement, sourceShape, sourceLiquid, "TEST", "rule-" + requirement,
            1, targetShape, targetLiquid, null, consumedFree, producedFree, null);

    private static WorldOrderRuleCandidateProfile Profile(
        params (string Shape, string? Liquid, WorldOrderRuleCandidate[] Candidates)[] requirements) =>
        Profile(requirements, null);

    private static WorldOrderRuleCandidateProfile Profile(
        (string Shape, string? Liquid, WorldOrderRuleCandidate[] Candidates)[] requirements,
        WorldOrderRuleCandidate[]? preparations)
    {
        var entries = requirements.Select((item, index) =>
            new WorldOrderRequirementCandidates(
                index, item.Shape, item.Liquid, item.Candidates));
        return new WorldOrderRuleCandidateProfile(
            "SlaveWorkshop", "Slave", "rules-v1", "workshops-v1", "spec-v1",
            entries, preparations);
    }

    [Fact]
    public void EachCandidateAssignmentBecomesItsOwnAndGroupAndAlternativesAreOr()
    {
        var profile = Profile((
            "target", null,
            new[]
            {
                Candidate(0, "source-a", null, "target", null),
                Candidate(0, "source-b", "inside-b", "target", null)
            }));

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", slaveStagingCapacity: 4);

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Complete, result.Status);
        Assert.Equal(2, result.ExploredBranches);
        Assert.Equal(2, result.Alternatives!.Groups.Count);
        Assert.All(result.Alternatives.Groups, group =>
        {
            Assert.Single(group.RequiredJarStates);
            Assert.Equal("Master", group.ProviderRole);
            Assert.Equal("Slave", group.ConsumerRole);
        });
        Assert.Contains(result.Alternatives.Groups, group =>
            group.Requirements.Any(x => x.Kind == WorldOrderBoundaryResourceKind.JarState &&
                                        x.JarLiquid == "inside-b"));
    }

    [Fact]
    public void MasterBanRejectsOnlyFreeLiquidBranchAndKeepsJarInternalLiquidBranch()
    {
        var profile = Profile((
            "target", null,
            new[]
            {
                Candidate(0, "source-a", null, "target", null, consumedFree: "forbidden"),
                Candidate(0, "source-b", "forbidden", "target", null)
            }));

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", 4,
            masterBannedLiquids: new[] { "forbidden" });

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Complete, result.Status);
        var group = Assert.Single(result.Alternatives!.Groups);
        Assert.DoesNotContain(group.Requirements,
            x => x.Kind == WorldOrderBoundaryResourceKind.FreeLiquid);
        Assert.Contains(group.Requirements,
            x => x.Kind == WorldOrderBoundaryResourceKind.JarState &&
                 x.JarLiquid == "forbidden");
    }

    [Fact]
    public void EarlierSlaveProductionSatisfiesLaterDemandWithoutBoundaryLiquid()
    {
        var profile = Profile(
            ("empty-a", null, new[]
            {
                Candidate(0, "source-a", null, "empty-a", null, producedFree: "X")
            }),
            ("target-b", null, new[]
            {
                Candidate(1, "source-b", null, "target-b", null, consumedFree: "X")
            }));

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", 1,
            masterBannedLiquids: new[] { "X" });

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Complete, result.Status);
        var group = Assert.Single(result.Alternatives!.Groups);
        Assert.Empty(group.RequiredFreeLiquid);
        Assert.Equal(2, group.RequiredJarStates.Values.Sum());
    }

    [Fact]
    public void BudgetExhaustionNeverClaimsCompleteAlternatives()
    {
        var profile = Profile((
            "target", null,
            new[]
            {
                Candidate(0, "source-a", null, "target", null),
                Candidate(0, "source-b", null, "target", null)
            }));

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", 4, maxBranches: 1);

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Undetermined, result.Status);
        Assert.False(result.Alternatives?.IsComplete ?? false);
        Assert.Contains("预算", result.Reason);
    }


    [Fact]
    public void SameJarSlavePreparationCreatesAnAlternativeThatAvoidsMasterFreeLiquidBoundary()
    {
        var requirement = Candidate(
            0, "round", null, "target", null, consumedFree: "X");
        var preparation = new WorldOrderRuleCandidate(
            -1, "round", "X", "PREPARE_POUR_OUT", "prepare-pour-out",
            1, "round", null, null, null, "X", null);
        var profile = Profile(
            new[] { ("target", (string?)null, new[] { requirement }) },
            new[] { preparation });

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", 2, masterBannedLiquids: new[] { "X" });

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Complete, result.Status);
        // 9h also considers an independent source Jar of the same catalogued state.
        // That is a distinct branch from preparing the requirement Jar in place.
        Assert.Equal(3, result.ExploredBranches);
        Assert.Equal(2, result.Alternatives!.Groups.Count);
        var sameJarGroup = Assert.Single(
            result.Alternatives.Groups,
            group => group.RequiredJarStates.Values.Sum() == 1);
        Assert.Empty(sameJarGroup.RequiredFreeLiquid);
        Assert.Single(sameJarGroup.RequiredJarStates);
        Assert.Contains(result.Alternatives.Groups, group =>
            group.RequiredJarStates.Values.Sum() == 2);
    }

    [Fact]
    public void PreparatoryBranchBudgetExhaustionRemainsUndetermined()
    {
        var requirement = Candidate(
            0, "round", null, "target", null, consumedFree: "X");
        var preparation = new WorldOrderRuleCandidate(
            -1, "round", "X", "PREPARE_POUR_OUT", "prepare-pour-out",
            1, "round", null, null, null, "X", null);
        var profile = Profile(
            new[] { ("target", (string?)null, new[] { requirement }) },
            new[] { preparation });

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", 2, maxBranches: 1);

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Undetermined, result.Status);
        Assert.False(result.Alternatives?.IsComplete ?? false);
    }


    [Fact]
    public void IndependentPreparationJarCanProduceLiquidForAnotherRequirementJar()
    {
        var requirement = Candidate(
            0, "target-jar", null, "finished", null, consumedFree: "X");
        var preparation = new WorldOrderRuleCandidate(
            -1, "liquid-source-jar", "X", "PREPARE_POUR_OUT", "prepare-source-X",
            1, "liquid-source-jar", null, null, null, "X", null);
        var profile = Profile(
            new[] { ("finished", (string?)null, new[] { requirement }) },
            new[] { preparation });

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", 2, masterBannedLiquids: new[] { "X" });

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Complete, result.Status);
        Assert.Equal(2, result.ExploredBranches);
        var group = Assert.Single(result.Alternatives!.Groups);
        Assert.Empty(group.RequiredFreeLiquid);
        Assert.Equal(2, group.RequiredJarStates.Values.Sum());
        Assert.Contains(group.Requirements, x =>
            x.Kind == WorldOrderBoundaryResourceKind.JarState &&
            x.JarShape == "liquid-source-jar" &&
            x.JarLiquid == "X");
    }

    [Fact]
    public void IndependentPreparationEnumerationIsBoundedByBranchAndNodeBudgets()
    {
        var requirement = Candidate(
            0, "target-jar", null, "finished", null, consumedFree: "X");
        var preparations = new[]
        {
            new WorldOrderRuleCandidate(-1, "source-a", "X", "PREPARE_POUR_OUT",
                "prepare-a", 1, "source-a", null, null, null, "X", null),
            new WorldOrderRuleCandidate(-1, "source-b", "X", "PREPARE_POUR_OUT",
                "prepare-b", 1, "source-b", null, null, null, "X", null)
        };
        var profile = Profile(
            new[] { ("finished", (string?)null, new[] { requirement }) },
            preparations);

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", 3, maxBranches: 1);

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Undetermined, result.Status);
        Assert.False(result.Alternatives?.IsComplete ?? false);
    }

    [Fact]
    public void TwoIndependentPreparationJarsMustCoverAggregatedSameLiquidDemand()
    {
        var requirementA = Candidate(
            0, "requirement-a", null, "finished-a", null, consumedFree: "X");
        var requirementB = Candidate(
            1, "requirement-b", null, "finished-b", null, consumedFree: "X");
        var preparationA = new WorldOrderRuleCandidate(
            -1, "source-a", "X", "PREPARE_POUR_OUT", "prepare-a",
            1, "source-a", null, null, null, "X", null);
        var preparationB = new WorldOrderRuleCandidate(
            -1, "source-b", "X", "PREPARE_POUR_OUT", "prepare-b",
            1, "source-b", null, null, null, "X", null);
        var profile = Profile(
            new[]
            {
                ("finished-a", (string?)null, new[] { requirementA }),
                ("finished-b", (string?)null, new[] { requirementB })
            },
            new[] { preparationA, preparationB });

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", 2, masterBannedLiquids: new[] { "X" });

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Complete, result.Status);
        Assert.Equal(4, result.ExploredBranches);
        var group = Assert.Single(result.Alternatives!.Groups);
        Assert.Empty(group.RequiredFreeLiquid);
        // Two requirement Jars plus two distinct resource-producing Jars cross the handoff.
        Assert.Equal(4, group.RequiredJarStates.Values.Sum());
    }

    [Fact]
    public void NodeBudgetExhaustionNeverClaimsCompleteAlternatives()
    {
        var requirement = Candidate(
            0, "target-jar", null, "finished", null, consumedFree: "X");
        var preparation = new WorldOrderRuleCandidate(
            -1, "liquid-source-jar", "X", "PREPARE_POUR_OUT", "prepare-source-X",
            1, "liquid-source-jar", null, null, null, "X", null);
        var profile = Profile(
            new[] { ("finished", (string?)null, new[] { requirement }) },
            new[] { preparation });

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(
            profile, "Master", 2, maxNodes: 1);

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Undetermined, result.Status);
        Assert.False(result.Alternatives?.IsComplete ?? false);
    }

    [Fact]
    public void OpaqueProfileRemainsUndetermined()
    {
        var profile = new WorldOrderRuleCandidateProfile(
            "SlaveWorkshop", "Slave", "rules-v1", "workshops-v1", "spec-v1",
            System.Array.Empty<WorldOrderRequirementCandidates>(),
            isUndetermined: true, reason: "opaque");

        var result = WorldOrderSlaveRuleBranchEnumerator.Enumerate(profile, "Master", 4);

        Assert.Equal(WorldOrderSlaveRuleBranchEnumerationStatus.Undetermined, result.Status);
        Assert.Null(result.Alternatives);
    }
}

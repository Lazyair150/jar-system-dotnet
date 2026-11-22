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
        params (string Shape, string? Liquid, WorldOrderRuleCandidate[] Candidates)[] requirements)
    {
        var entries = requirements.Select((item, index) =>
            new WorldOrderRequirementCandidates(
                index, item.Shape, item.Liquid, item.Candidates));
        return new WorldOrderRuleCandidateProfile(
            "SlaveWorkshop", "Slave", "rules-v1", "workshops-v1", "spec-v1", entries);
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

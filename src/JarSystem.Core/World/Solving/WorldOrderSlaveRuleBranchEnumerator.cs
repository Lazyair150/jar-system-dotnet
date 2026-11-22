using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Solving;

public enum WorldOrderSlaveRuleBranchEnumerationStatus
{
    Complete,
    Infeasible,
    Undetermined
}

/// <summary>
/// Result of enumerating Slave-only direct rule-candidate branches and projecting
/// each branch to one independent Master-to-Slave boundary AND group.
/// </summary>
public sealed class WorldOrderSlaveRuleBranchEnumerationResult
{
    public WorldOrderSlaveRuleBranchEnumerationStatus Status { get; }
    public WorldOrderResourceRequirementAlternativeSet? Alternatives { get; }
    public int ExploredBranches { get; }
    public int RejectedBranches { get; }
    public string? Reason { get; }

    public bool IsComplete => Status == WorldOrderSlaveRuleBranchEnumerationStatus.Complete;
    public bool IsUndetermined => Status == WorldOrderSlaveRuleBranchEnumerationStatus.Undetermined;

    internal WorldOrderSlaveRuleBranchEnumerationResult(
        WorldOrderSlaveRuleBranchEnumerationStatus status,
        WorldOrderResourceRequirementAlternativeSet? alternatives,
        int exploredBranches,
        int rejectedBranches,
        string? reason)
    {
        Status = status;
        Alternatives = alternatives;
        ExploredBranches = exploredBranches;
        RejectedBranches = rejectedBranches;
        Reason = reason;
    }
}

/// <summary>
/// Enumerates direct Slave rule candidates together with compatible same-Jar
/// PREPARE_POUR_OUT assignments. Every resulting program is projected independently;
/// no candidate is sent to a Master rule solver.
/// </summary>
/// <remarks>
/// This slice supports at most one catalogued PourOut preparation on the same Jar
/// immediately before its direct requirement rule. It does not yet synthesize arbitrary
/// multi-Jar preparation chains or recursively expand intermediate requirement states.
/// A profile that cannot describe a rule statically, or a budget-exhausted enumeration,
/// is never reported as complete.
/// </remarks>
public static class WorldOrderSlaveRuleBranchEnumerator
{
    public static WorldOrderSlaveRuleBranchEnumerationResult Enumerate(
        WorldOrderRuleCandidateProfile slaveProfile,
        string masterRole,
        int slaveStagingCapacity,
        IEnumerable<string>? masterBannedLiquids = null,
        IEnumerable<string>? slaveRejectedLiquids = null,
        int maxBranches = 256,
        int maxNodes = 100_000)
    {
        if (slaveProfile == null) throw new ArgumentNullException(nameof(slaveProfile));
        if (string.IsNullOrWhiteSpace(masterRole))
            throw new ArgumentException("主台角色不能为空", nameof(masterRole));
        if (slaveProfile.Role == masterRole)
            throw new ArgumentException("从台 Profile 不能使用主台角色", nameof(slaveProfile));
        if (slaveStagingCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(slaveStagingCapacity));
        if (maxBranches <= 0) throw new ArgumentOutOfRangeException(nameof(maxBranches));
        if (maxNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maxNodes));

        var banned = CopyNames(masterBannedLiquids, nameof(masterBannedLiquids));
        var rejected = CopyNames(slaveRejectedLiquids, nameof(slaveRejectedLiquids));
        if (slaveProfile.IsUndetermined)
            return Result(WorldOrderSlaveRuleBranchEnumerationStatus.Undetermined,
                null, 0, 0, slaveProfile.Reason ?? "从台规则候选 Profile 未能确定");

        if (slaveProfile.Requirements.Any(x => x.Candidates.Count == 0))
            return Result(WorldOrderSlaveRuleBranchEnumerationStatus.Infeasible,
                null, 0, 0, "至少一项最终需求在从台规则 Profile 中没有直接规则候选");

        var choices = slaveProfile.Requirements
            .OrderBy(x => x.RequirementIndex)
            .Select(x => x.Candidates
                .OrderBy(c => c.SourceShape, StringComparer.Ordinal)
                .ThenBy(c => c.SourceLiquid, StringComparer.Ordinal)
                .ThenBy(c => c.Operation, StringComparer.Ordinal)
                .ThenBy(c => c.RuleId, StringComparer.Ordinal)
                .ToArray())
            .ToArray();

        var selected = new WorldOrderRuleCandidate[choices.Length];
        var groups = new List<WorldOrderResourceRequirementGroup>();
        var exploredNodes = 0;
        var exploredBranches = 0;
        var rejectedBranches = 0;
        var budgetExceeded = false;
        string? lastInfeasibleReason = null;

        void Visit(int requirementPosition)
        {
            if (budgetExceeded) return;
            if (requirementPosition == choices.Length)
            {
                if (exploredBranches >= maxBranches)
                {
                    budgetExceeded = true;
                    return;
                }

                var assignments = selected.Select(candidate =>
                    new WorldOrderAssignedRuleCandidate(slaveProfile.Role, candidate)).ToArray();
                var solution = new WorldOrderSymbolicSolution(assignments);
                var prepChoices = selected.Select(candidate =>
                    slaveProfile.PreparatoryOperations
                        .Where(prep => IsCompatiblePreparation(candidate, prep))
                        .OrderBy(prep => prep.SourceLiquid, StringComparer.Ordinal)
                        .ThenBy(prep => prep.RuleId, StringComparer.Ordinal)
                        .ToArray()).ToArray();
                var selectedPreparations = new List<WorldOrderPreparatoryOperationAssignment>();

                void VisitPreparations(int position)
                {
                    if (budgetExceeded) return;
                    if (position == prepChoices.Length)
                    {
                        if (exploredBranches >= maxBranches)
                        {
                            budgetExceeded = true;
                            return;
                        }

                        exploredBranches++;
                        try
                        {
                            var program = WorldOrderPreparatoryProgramBuilder.Build(
                                solution, selectedPreparations, true, masterRole, slaveProfile.Role);
                            var projection = WorldOrderSlaveBoundaryProjector.Project(
                                program, "slave-branch-" + exploredBranches,
                                slaveStagingCapacity, banned, rejected);
                            if (projection.IsProjected)
                                groups.Add(projection.Group!);
                            else
                            {
                                rejectedBranches++;
                                lastInfeasibleReason = projection.Reason;
                            }
                        }
                        catch (ArgumentException exception)
                        {
                            rejectedBranches++;
                            lastInfeasibleReason = exception.Message;
                        }
                        return;
                    }

                    // First enumerate the direct branch without a preparation.
                    VisitPreparations(position + 1);
                    foreach (var preparation in prepChoices[position])
                    {
                        if (budgetExceeded) return;
                        selectedPreparations.Add(new WorldOrderPreparatoryOperationAssignment(
                            slaveProfile.Role,
                            "requirement-" + selected[position].RequirementIndex,
                            preparation));
                        VisitPreparations(position + 1);
                        selectedPreparations.RemoveAt(selectedPreparations.Count - 1);
                    }
                }

                VisitPreparations(0);
                return;
            }

            foreach (var candidate in choices[requirementPosition])
            {
                if (exploredNodes >= maxNodes)
                {
                    budgetExceeded = true;
                    return;
                }
                exploredNodes++;
                selected[requirementPosition] = candidate;
                Visit(requirementPosition + 1);
                if (budgetExceeded) return;
            }
        }

        Visit(0);
        if (budgetExceeded)
            return Result(WorldOrderSlaveRuleBranchEnumerationStatus.Undetermined,
                groups.Count == 0 ? null : CreateAlternatives(
                    WorldOrderResourceRequirementSetStatus.Undetermined,
                    slaveProfile, masterRole, groups, banned,
                    "从台规则分支枚举达到预算上限，当前资源组只是部分结果"),
                exploredBranches, rejectedBranches,
                "从台规则分支枚举达到节点或分支预算上限；不得将部分资源组视为完整 OR 集合");

        if (groups.Count == 0)
            return Result(WorldOrderSlaveRuleBranchEnumerationStatus.Infeasible,
                CreateAlternatives(
                    WorldOrderResourceRequirementSetStatus.Infeasible,
                    slaveProfile, masterRole, Array.Empty<WorldOrderResourceRequirementGroup>(),
                    banned, lastInfeasibleReason ?? "所有从台候选分支均不可行"),
                exploredBranches, rejectedBranches,
                lastInfeasibleReason ?? "所有从台候选分支均不可行");

        var alternatives = CreateAlternatives(
            WorldOrderResourceRequirementSetStatus.Complete,
            slaveProfile, masterRole, groups, banned, null);
        return Result(WorldOrderSlaveRuleBranchEnumerationStatus.Complete,
            alternatives, exploredBranches, rejectedBranches, null);
    }

    private static bool IsCompatiblePreparation(
        WorldOrderRuleCandidate requirement,
        WorldOrderRuleCandidate preparation) =>
        requirement.SourceLiquid == null &&
        preparation.RequirementIndex == -1 &&
        preparation.Operation == "PREPARE_POUR_OUT" &&
        preparation.SourceShape == requirement.SourceShape &&
        preparation.TargetShape == requirement.SourceShape &&
        preparation.TargetLiquid == null &&
        preparation.ProducedFreeLiquid != null;

    private static WorldOrderResourceRequirementAlternativeSet CreateAlternatives(
        WorldOrderResourceRequirementSetStatus status,
        WorldOrderRuleCandidateProfile profile,
        string masterRole,
        IEnumerable<WorldOrderResourceRequirementGroup> groups,
        IEnumerable<string> banned,
        string? reason) =>
        new(status,
            profile.RuleSetVersion,
            profile.WorkshopProfileVersion,
            profile.SpecificationVersion,
            masterRole,
            profile.Role,
            groups,
            reason,
            banned);

    private static WorldOrderSlaveRuleBranchEnumerationResult Result(
        WorldOrderSlaveRuleBranchEnumerationStatus status,
        WorldOrderResourceRequirementAlternativeSet? alternatives,
        int exploredBranches,
        int rejectedBranches,
        string? reason) =>
        new(status, alternatives, exploredBranches, rejectedBranches, reason);

    private static string[] CopyNames(IEnumerable<string>? names, string parameterName)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in names ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("液体名称不能为空", parameterName);
            result.Add(name);
        }
        return result.ToArray();
    }
}

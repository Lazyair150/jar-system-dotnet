using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.World;

namespace JarSystem.Core.World.Solving;

public enum WorldOrderAClassBranchEnumerationStatus
{
    Feasible,
    Infeasible,
    Undetermined
}

/// <summary>
/// Result of searching A-class rule candidates, independent preparation Jars, and
/// Master staging schedules. Feasible is returned only with a concrete plan and
/// a resource Manifest that validates against the supplied immutable snapshot.
/// </summary>
public sealed class WorldOrderAClassBranchEnumerationResult
{
    public WorldOrderAClassBranchEnumerationStatus Status { get; }
    public WorldOrderConcretePlanAssemblyResult? Assembly { get; }
    public int ExploredBranches { get; }
    public int ExploredNodes { get; }
    public int RejectedBranches { get; }
    public string? Reason { get; }
    public bool IsFeasible => Status == WorldOrderAClassBranchEnumerationStatus.Feasible;

    internal WorldOrderAClassBranchEnumerationResult(
        WorldOrderAClassBranchEnumerationStatus status,
        WorldOrderConcretePlanAssemblyResult? assembly,
        int exploredBranches,
        int exploredNodes,
        int rejectedBranches,
        string? reason)
    {
        Status = status;
        Assembly = assembly;
        ExploredBranches = exploredBranches;
        ExploredNodes = exploredNodes;
        RejectedBranches = rejectedBranches;
        Reason = reason;
    }
}

/// <summary>
/// Bounded A-class search over rule-candidate assignments, independent PourOut
/// preparation Jars, and every resource-affecting Master operation order.
/// Requirement index is only an identity; it is never treated as a resource schedule.
/// </summary>
public static class WorldOrderAClassBranchEnumerator
{
    public static WorldOrderAClassBranchEnumerationResult Enumerate(
        int demandId,
        WorldOrderRuleCandidateProfile masterProfile,
        WorldSnapshot world,
        int maxBranches = 256,
        int maxNodes = 100_000,
        WorldOrderSymbolicSolutionSet? precomputedSolutions = null)
    {
        if (demandId <= 0) throw new ArgumentOutOfRangeException(nameof(demandId));
        if (masterProfile == null) throw new ArgumentNullException(nameof(masterProfile));
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (maxBranches <= 0) throw new ArgumentOutOfRangeException(nameof(maxBranches));
        if (maxNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (world.Master == null)
            return Result(WorldOrderAClassBranchEnumerationStatus.Undetermined,
                null, 0, 0, 0, "缺少主台快照");
        if (masterProfile.Role != "Master" ||
            masterProfile.WorkshopName != world.Master.Name ||
            masterProfile.RuleSetVersion != world.RuleSetVersion ||
            masterProfile.WorkshopProfileVersion != world.WorkshopProfileVersion)
            return Result(WorldOrderAClassBranchEnumerationStatus.Undetermined,
                null, 0, 0, 0, "主台规则候选 Profile 与当前世界快照不一致");
        if (masterProfile.IsUndetermined)
            return Result(WorldOrderAClassBranchEnumerationStatus.Undetermined,
                null, 0, 0, 0, masterProfile.Reason ?? "主台规则候选 Profile 未能确定");

        // Candidate profiles are intentionally inventory-independent and cacheable.
        // Before enumerating concrete A-class assignments, remove candidates whose exact
        // initial Jar state is absent. The physical binder still enforces distinct IDs.
        var profile = FilterToAvailablePhysicalJarStates(masterProfile, world.Master!);
        if (profile.Requirements.Any(x => x.Candidates.Count == 0))
            return Result(WorldOrderAClassBranchEnumerationStatus.Infeasible,
                null, 0, 0, 0, "至少一项订单需求没有可绑定的未标记物理 Jar 初态");

        var reusableSolutions = ReferenceEquals(profile, masterProfile)
            ? precomputedSolutions
            : null;
        var symbolic = reusableSolutions ?? WorldOrderSymbolicSolutionBuilder.Build(
            new[] { profile }, maxBranches, maxNodes);
        if (symbolic.RuleSetVersion != profile.RuleSetVersion ||
            symbolic.WorkshopProfileVersion != profile.WorkshopProfileVersion ||
            symbolic.SpecificationVersion != profile.SpecificationVersion ||
            symbolic.Solutions.Any(solution => solution.Steps.Any(step => step.WorkshopRole != "Master")))
            return Result(WorldOrderAClassBranchEnumerationStatus.Undetermined,
                null, 0, symbolic.ExploredNodes, 0,
                "预计算符号解集与主台 Profile/订单规格不一致");
        if (symbolic.Status == WorldOrderSymbolicSolutionStatus.Undetermined)
            return Result(WorldOrderAClassBranchEnumerationStatus.Undetermined,
                null, 0, symbolic.ExploredNodes, 0,
                symbolic.Reason ?? "规则候选组合枚举达到预算上限");
        if (symbolic.Status != WorldOrderSymbolicSolutionStatus.EnumerationComplete)
            return Result(WorldOrderAClassBranchEnumerationStatus.Infeasible,
                null, 0, symbolic.ExploredNodes, 0,
                symbolic.Reason ?? "没有完整的规则候选组合");

        var exploredBranches = 0;
        var exploredNodes = symbolic.ExploredNodes;
        var rejectedBranches = 0;
        var budgetExceeded = false;
        var sawUndetermined = false;
        string? lastReason = null;
        WorldOrderConcretePlanAssemblyResult? foundAssembly = null;

        foreach (var solution in symbolic.Solutions)
        {
            if (budgetExceeded) break;
            var requiredLiquids = solution.Steps
                .Where(x => x.Candidate.ConsumedFreeLiquid != null)
                .GroupBy(x => x.Candidate.ConsumedFreeLiquid!, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var prepChoices = profile.PreparatoryOperations
                .Where(x => x.ProducedFreeLiquid != null &&
                            requiredLiquids.ContainsKey(x.ProducedFreeLiquid))
                .OrderBy(x => x.ProducedFreeLiquid, StringComparer.Ordinal)
                .ThenBy(x => x.SourceShape, StringComparer.Ordinal)
                .ThenBy(x => x.SourceLiquid, StringComparer.Ordinal)
                .ThenBy(x => x.RuleId, StringComparer.Ordinal)
                .ToArray();
            var selectedPreparations = new List<WorldOrderPreparatoryOperationAssignment>();

            void EmitSchedules()
            {
                if (budgetExceeded) return;
                var assignments = selectedPreparations.ToArray();
                WorldOrderSymbolicOperationProgram canonical;
                try
                {
                    canonical = WorldOrderPreparatoryProgramBuilder.Build(
                        solution, assignments, false, "Master", "Slave");
                }
                catch (ArgumentException exception)
                {
                    rejectedBranches++;
                    lastReason = exception.Message;
                    return;
                }

                var resourceOperations = canonical.Nodes
                    .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                                (x.Candidate!.ConsumedFreeLiquid != null ||
                                 x.Candidate.ProducedFreeLiquid != null))
                    .OrderBy(x => x.NodeId, StringComparer.Ordinal)
                    .Select(x => x.NodeId)
                    .ToArray();
                var inertOperations = canonical.Nodes
                    .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                                x.Candidate!.ConsumedFreeLiquid == null &&
                                x.Candidate.ProducedFreeLiquid == null)
                    .Select(x => x.NodeId)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToArray();

                var order = new string[resourceOperations.Length];
                var used = new bool[resourceOperations.Length];

                void VisitSchedule(int position)
                {
                    if (budgetExceeded) return;
                    if (exploredNodes >= maxNodes)
                    {
                        budgetExceeded = true;
                        return;
                    }
                    exploredNodes++;
                    if (position == resourceOperations.Length)
                    {
                        if (exploredBranches >= maxBranches)
                        {
                            budgetExceeded = true;
                            return;
                        }
                        exploredBranches++;
                        var completeOrder = order.Concat(inertOperations).ToArray();
                        WorldOrderSymbolicOperationProgram program;
                        try
                        {
                            program = WorldOrderPreparatoryProgramBuilder.Build(
                                solution, assignments, false, "Master", "Slave",
                                masterOperationOrder: completeOrder);
                        }
                        catch (ArgumentException exception)
                        {
                            rejectedBranches++;
                            lastReason = exception.Message;
                            return;
                        }

                        var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);
                        var assembly = WorldOrderConcretePlanAssembler.Assemble(
                            demandId, program, binding, world, profile);
                        if (assembly.IsFeasible)
                        {
                            foundAssembly = assembly;
                            return;
                        }

                        rejectedBranches++;
                        lastReason = assembly.Reason ?? "具体 A 类分支不可行";
                        if (assembly.Status == WorldOrderSolveStatus.Undetermined)
                            sawUndetermined = true;
                        return;
                    }

                    for (var index = 0; index < resourceOperations.Length; index++)
                    {
                        if (used[index]) continue;
                        used[index] = true;
                        order[position] = resourceOperations[index];
                        VisitSchedule(position + 1);
                        used[index] = false;
                        if (budgetExceeded || foundAssembly != null) return;
                    }
                }

                VisitSchedule(0);
            }

            void VisitPreparations(int position)
            {
                if (budgetExceeded || foundAssembly != null) return;
                if (exploredNodes >= maxNodes)
                {
                    budgetExceeded = true;
                    return;
                }
                exploredNodes++;
                if (position == prepChoices.Length)
                {
                    EmitSchedules();
                    return;
                }

                // Omit this preparation Jar.
                VisitPreparations(position + 1);
                if (budgetExceeded || foundAssembly != null) return;

                var candidate = prepChoices[position];
                var liquid = candidate.ProducedFreeLiquid!;
                var alreadySelected = selectedPreparations.Count(
                    x => x.Candidate.ProducedFreeLiquid == liquid);
                if (alreadySelected >= requiredLiquids[liquid]) return;

                selectedPreparations.Add(new WorldOrderPreparatoryOperationAssignment(
                    "Master", "preparation-resource-" + position, candidate));
                VisitPreparations(position + 1);
                selectedPreparations.RemoveAt(selectedPreparations.Count - 1);
            }

            VisitPreparations(0);
            if (foundAssembly != null) break;
        }

        if (foundAssembly != null)
            return Result(WorldOrderAClassBranchEnumerationStatus.Feasible,
                foundAssembly, exploredBranches, exploredNodes, rejectedBranches, null);
        if (budgetExceeded || sawUndetermined)
            return Result(WorldOrderAClassBranchEnumerationStatus.Undetermined,
                null, exploredBranches, exploredNodes, rejectedBranches,
                budgetExceeded
                    ? "A 类候选/准备 Jar/主台操作顺序枚举达到预算上限"
                    : lastReason ?? "至少一个候选分支无法被确定验证");
        return Result(WorldOrderAClassBranchEnumerationStatus.Infeasible,
            null, exploredBranches, exploredNodes, rejectedBranches,
            lastReason ?? "所有 A 类规则候选、准备资源和主台顺序均不可行");

    }

    private static WorldOrderRuleCandidateProfile FilterToAvailablePhysicalJarStates(
        WorldOrderRuleCandidateProfile profile,
        WorkshopSolveSnapshot master)
    {
        var available = master.Jars
            .Where(jar => !jar.IsMarked)
            .GroupBy(jar => (jar.Shape, jar.Liquid))
            .ToDictionary(group => group.Key, group => group.Count());

        bool Exists(WorldOrderRuleCandidate candidate) =>
            available.GetValueOrDefault((candidate.SourceShape, candidate.SourceLiquid)) > 0;

        var requirements = profile.Requirements
            .Select(requirement => new WorldOrderRequirementCandidates(
                requirement.RequirementIndex,
                requirement.TargetShape,
                requirement.TargetLiquid,
                requirement.Candidates.Where(Exists)))
            .ToArray();
        var preparations = profile.PreparatoryOperations.Where(Exists).ToArray();

        if (requirements.Select((item, index) =>
                item.Candidates.Count == profile.Requirements[index].Candidates.Count).All(x => x) &&
            preparations.Length == profile.PreparatoryOperations.Count)
            return profile;

        return new WorldOrderRuleCandidateProfile(
            profile.WorkshopName,
            profile.Role,
            profile.RuleSetVersion,
            profile.WorkshopProfileVersion,
            profile.SpecificationVersion,
            requirements,
            preparations,
            profile.IsUndetermined,
            profile.Reason);
    }

    private static WorldOrderAClassBranchEnumerationResult Result(
        WorldOrderAClassBranchEnumerationStatus status,
        WorldOrderConcretePlanAssemblyResult? assembly,
        int branches,
        int nodes,
        int rejected,
        string? reason) =>
        new(status, assembly, branches, nodes, rejected, reason);
}

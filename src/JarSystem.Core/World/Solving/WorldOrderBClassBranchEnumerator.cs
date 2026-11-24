using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.World;

namespace JarSystem.Core.World.Solving;

public enum WorldOrderBClassBranchEnumerationStatus
{
    Feasible,
    Infeasible,
    Undetermined
}

/// <summary>
/// Result of concrete B-class branch search. A feasible result contains a complete
/// Master→Slave plan, not merely a symbolic boundary alternative.
/// </summary>
public sealed class WorldOrderBClassBranchEnumerationResult
{
    public WorldOrderBClassBranchEnumerationStatus Status { get; }
    public WorldOrderConcretePlanAssemblyResult? Assembly { get; }
    public int ExploredBranches { get; }
    public int ExploredNodes { get; }
    public int RejectedBranches { get; }
    public string? Reason { get; }
    public bool IsFeasible => Status == WorldOrderBClassBranchEnumerationStatus.Feasible;

    internal WorldOrderBClassBranchEnumerationResult(
        WorldOrderBClassBranchEnumerationStatus status,
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
/// B-class enumerator. Requirement operations may be assigned to either role using each
/// role's exact rule profile. Master operations execute before the one-way handoff; Slave
/// operations execute after it. Jar transfer is ownership movement, not a workshop operation,
/// and therefore never tests either workshop's polarity. A receiving workshop checks polarity
/// only when it later attempts an operation on the transferred Jar.
/// </summary>
public static class WorldOrderBClassBranchEnumerator
{
    public static WorldOrderBClassBranchEnumerationResult Enumerate(
        int demandId,
        WorldOrderRuleCandidateProfile masterProfile,
        WorldOrderRuleCandidateProfile slaveProfile,
        WorldSnapshot world,
        int maxBranches = 256,
        int maxNodes = 100_000,
        WorldOrderSymbolicSolutionSet? precomputedSolutions = null,
        IEnumerable<string>? slaveRejectedLiquids = null)
    {
        if (demandId <= 0) throw new ArgumentOutOfRangeException(nameof(demandId));
        if (masterProfile == null) throw new ArgumentNullException(nameof(masterProfile));
        if (slaveProfile == null) throw new ArgumentNullException(nameof(slaveProfile));
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (maxBranches <= 0) throw new ArgumentOutOfRangeException(nameof(maxBranches));
        if (maxNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (world.Master == null || world.Slave == null)
            return Result(WorldOrderBClassBranchEnumerationStatus.Undetermined,
                null, 0, 0, 0, "B 类订单缺少主台或从台快照");

        if (masterProfile.Role != "Master" || slaveProfile.Role != "Slave" ||
            masterProfile.WorkshopName != world.Master.Name ||
            slaveProfile.WorkshopName != world.Slave.Name ||
            masterProfile.RuleSetVersion != world.RuleSetVersion ||
            slaveProfile.RuleSetVersion != world.RuleSetVersion ||
            masterProfile.WorkshopProfileVersion != world.WorkshopProfileVersion ||
            slaveProfile.WorkshopProfileVersion != world.WorkshopProfileVersion ||
            masterProfile.SpecificationVersion != slaveProfile.SpecificationVersion)
            return Result(WorldOrderBClassBranchEnumerationStatus.Undetermined,
                null, 0, 0, 0, "主从规则候选 Profile 与当前世界/订单规格不一致");

        // Both role profiles participate in requirement assignment. Unknown rules in
        // either role make the combined search Undetermined; silently treating that
        // role as unavailable would incorrectly remove legal Master/Slave schedules.
        if (masterProfile.IsUndetermined || slaveProfile.IsUndetermined)
            return Result(WorldOrderBClassBranchEnumerationStatus.Undetermined,
                null, 0, 0, 0,
                masterProfile.Reason ?? slaveProfile.Reason ?? "主从规则候选 Profile 未能确定");
        if (world.Slave.Jars.Count != 0 || world.Slave.Liquids.Values.Any(amount => amount != 0))
            return Result(WorldOrderBClassBranchEnumerationStatus.Infeasible,
                null, 0, 0, 0, "B 类从台初始库存必须为空；从台不能用自有 Jar 或游离液体补订单");

        // All physical Jars begin on Master. Slave candidate profiles describe Slave
        // rules, not a second source inventory. Filter both profiles against Master IDs.
        var master = FilterToAvailablePhysicalJarStates(masterProfile, world.Master);
        var slave = FilterToAvailablePhysicalJarStates(slaveProfile, world.Master);
        if (master.Requirements.Count != masterProfile.Requirements.Count ||
            slave.Requirements.Count != slaveProfile.Requirements.Count ||
            master.Requirements.Select((item, index) =>
                item.RequirementIndex != masterProfile.Requirements[index].RequirementIndex).Any(x => x) ||
            slave.Requirements.Select((item, index) =>
                item.RequirementIndex != slaveProfile.Requirements[index].RequirementIndex).Any(x => x))
            return Result(WorldOrderBClassBranchEnumerationStatus.Undetermined,
                null, 0, 0, 0, "主从规则候选 Profile 的需求索引无法与当前订单规格对齐");

        // Every order requirement may be fulfilled by either role. The symbolic builder
        // chooses one role-specific candidate per requirement; physical Jar identity is
        // bound only after this role assignment has been selected.
        // A legacy precomputed set may have been built from only the Slave profile.
        // The current role-assignment Cartesian product is different, and the set does
        // not carry a profile-membership fingerprint, so reusing it could silently omit
        // Master-owned requirement branches. Rebuild from both exact profiles until the
        // cache key explicitly binds the complete role/profile set.
        var symbolic = WorldOrderSymbolicSolutionBuilder.Build(
            new[] { master, slave }, maxBranches, maxNodes);

        if (symbolic.RuleSetVersion != slave.RuleSetVersion ||
            symbolic.WorkshopProfileVersion != slave.WorkshopProfileVersion ||
            symbolic.SpecificationVersion != slave.SpecificationVersion ||
            symbolic.Solutions.Any(solution => solution.Steps.Any(step =>
                step.WorkshopRole != "Master" && step.WorkshopRole != "Slave")))
            return Result(WorldOrderBClassBranchEnumerationStatus.Undetermined,
                null, 0, symbolic.ExploredNodes, 0,
                "预计算符号解集与主从 Profile/订单规格不一致");

        if (symbolic.Status == WorldOrderSymbolicSolutionStatus.Undetermined)
            return Result(WorldOrderBClassBranchEnumerationStatus.Undetermined,
                null, 0, symbolic.ExploredNodes, 0,
                symbolic.Reason ?? "B 类规则候选组合枚举达到预算上限");
        if (symbolic.Status != WorldOrderSymbolicSolutionStatus.EnumerationComplete)
            return Result(WorldOrderBClassBranchEnumerationStatus.Infeasible,
                null, 0, symbolic.ExploredNodes, 0,
                symbolic.Reason ?? "从台规则环境没有完整的订单需求候选组合");

        var effectiveSlaveRejects = new SortedSet<string>(
            world.Slave.Capability.Ban, StringComparer.Ordinal);
        foreach (var liquid in slaveRejectedLiquids ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(liquid))
                throw new ArgumentException("从台拒收液体名称不能为空", nameof(slaveRejectedLiquids));
            effectiveSlaveRejects.Add(liquid);
        }

        var exploredBranches = 0;
        var exploredNodes = symbolic.ExploredNodes;
        var rejectedBranches = 0;
        var budgetExceeded = false;
        var sawUndetermined = false;
        string? lastReason = null;
        WorldOrderConcretePlanAssemblyResult? foundAssembly = null;

        // The symbolic set already assigns every requirement to exactly one of the
        // two roles. Do not collapse it back to a Slave-only plan here.
        var orderedSolutions = symbolic.Solutions
            .Where(solution =>
                solution.Steps.All(x =>
                    x.WorkshopRole == "Master" || x.WorkshopRole == "Slave") &&
                // A B-class order must still cross the Slave-use boundary. Master may
                // complete some requirements, but at least one remaining requirement
                // must be concretely executed by Slave; a Jar-only handoff is not a
                // substitute for the B-class Slave participation event.
                solution.Steps.Any(x => x.WorkshopRole == "Slave"))
            .ToArray();
        if (orderedSolutions.Length == 0)
            return Result(WorldOrderBClassBranchEnumerationStatus.Infeasible,
                null, exploredNodes, exploredNodes, 0,
                "主从规则环境没有可执行的完整订单需求分支");
        foreach (var solution in orderedSolutions)
        {
            if (budgetExceeded || foundAssembly != null) break;
            var masterNeeds = solution.Steps
                .Where(x => x.WorkshopRole == "Master" && x.Candidate.ConsumedFreeLiquid != null)
                .GroupBy(x => x.Candidate.ConsumedFreeLiquid!, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var slaveNeeds = solution.Steps
                .Where(x => x.WorkshopRole == "Slave" && x.Candidate.ConsumedFreeLiquid != null)
                .GroupBy(x => x.Candidate.ConsumedFreeLiquid!, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            var allMasterPrepNeeds = new HashSet<string>(
                masterNeeds.Keys.Concat(slaveNeeds.Keys), StringComparer.Ordinal);
            var masterPrepChoices = master.PreparatoryOperations
                .Where(x => x.ProducedFreeLiquid != null &&
                            allMasterPrepNeeds.Contains(x.ProducedFreeLiquid))
                .OrderBy(x => x.ProducedFreeLiquid, StringComparer.Ordinal)
                .ThenBy(x => x.SourceShape, StringComparer.Ordinal)
                .ThenBy(x => x.SourceLiquid, StringComparer.Ordinal)
                .ThenBy(x => x.RuleId, StringComparer.Ordinal)
                .ToArray();
            var slavePrepChoices = slave.PreparatoryOperations
                .Where(x => x.ProducedFreeLiquid != null &&
                            slaveNeeds.ContainsKey(x.ProducedFreeLiquid))
                .OrderBy(x => x.ProducedFreeLiquid, StringComparer.Ordinal)
                .ThenBy(x => x.SourceShape, StringComparer.Ordinal)
                .ThenBy(x => x.SourceLiquid, StringComparer.Ordinal)
                .ThenBy(x => x.RuleId, StringComparer.Ordinal)
                .ToArray();
            var prepChoices = masterPrepChoices
                .Select((candidate, index) => (Role: "Master", Candidate: candidate, Index: index))
                .Concat(slavePrepChoices.Select((candidate, index) =>
                    (Role: "Slave", Candidate: candidate, Index: index)))
                .ToArray();
            var selectedPreparations = new List<WorldOrderPreparatoryOperationAssignment>();

            void EmitSchedules()
            {
                if (budgetExceeded || foundAssembly != null) return;
                var assignments = selectedPreparations.ToArray();
                WorldOrderSymbolicOperationProgram canonical;
                try
                {
                    canonical = WorldOrderPreparatoryProgramBuilder.Build(
                        solution, assignments, true, "Master", "Slave");
                }
                catch (ArgumentException exception)
                {
                    rejectedBranches++;
                    lastReason = exception.Message;
                    return;
                }

                var masterResourceNodes = canonical.Nodes
                    .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                                x.WorkshopRole == "Master" &&
                                (x.Candidate!.ConsumedFreeLiquid != null ||
                                 x.Candidate.ProducedFreeLiquid != null))
                    .Select(x => x.NodeId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var masterInertNodes = canonical.Nodes
                    .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                                x.WorkshopRole == "Master" &&
                                x.Candidate!.ConsumedFreeLiquid == null &&
                                x.Candidate.ProducedFreeLiquid == null)
                    .Select(x => x.NodeId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var slaveResourceNodes = canonical.Nodes
                    .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                                x.WorkshopRole == "Slave" &&
                                (x.Candidate!.ConsumedFreeLiquid != null ||
                                 x.Candidate.ProducedFreeLiquid != null))
                    .Select(x => x.NodeId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var slaveInertNodes = canonical.Nodes
                    .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                                x.WorkshopRole == "Slave" &&
                                x.Candidate!.ConsumedFreeLiquid == null &&
                                x.Candidate.ProducedFreeLiquid == null)
                    .Select(x => x.NodeId).OrderBy(x => x, StringComparer.Ordinal).ToArray();

                var masterOrder = new string[masterResourceNodes.Length];
                var slaveOrder = new string[slaveResourceNodes.Length];
                var masterUsed = new bool[masterResourceNodes.Length];
                var slaveUsed = new bool[slaveResourceNodes.Length];

                void VisitSlaveOrder(int position)
                {
                    if (budgetExceeded || foundAssembly != null) return;
                    if (!SpendNode()) return;
                    if (position == slaveResourceNodes.Length)
                    {
                        TrySchedulePair();
                        return;
                    }
                    for (var i = 0; i < slaveResourceNodes.Length; i++)
                    {
                        if (slaveUsed[i]) continue;
                        slaveUsed[i] = true;
                        slaveOrder[position] = slaveResourceNodes[i];
                        VisitSlaveOrder(position + 1);
                        slaveUsed[i] = false;
                        if (budgetExceeded || foundAssembly != null) return;
                    }
                }

                void VisitMasterOrder(int position)
                {
                    if (budgetExceeded || foundAssembly != null) return;
                    if (!SpendNode()) return;
                    if (position == masterResourceNodes.Length)
                    {
                        VisitSlaveOrder(0);
                        return;
                    }
                    for (var i = 0; i < masterResourceNodes.Length; i++)
                    {
                        if (masterUsed[i]) continue;
                        masterUsed[i] = true;
                        masterOrder[position] = masterResourceNodes[i];
                        VisitMasterOrder(position + 1);
                        masterUsed[i] = false;
                        if (budgetExceeded || foundAssembly != null) return;
                    }
                }

                void TrySchedulePair()
                {
                    if (exploredBranches >= maxBranches)
                    {
                        budgetExceeded = true;
                        return;
                    }
                    exploredBranches++;
                    WorldOrderSymbolicOperationProgram program;
                    try
                    {
                        program = WorldOrderPreparatoryProgramBuilder.Build(
                            solution, assignments, true, "Master", "Slave",
                            slaveOperationOrder: slaveOrder.Concat(slaveInertNodes).ToArray(),
                            masterOperationOrder: masterOrder.Concat(masterInertNodes).ToArray());
                    }
                    catch (ArgumentException exception)
                    {
                        rejectedBranches++;
                        lastReason = exception.Message;
                        return;
                    }

                    var binding = WorldOrderPhysicalJarBinder.Bind(program, world.Master!);
                    if (binding.Status == WorldOrderPhysicalJarBindingStatus.Undetermined)
                    {
                        sawUndetermined = true;
                        lastReason = binding.Reason;
                        rejectedBranches++;
                        return;
                    }
                    if (!binding.IsComplete)
                    {
                        rejectedBranches++;
                        lastReason = binding.Reason;
                        return;
                    }

                    var assembly = WorldOrderConcretePlanAssembler.Assemble(
                        demandId, program, binding, world, master, slave,
                        effectiveSlaveRejects);
                    if (assembly.IsFeasible)
                    {
                        foundAssembly = assembly;
                        return;
                    }

                    rejectedBranches++;
                    lastReason = assembly.Reason ?? "具体 B 类分支不可行";
                    if (assembly.Status == WorldOrderSolveStatus.Undetermined)
                        sawUndetermined = true;
                }

                VisitMasterOrder(0);
            }

            void VisitPreparations(int position)
            {
                if (budgetExceeded || foundAssembly != null) return;
                if (!SpendNode()) return;
                if (position == prepChoices.Length)
                {
                    EmitSchedules();
                    return;
                }

                // Omit this preparation Jar.
                VisitPreparations(position + 1);
                if (budgetExceeded || foundAssembly != null) return;

                var item = prepChoices[position];
                var liquid = item.Candidate.ProducedFreeLiquid!;
                var demandCount = item.Role == "Master"
                    ? masterNeeds.GetValueOrDefault(liquid) + slaveNeeds.GetValueOrDefault(liquid)
                    : slaveNeeds.GetValueOrDefault(liquid);
                var selectedCount = selectedPreparations.Count(x =>
                    x.WorkshopRole == item.Role &&
                    x.Candidate.ProducedFreeLiquid == liquid);
                if (selectedCount >= demandCount) return;

                selectedPreparations.Add(new WorldOrderPreparatoryOperationAssignment(
                    item.Role,
                    (item.Role == "Master" ? "master" : "slave") +
                    "-preparation-resource-" + item.Index,
                    item.Candidate));
                VisitPreparations(position + 1);
                selectedPreparations.RemoveAt(selectedPreparations.Count - 1);
            }

            VisitPreparations(0);
        }

        if (foundAssembly != null)
            return Result(WorldOrderBClassBranchEnumerationStatus.Feasible,
                foundAssembly, exploredBranches, exploredNodes, rejectedBranches, null);
        if (budgetExceeded || sawUndetermined)
            return Result(WorldOrderBClassBranchEnumerationStatus.Undetermined,
                null, exploredBranches, exploredNodes, rejectedBranches,
                budgetExceeded
                    ? "B 类候选/准备 Jar/主从操作顺序枚举达到预算上限"
                    : lastReason ?? "至少一个 B 类候选分支无法被确定验证");
        return Result(WorldOrderBClassBranchEnumerationStatus.Infeasible,
            null, exploredBranches, exploredNodes, rejectedBranches,
            lastReason ?? "所有 B 类主从候选、准备资源和操作顺序均不可行");

        bool SpendNode()
        {
            if (exploredNodes >= maxNodes)
            {
                budgetExceeded = true;
                return false;
            }
            exploredNodes++;
            return true;
        }
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
        // A profile stores one rule candidate per Jar state, not one candidate per
        // physical Jar. Expand each preparation candidate to the number of distinct
        // unmarked physical Jars with that exact source state. Otherwise two identical
        // full Jars could never both be selected as independent preparation resources.
        var preparations = profile.PreparatoryOperations
            .SelectMany(candidate => Enumerable.Repeat(
                candidate,
                available.GetValueOrDefault((candidate.SourceShape, candidate.SourceLiquid))))
            .ToArray();

        if (requirements.Select((item, index) =>
                item.Candidates.Count == profile.Requirements[index].Candidates.Count).All(x => x) &&
            preparations.Length == profile.PreparatoryOperations.Count &&
            preparations.SequenceEqual(profile.PreparatoryOperations))
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

    private static WorldOrderBClassBranchEnumerationResult Result(
        WorldOrderBClassBranchEnumerationStatus status,
        WorldOrderConcretePlanAssemblyResult? assembly,
        int branches,
        int nodes,
        int rejected,
        string? reason) =>
        new(status, assembly, branches, nodes, rejected, reason);
}

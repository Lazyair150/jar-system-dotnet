using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// Assigns one catalogued preparation operation to one independent abstract Jar.
/// A preparation Jar must never also be used as a requirement Jar.
/// </summary>
public sealed record WorldOrderPreparatoryOperationAssignment(
    string WorkshopRole,
    string JarKey,
    WorldOrderRuleCandidate Candidate);

/// <summary>
/// Builds a symbolic order program in which every Jar identity participates in at
/// most one rule operation. Jar revisions describe state versions; they never grant
/// a second operation. Master-to-Slave handoff preserves the Jar's mark.
/// </summary>
public static class WorldOrderPreparatoryProgramBuilder
{
    public static WorldOrderSymbolicOperationProgram Build(
        WorldOrderSymbolicSolution solution,
        IReadOnlyCollection<WorldOrderPreparatoryOperationAssignment> preparations,
        bool isBClass,
        string masterRole,
        string slaveRole,
        IReadOnlyList<string>? slaveOperationOrder = null)
    {
        if (solution == null) throw new ArgumentNullException(nameof(solution));
        if (preparations == null) throw new ArgumentNullException(nameof(preparations));
        if (string.IsNullOrWhiteSpace(masterRole)) throw new ArgumentException("主台角色不能为空", nameof(masterRole));
        if (string.IsNullOrWhiteSpace(slaveRole)) throw new ArgumentException("从台角色不能为空", nameof(slaveRole));
        if (masterRole == slaveRole) throw new ArgumentException("主台和从台角色必须不同");

        var requirements = solution.Steps.OrderBy(x => x.Candidate.RequirementIndex).ToArray();
        if (requirements.Any(x => x.Candidate.RequirementIndex < 0))
            throw new ArgumentException("需求解不能包含准备操作", nameof(solution));
        if (requirements.Select(x => x.Candidate.RequirementIndex).Distinct().Count() != requirements.Length)
            throw new ArgumentException("每条需求只能分配一个候选", nameof(solution));
        if (requirements.Any(x => x.WorkshopRole != masterRole && x.WorkshopRole != slaveRole))
            throw new ArgumentException("需求候选包含未知岗位", nameof(solution));
        if (!isBClass && requirements.Any(x => x.WorkshopRole != masterRole))
            throw new ArgumentException("A 类订单只能在主台执行", nameof(solution));

        var prep = preparations.ToArray();
        if (prep.Any(x => x == null || x.Candidate == null))
            throw new ArgumentException("准备操作分配不能包含 null", nameof(preparations));
        if (prep.Any(x => x.WorkshopRole != masterRole && x.WorkshopRole != slaveRole))
            throw new ArgumentException("准备操作包含未知岗位", nameof(preparations));
        if (!isBClass && prep.Any(x => x.WorkshopRole != masterRole))
            throw new ArgumentException("A 类订单不能在从台执行准备操作", nameof(preparations));
        if (prep.Any(x => x.Candidate.RequirementIndex != -1 ||
                          x.Candidate.Operation != "PREPARE_POUR_OUT"))
            throw new ArgumentException(
                "当前准备操作目录只接受目录生成的 PREPARE_POUR_OUT 候选", nameof(preparations));
        if (prep.Any(x => string.IsNullOrWhiteSpace(x.JarKey)))
            throw new ArgumentException("准备操作必须指定独立 Jar 身份", nameof(preparations));
        if (prep.Select(x => x.JarKey).Distinct(StringComparer.Ordinal).Count() != prep.Length)
            throw new ArgumentException("同一 Jar 在一次订单实现中只能分配一次操作", nameof(preparations));

        var requirementJarKeys = requirements.ToDictionary(
            x => x.Candidate.RequirementIndex,
            x => "requirement-" + x.Candidate.RequirementIndex);
        var requirementKeys = new HashSet<string>(requirementJarKeys.Values, StringComparer.Ordinal);
        if (prep.Any(x => requirementKeys.Contains(x.JarKey)))
            throw new ArgumentException(
                "准备操作 Jar 与最终需求 Jar 必须是不同身份；每只 Jar 在一次订单实现中只能操作一次",
                nameof(preparations));

        var allInitial = new Dictionary<string, WorldOrderSymbolicJarVersion>(StringComparer.Ordinal);
        foreach (var assigned in requirements)
        {
            var candidate = assigned.Candidate;
            var key = requirementJarKeys[candidate.RequirementIndex];
            allInitial.Add(key, new WorldOrderSymbolicJarVersion(
                key, 0, masterRole, candidate.SourceShape, candidate.SourceLiquid));
        }
        foreach (var selectedPrep in prep)
        {
            var candidate = selectedPrep.Candidate;
            allInitial.Add(selectedPrep.JarKey, new WorldOrderSymbolicJarVersion(
                selectedPrep.JarKey, 0, masterRole, candidate.SourceShape, candidate.SourceLiquid));
        }

        var nodes = new List<WorldOrderSymbolicOperationNode>();
        var edges = new List<WorldOrderSymbolicOperationDependency>();
        var current = new Dictionary<string, WorldOrderSymbolicJarVersion>(allInitial, StringComparer.Ordinal);
        var sequence = 0;

        // Master operations, including independent preparation Jars, finish before handoff.
        foreach (var selectedPrep in prep.Where(x => x.WorkshopRole == masterRole)
                     .OrderBy(x => x.JarKey, StringComparer.Ordinal))
        {
            var input = current[selectedPrep.JarKey];
            EnsureCandidateInput(selectedPrep.Candidate, input);
            var output = NextVersion(input, masterRole, selectedPrep.Candidate);
            nodes.Add(WorldOrderSymbolicOperationNode.Rule(
                "prepare-" + selectedPrep.JarKey,
                sequence++, masterRole, null, selectedPrep.Candidate, input, output));
            current[selectedPrep.JarKey] = output;
        }

        foreach (var assigned in requirements.Where(x => x.WorkshopRole == masterRole))
        {
            var candidate = assigned.Candidate;
            var key = requirementJarKeys[candidate.RequirementIndex];
            var input = current[key];
            EnsureCandidateInput(candidate, input);
            var output = NextVersion(input, masterRole, candidate);
            nodes.Add(WorldOrderSymbolicOperationNode.Rule(
                "requirement-" + candidate.RequirementIndex + "-master",
                sequence++, masterRole, candidate.RequirementIndex, candidate, input, output));
            current[key] = output;
        }

        WorldOrderSymbolicOperationNode? handoff = null;
        if (isBClass)
        {
            var slaveJarKeys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var assigned in requirements.Where(x => x.WorkshopRole == slaveRole))
                slaveJarKeys.Add(requirementJarKeys[assigned.Candidate.RequirementIndex]);
            var slaveOperations = new List<(
            string NodeId,
            string JarKey,
            int? RequirementIndex,
            WorldOrderRuleCandidate Candidate)>();
        foreach (var selectedPrep in prep.Where(x => x.WorkshopRole == slaveRole))
            slaveOperations.Add((
                "prepare-" + selectedPrep.JarKey,
                selectedPrep.JarKey,
                null,
                selectedPrep.Candidate));
        foreach (var assigned in requirements.Where(x => x.WorkshopRole == slaveRole))
        {
            var requirementIndex = assigned.Candidate.RequirementIndex;
            slaveOperations.Add((
                "requirement-" + requirementIndex + "-slave",
                requirementJarKeys[requirementIndex],
                requirementIndex,
                assigned.Candidate));
        }

        if (slaveOperationOrder != null)
        {
            if (slaveOperationOrder.Count != slaveOperations.Count ||
                slaveOperationOrder.Distinct(StringComparer.Ordinal).Count() != slaveOperations.Count)
                throw new ArgumentException("从台操作顺序必须恰好包含每个操作一次", nameof(slaveOperationOrder));
            var byNodeId = slaveOperations.ToDictionary(x => x.NodeId, StringComparer.Ordinal);
            if (slaveOperationOrder.Any(id => !byNodeId.ContainsKey(id)))
                throw new ArgumentException("从台操作顺序包含未知操作", nameof(slaveOperationOrder));
            slaveOperations = slaveOperationOrder.Select(id => byNodeId[id]).ToList();
        }
        else
        {
            // Stable compatibility order for callers that do not request schedule enumeration.
            slaveOperations = slaveOperations
                .OrderBy(x => x.RequirementIndex.HasValue ? 1 : 0)
                .ThenBy(x => x.RequirementIndex ?? -1)
                .ThenBy(x => x.JarKey, StringComparer.Ordinal)
                .ToList();
        }

        foreach (var operation in slaveOperations)
        {
            var input = current[operation.JarKey];
            EnsureCandidateInput(operation.Candidate, input);
            var output = NextVersion(input, slaveRole, operation.Candidate);
            var node = WorldOrderSymbolicOperationNode.Rule(
                operation.NodeId,
                sequence++, slaveRole, operation.RequirementIndex,
                operation.Candidate, input, output);
            nodes.Add(node);
            edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                handoff!.NodeId, node.NodeId, operation.JarKey, input.Revision));
            current[operation.JarKey] = output;
        }
        }

        AddLiquidDependencies(nodes, edges, handoff, masterRole, slaveRole);
        return new WorldOrderSymbolicOperationProgram(isBClass, masterRole, slaveRole, nodes, edges);
    }

    private static void EnsureCandidateInput(
        WorldOrderRuleCandidate candidate,
        WorldOrderSymbolicJarVersion input)
    {
        if (input.IsMarked)
            throw new ArgumentException("已标记 Jar 不能再次执行操作：" + input.JarKey);
        if (candidate.SourceShape != input.Shape || candidate.SourceLiquid != input.Liquid)
            throw new ArgumentException("操作候选与当前 Jar 状态不匹配：" + input.JarKey);
    }

    private static WorldOrderSymbolicJarVersion NextVersion(
        WorldOrderSymbolicJarVersion input,
        string role,
        WorldOrderRuleCandidate candidate) =>
        new(input.JarKey, checked(input.Revision + 1), role,
            candidate.TargetShape, candidate.TargetLiquid, isMarked: true);

    private static void AddLiquidDependencies(
        IReadOnlyList<WorldOrderSymbolicOperationNode> nodes,
        ICollection<WorldOrderSymbolicOperationDependency> edges,
        WorldOrderSymbolicOperationNode? handoff,
        string masterRole,
        string slaveRole)
    {
        var producers = nodes.Where(x =>
                x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                x.Candidate?.ProducedFreeLiquid != null)
            .ToArray();

        foreach (var consumer in nodes.Where(x =>
                     x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                     x.Candidate?.ConsumedFreeLiquid != null))
        {
            var liquid = consumer.Candidate!.ConsumedFreeLiquid!;
            var role = consumer.WorkshopRole!;
            var earlier = producers.Where(x =>
                    x.WorkshopRole == role &&
                    x.Sequence < consumer.Sequence &&
                    x.Candidate!.ProducedFreeLiquid == liquid)
                .ToArray();

            foreach (var producer in earlier)
                edges.Add(WorldOrderSymbolicOperationDependency.FreeLiquid(
                    producer.NodeId, consumer.NodeId, liquid));

            if (role == slaveRole && handoff != null)
            {
                // Handoff is the possible source for any deficit left after local
                // production. The projector determines whether a positive amount is
                // actually required; this edge records the legal source boundary.
                edges.Add(WorldOrderSymbolicOperationDependency.FreeLiquid(
                    handoff.NodeId, consumer.NodeId, liquid));
            }
        }

        if (handoff == null) return;
        var slaveNeeds = nodes.Where(x =>
                x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                x.WorkshopRole == slaveRole &&
                x.Candidate?.ConsumedFreeLiquid != null)
            .Select(x => x.Candidate!.ConsumedFreeLiquid!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var producer in producers.Where(x =>
                     x.WorkshopRole == masterRole &&
                     slaveNeeds.Contains(x.Candidate!.ProducedFreeLiquid!)))
            edges.Add(WorldOrderSymbolicOperationDependency.FreeLiquid(
                producer.NodeId, handoff.NodeId, producer.Candidate!.ProducedFreeLiquid!));
    }
}

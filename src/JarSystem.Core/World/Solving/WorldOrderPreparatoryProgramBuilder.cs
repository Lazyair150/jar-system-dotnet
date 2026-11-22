using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// Assigns one catalogued preparatory operation to an abstract Jar identity.
/// InitialJar is always located at Master before the B-class one-time handoff.
/// WorkshopRole selects where the preparation itself executes.
/// </summary>
public sealed record WorldOrderPreparatoryOperationAssignment(
    string WorkshopRole,
    string JarKey,
    WorldOrderRuleCandidate Candidate);

/// <summary>
/// Expands a direct requirement solution with explicitly selected PourOut preparation
/// operations. This binds preparation candidates to abstract Jar identities and places
/// them on the correct side of the single B-class handoff.
/// It does not enumerate preparation combinations or bind physical inventory.
/// </summary>
public static class WorldOrderPreparatoryProgramBuilder
{
    public static WorldOrderSymbolicOperationProgram Build(
        WorldOrderSymbolicSolution solution,
        IReadOnlyCollection<WorldOrderPreparatoryOperationAssignment> preparations,
        bool isBClass,
        string masterRole,
        string slaveRole)
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
                          x.Candidate.Operation != "PREPARE_POUR_OUT" ||
                          x.Candidate.SourceShape != x.Candidate.TargetShape ||
                          x.Candidate.TargetLiquid != null ||
                          x.Candidate.ProducedFreeLiquid == null))
            throw new ArgumentException("当前准备操作切片只接受目录生成的 PREPARE_POUR_OUT 候选", nameof(preparations));
        if (prep.Select(x => x.JarKey).Distinct(StringComparer.Ordinal).Count() != prep.Length)
            throw new ArgumentException("一个 Jar 身份在本切片只能分配一次准备操作", nameof(preparations));

        var requirementByIndex = requirements.ToDictionary(x => x.Candidate.RequirementIndex);
        var prepByJar = prep.ToDictionary(x => x.JarKey, StringComparer.Ordinal);
        var requirementJarKeys = requirements.ToDictionary(
            x => x.Candidate.RequirementIndex,
            x => "requirement-" + x.Candidate.RequirementIndex);
        var allInitial = new Dictionary<string, WorldOrderSymbolicJarVersion>(StringComparer.Ordinal);

        foreach (var assigned in requirements)
        {
            var key = requirementJarKeys[assigned.Candidate.RequirementIndex];
            var candidate = assigned.Candidate;
            var hasPreparation = prepByJar.TryGetValue(key, out var selectedPrep);
            var initial = hasPreparation
                ? new WorldOrderSymbolicJarVersion(
                    key, 0, masterRole,
                    selectedPrep!.Candidate.SourceShape,
                    selectedPrep.Candidate.SourceLiquid)
                : new WorldOrderSymbolicJarVersion(
                    key, 0, masterRole, candidate.SourceShape, candidate.SourceLiquid);
            if (hasPreparation)
            {
                var preparedShape = selectedPrep!.Candidate.TargetShape;
                var preparedLiquid = selectedPrep.Candidate.TargetLiquid;
                if (candidate.SourceShape != preparedShape || candidate.SourceLiquid != preparedLiquid)
                    throw new ArgumentException("准备操作输出 Jar 状态与需求候选输入不匹配：" + key);
            }
            allInitial.Add(key, initial);
        }

        foreach (var selectedPrep in prep)
        {
            if (allInitial.ContainsKey(selectedPrep.JarKey)) continue;
            var candidate = selectedPrep.Candidate;
            allInitial.Add(selectedPrep.JarKey, new WorldOrderSymbolicJarVersion(
                selectedPrep.JarKey, 0, masterRole, candidate.SourceShape, candidate.SourceLiquid));
        }

        var nodes = new List<WorldOrderSymbolicOperationNode>();
        var edges = new List<WorldOrderSymbolicOperationDependency>();
        var current = new Dictionary<string, WorldOrderSymbolicJarVersion>(allInitial, StringComparer.Ordinal);
        var prepNodeByJar = new Dictionary<string, WorldOrderSymbolicOperationNode>(StringComparer.Ordinal);
        var requirementNodeByIndex = new Dictionary<int, WorldOrderSymbolicOperationNode>();
        var sequence = 0;

        // Master-side preparations happen before every Master requirement operation
        // and before the one batch handoff.
        foreach (var selectedPrep in prep.Where(x => x.WorkshopRole == masterRole)
                     .OrderBy(x => x.JarKey, StringComparer.Ordinal))
        {
            var input = current[selectedPrep.JarKey];
            var output = NextVersion(input, masterRole, selectedPrep.Candidate);
            var node = WorldOrderSymbolicOperationNode.Rule(
                "prepare-" + selectedPrep.JarKey, sequence++, masterRole, null,
                selectedPrep.Candidate, input, output);
            nodes.Add(node);
            prepNodeByJar.Add(selectedPrep.JarKey, node);
            current[selectedPrep.JarKey] = output;
        }

        foreach (var assigned in requirements.Where(x => x.WorkshopRole == masterRole))
        {
            var candidate = assigned.Candidate;
            var key = requirementJarKeys[candidate.RequirementIndex];
            var input = current[key];
            EnsureCandidateInput(candidate, input);
            var output = new WorldOrderSymbolicJarVersion(
                key, checked(input.Revision + 1), masterRole,
                candidate.TargetShape, candidate.TargetLiquid);
            var node = WorldOrderSymbolicOperationNode.Rule(
                "requirement-" + candidate.RequirementIndex + "-master",
                sequence++, masterRole, candidate.RequirementIndex, candidate, input, output);
            nodes.Add(node);
            requirementNodeByIndex.Add(candidate.RequirementIndex, node);
            if (prepNodeByJar.TryGetValue(key, out var producer))
                edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                    producer.NodeId, node.NodeId, key, input.Revision));
            current[key] = output;
        }

        WorldOrderSymbolicOperationNode? handoff = null;
        var slaveJarKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var assigned in requirements.Where(x => x.WorkshopRole == slaveRole))
            slaveJarKeys.Add(requirementJarKeys[assigned.Candidate.RequirementIndex]);
        foreach (var selectedPrep in prep.Where(x => x.WorkshopRole == slaveRole))
            slaveJarKeys.Add(selectedPrep.JarKey);

        if (isBClass)
        {
            var handoffInputs = slaveJarKeys.Select(key => current[key]).ToArray();
            handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
                "master-to-slave-handoff", sequence++, masterRole, slaveRole, handoffInputs);
            nodes.Add(handoff);
            foreach (var input in handoff.InputJars)
            {
                var output = handoff.OutputJars.Single(x => x.JarKey == input.JarKey);
                var prepProducer = prepNodeByJar.GetValueOrDefault(input.JarKey);
                if (prepProducer != null)
                    edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                        prepProducer.NodeId, handoff.NodeId, input.JarKey, input.Revision));
                current[input.JarKey] = output;
            }
        }

        foreach (var selectedPrep in prep.Where(x => x.WorkshopRole == slaveRole)
                     .OrderBy(x => x.JarKey, StringComparer.Ordinal))
        {
            var input = current[selectedPrep.JarKey];
            EnsureCandidateInput(selectedPrep.Candidate, input);
            var output = NextVersion(input, slaveRole, selectedPrep.Candidate);
            var node = WorldOrderSymbolicOperationNode.Rule(
                "prepare-" + selectedPrep.JarKey, sequence++, slaveRole, null,
                selectedPrep.Candidate, input, output);
            nodes.Add(node);
            prepNodeByJar[selectedPrep.JarKey] = node;
            edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                handoff!.NodeId, node.NodeId, selectedPrep.JarKey, input.Revision));
            current[selectedPrep.JarKey] = output;
        }

        foreach (var assigned in requirements.Where(x => x.WorkshopRole == slaveRole))
        {
            var candidate = assigned.Candidate;
            var key = requirementJarKeys[candidate.RequirementIndex];
            var input = current[key];
            EnsureCandidateInput(candidate, input);
            var output = new WorldOrderSymbolicJarVersion(
                key, checked(input.Revision + 1), slaveRole,
                candidate.TargetShape, candidate.TargetLiquid);
            var node = WorldOrderSymbolicOperationNode.Rule(
                "requirement-" + candidate.RequirementIndex + "-slave",
                sequence++, slaveRole, candidate.RequirementIndex, candidate, input, output);
            nodes.Add(node);
            requirementNodeByIndex.Add(candidate.RequirementIndex, node);
            var producer = prepNodeByJar.GetValueOrDefault(key);
            edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                producer?.NodeId ?? handoff!.NodeId, node.NodeId, key, input.Revision));
            current[key] = output;
        }

        // Add liquid provenance edges without merging the actual role-local accounts.
        AddLiquidDependencies(nodes, edges, handoff, masterRole, slaveRole);

        return new WorldOrderSymbolicOperationProgram(
            isBClass, masterRole, slaveRole, nodes, edges);
    }

    private static void ValidatePreparationInput(
        WorldOrderPreparatoryOperationAssignment assignment,
        WorldOrderSymbolicJarVersion initial)
    {
        if (assignment.Candidate.SourceShape != initial.Shape ||
            assignment.Candidate.SourceLiquid != initial.Liquid)
            throw new ArgumentException("准备操作候选与其抽象 Jar 初始状态不匹配：" + assignment.JarKey);
    }

    private static void EnsureCandidateInput(
        WorldOrderRuleCandidate candidate,
        WorldOrderSymbolicJarVersion input)
    {
        if (candidate.SourceShape != input.Shape || candidate.SourceLiquid != input.Liquid)
            throw new ArgumentException("操作候选与当前 Jar 状态不匹配：" + input.JarKey);
    }

    private static WorldOrderSymbolicJarVersion NextVersion(
        WorldOrderSymbolicJarVersion input,
        string role,
        WorldOrderRuleCandidate candidate) =>
        new(input.JarKey, checked(input.Revision + 1), role,
            candidate.TargetShape, candidate.TargetLiquid);

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
                var masterCanProduce = producers.Any(x =>
                    x.WorkshopRole == masterRole &&
                    x.Sequence < handoff.Sequence &&
                    x.Candidate!.ProducedFreeLiquid == liquid);
                var slaveHasPriorProduction = earlier.Length > 0;
                if (masterCanProduce || !slaveHasPriorProduction)
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

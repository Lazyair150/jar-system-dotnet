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
    WorldOrderRuleCandidate Candidate,
    int Order = 0);

/// <summary>
/// Expands a direct requirement solution with explicitly selected, ordered preparation
/// chains. Multiple operations may transform one Jar identity; each transition advances
/// its revision and must consume the immediately preceding state.
/// This does not enumerate preparation combinations or bind physical inventory.
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
                          !x.Candidate.Operation.StartsWith("PREPARE_", StringComparison.Ordinal)))
            throw new ArgumentException("准备操作必须是目录生成的 PREPARE_* 转换候选", nameof(preparations));
        if (prep.Any(x => x.Order < 0))
            throw new ArgumentOutOfRangeException(nameof(preparations), "准备操作顺序不能为负数");

        var prepGroups = prep.GroupBy(x => x.JarKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(x => x.Order).ToArray(),
                StringComparer.Ordinal);
        foreach (var group in prepGroups.Values)
        {
            if (group.Select(x => x.Order).Distinct().Count() != group.Length)
                throw new ArgumentException("同一 Jar 的准备操作顺序不能重复", nameof(preparations));
            if (group.Select(x => x.WorkshopRole).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new ArgumentException("同一 Jar 的准备操作链不能跨工作台执行", nameof(preparations));
            for (var i = 1; i < group.Length; i++)
            {
                var previous = group[i - 1].Candidate;
                var next = group[i].Candidate;
                if (previous.TargetShape != next.SourceShape ||
                    previous.TargetLiquid != next.SourceLiquid)
                    throw new ArgumentException("同一 Jar 的准备操作链状态不连续：" + group[i].JarKey,
                        nameof(preparations));
            }
        }

        foreach (var selectedPrep in prep)
        {
            var prefix = "requirement-";
            if (!selectedPrep.JarKey.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(selectedPrep.JarKey.Substring(prefix.Length), out var requirementIndex))
                continue;
            var matchingRequirement = requirements.FirstOrDefault(
                x => x.Candidate.RequirementIndex == requirementIndex);
            if (matchingRequirement == null) continue;
            var requirementRole = matchingRequirement.WorkshopRole;
            var allowedRole = requirementRole == slaveRole
                ? selectedPrep.WorkshopRole == masterRole || selectedPrep.WorkshopRole == slaveRole
                : selectedPrep.WorkshopRole == masterRole;
            if (!allowedRole)
                throw new ArgumentException("准备操作不能在 Jar 已归属的主从阶段之后回到主台", nameof(preparations));
        }

        var prepByJar = prepGroups;
        var requirementJarKeys = requirements.ToDictionary(
            x => x.Candidate.RequirementIndex,
            x => "requirement-" + x.Candidate.RequirementIndex);
        var allInitial = new Dictionary<string, WorldOrderSymbolicJarVersion>(StringComparer.Ordinal);

        foreach (var assigned in requirements)
        {
            var key = requirementJarKeys[assigned.Candidate.RequirementIndex];
            var candidate = assigned.Candidate;
            var hasPreparation = prepByJar.TryGetValue(key, out var selectedChain);
            var initial = hasPreparation
                ? new WorldOrderSymbolicJarVersion(
                    key, 0, masterRole,
                    selectedChain![0].Candidate.SourceShape,
                    selectedChain[0].Candidate.SourceLiquid)
                : new WorldOrderSymbolicJarVersion(
                    key, 0, masterRole, candidate.SourceShape, candidate.SourceLiquid);
            if (hasPreparation)
            {
                var finalPrep = selectedChain![^1].Candidate;
                if (candidate.SourceShape != finalPrep.TargetShape ||
                    candidate.SourceLiquid != finalPrep.TargetLiquid)
                    throw new ArgumentException("准备操作链输出 Jar 状态与需求候选输入不匹配：" + key);
            }
            allInitial.Add(key, initial);
        }

        foreach (var chain in prepGroups)
        {
            if (allInitial.ContainsKey(chain.Key)) continue;
            var first = chain.Value[0].Candidate;
            allInitial.Add(chain.Key, new WorldOrderSymbolicJarVersion(
                chain.Key, 0, masterRole, first.SourceShape, first.SourceLiquid));
        }

        var nodes = new List<WorldOrderSymbolicOperationNode>();
        var edges = new List<WorldOrderSymbolicOperationDependency>();
        var current = new Dictionary<string, WorldOrderSymbolicJarVersion>(allInitial, StringComparer.Ordinal);
        var prepNodeByJar = new Dictionary<string, WorldOrderSymbolicOperationNode>(StringComparer.Ordinal);
        var sequence = 0;

        // Master-side preparations happen before every Master requirement operation
        // and before the one batch handoff.
        foreach (var chain in prepGroups.Values
                     .Where(x => x[0].WorkshopRole == masterRole)
                     .OrderBy(x => x[0].JarKey, StringComparer.Ordinal))
        {
            foreach (var selectedPrep in chain)
            {
                var input = current[selectedPrep.JarKey];
                EnsureCandidateInput(selectedPrep.Candidate, input);
                var output = NextVersion(input, masterRole, selectedPrep.Candidate);
                var node = WorldOrderSymbolicOperationNode.Rule(
                    "prepare-" + selectedPrep.JarKey + "-" + selectedPrep.Order,
                    sequence++, masterRole, null, selectedPrep.Candidate, input, output);
                nodes.Add(node);
                if (prepNodeByJar.TryGetValue(selectedPrep.JarKey, out var previous))
                    edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                        previous.NodeId, node.NodeId, selectedPrep.JarKey, input.Revision));
                prepNodeByJar[selectedPrep.JarKey] = node;
                current[selectedPrep.JarKey] = output;
            }
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
            if (prepNodeByJar.TryGetValue(key, out var producer))
                edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                    producer.NodeId, node.NodeId, key, input.Revision));
            current[key] = output;
        }

        WorldOrderSymbolicOperationNode? handoff = null;
        var slaveJarKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var assigned in requirements.Where(x => x.WorkshopRole == slaveRole))
            slaveJarKeys.Add(requirementJarKeys[assigned.Candidate.RequirementIndex]);
        foreach (var chain in prepGroups.Values.Where(x => x[0].WorkshopRole == slaveRole))
            slaveJarKeys.Add(chain[0].JarKey);

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

        foreach (var chain in prepGroups.Values
                     .Where(x => x[0].WorkshopRole == slaveRole)
                     .OrderBy(x => x[0].JarKey, StringComparer.Ordinal))
        {
            foreach (var selectedPrep in chain)
            {
                var input = current[selectedPrep.JarKey];
                EnsureCandidateInput(selectedPrep.Candidate, input);
                var output = NextVersion(input, slaveRole, selectedPrep.Candidate);
                var node = WorldOrderSymbolicOperationNode.Rule(
                    "prepare-" + selectedPrep.JarKey + "-" + selectedPrep.Order,
                    sequence++, slaveRole, null, selectedPrep.Candidate, input, output);
                nodes.Add(node);
                var producer = prepNodeByJar.GetValueOrDefault(selectedPrep.JarKey);
                var producerId = producer?.WorkshopRole == slaveRole
                    ? producer.NodeId
                    : handoff!.NodeId;
                edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                    producerId, node.NodeId, selectedPrep.JarKey, input.Revision));
                prepNodeByJar[selectedPrep.JarKey] = node;
                current[selectedPrep.JarKey] = output;
            }
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
            var producer = prepNodeByJar.GetValueOrDefault(key);
            // A Master-side preparation feeds the handoff; the Slave requirement
            // consumes the post-handoff Jar version, so its immediate producer is
            // the handoff node, not the pre-handoff preparation node.
            var jarStateProducer = producer?.WorkshopRole == slaveRole
                ? producer.NodeId
                : handoff!.NodeId;
            edges.Add(WorldOrderSymbolicOperationDependency.JarState(
                jarStateProducer, node.NodeId, key, input.Revision));
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

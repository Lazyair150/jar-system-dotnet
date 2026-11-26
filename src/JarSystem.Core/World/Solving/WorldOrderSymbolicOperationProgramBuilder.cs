using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// Projects the existing one-candidate-per-requirement solution into an ordered graph.
/// This deliberately does not choose preparatory operations or prove resource sufficiency.
/// </summary>
public static class WorldOrderSymbolicOperationProgramBuilder
{
    public static WorldOrderSymbolicOperationProgram BuildDirectCandidateProjection(
        WorldOrderSymbolicSolution solution,
        bool isBClass,
        string masterRole,
        string slaveRole)
    {
        if (solution == null) throw new ArgumentNullException(nameof(solution));
        if (string.IsNullOrWhiteSpace(masterRole)) throw new ArgumentException("主台角色不能为空", nameof(masterRole));
        if (string.IsNullOrWhiteSpace(slaveRole)) throw new ArgumentException("从台角色不能为空", nameof(slaveRole));
        if (masterRole == slaveRole) throw new ArgumentException("主台和从台角色必须不同");

        var steps = solution.Steps.ToArray();
        if (steps.Any(x => x == null || x.Candidate == null))
            throw new ArgumentException("符号解不能包含空的岗位候选", nameof(solution));
        if (steps.Any(x => x.Candidate.RequirementIndex < 0))
            throw new ArgumentException("直接候选解不能把准备操作伪装成需求步骤", nameof(solution));
        if (steps.Select(x => x.Candidate.RequirementIndex).Distinct().Count() != steps.Length)
            throw new ArgumentException("每条订单需求在直接候选解中必须且只能出现一次", nameof(solution));
        if (steps.Any(x => x.WorkshopRole != masterRole && x.WorkshopRole != slaveRole))
            throw new ArgumentException("符号解包含未知工作台角色", nameof(solution));
        if (!isBClass && steps.Any(x => x.WorkshopRole == slaveRole))
            throw new ArgumentException("A 类订单不能分配从台候选", nameof(solution));

        var masterSteps = steps
            .Where(x => x.WorkshopRole == masterRole)
            .OrderBy(x => x.Candidate.RequirementIndex)
            .ToArray();
        var slaveSteps = steps
            .Where(x => x.WorkshopRole == slaveRole)
            .OrderBy(x => x.Candidate.RequirementIndex)
            .ToArray();

        var nodes = new List<WorldOrderSymbolicOperationNode>();
        var dependencies = new List<WorldOrderSymbolicOperationDependency>();
        var currentByJar = new Dictionary<string, WorldOrderSymbolicJarVersion>(StringComparer.Ordinal);
        var sequence = 0;

        foreach (var assigned in steps)
        {
            var candidate = assigned.Candidate;
            var jarKey = "requirement-" + candidate.RequirementIndex;
            currentByJar.Add(jarKey, new WorldOrderSymbolicJarVersion(
                jarKey, 0, masterRole, candidate.SourceShape, candidate.SourceLiquid));
        }

        foreach (var assigned in masterSteps)
        {
            var candidate = assigned.Candidate;
            var jarKey = "requirement-" + candidate.RequirementIndex;
            var input = currentByJar[jarKey];
            var output = new WorldOrderSymbolicJarVersion(
                jarKey, checked(input.Revision + 1), masterRole,
                candidate.TargetShape, candidate.TargetLiquid, isMarked: true);
            var nodeId = "requirement-" + candidate.RequirementIndex + "-master";
            nodes.Add(WorldOrderSymbolicOperationNode.Rule(
                nodeId, sequence++, masterRole, candidate.RequirementIndex,
                candidate, input, output));
            currentByJar[jarKey] = output;
        }

        WorldOrderSymbolicOperationNode? handoff = null;
        if (isBClass)
        {
            // Every B-class delivery Jar crosses the ownership boundary, including
            // requirements already completed on Master. Receiver-only Slave plans
            // must not silently lose Master-completed output Jars.
            var handoffInputs = steps
                .OrderBy(assigned => assigned.Candidate.RequirementIndex)
                .Select(assigned => currentByJar["requirement-" + assigned.Candidate.RequirementIndex])
                .ToArray();

            handoff = WorldOrderSymbolicOperationNode.MasterToSlaveHandoff(
                "master-to-slave-handoff", sequence++, masterRole, slaveRole, handoffInputs);
            nodes.Add(handoff);

            foreach (var assigned in masterSteps)
            {
                var jarKey = "requirement-" + assigned.Candidate.RequirementIndex;
                dependencies.Add(WorldOrderSymbolicOperationDependency.JarState(
                    "requirement-" + assigned.Candidate.RequirementIndex + "-master",
                    handoff.NodeId, jarKey, currentByJar[jarKey].Revision));
            }
        }

        var handoffOutputByJar = handoff?.OutputJars.ToDictionary(x => x.JarKey, StringComparer.Ordinal)
            ?? new Dictionary<string, WorldOrderSymbolicJarVersion>(StringComparer.Ordinal);

        foreach (var assigned in slaveSteps)
        {
            var candidate = assigned.Candidate;
            var jarKey = "requirement-" + candidate.RequirementIndex;
            var input = handoffOutputByJar[jarKey];
            var output = new WorldOrderSymbolicJarVersion(
                jarKey, checked(input.Revision + 1), slaveRole,
                candidate.TargetShape, candidate.TargetLiquid, isMarked: true);
            var nodeId = "requirement-" + candidate.RequirementIndex + "-slave";
            nodes.Add(WorldOrderSymbolicOperationNode.Rule(
                nodeId, sequence++, slaveRole, candidate.RequirementIndex, candidate, input, output));
            dependencies.Add(WorldOrderSymbolicOperationDependency.JarState(
                handoff!.NodeId, nodeId, jarKey, input.Revision));

            if (candidate.ConsumedFreeLiquid != null)
                dependencies.Add(WorldOrderSymbolicOperationDependency.FreeLiquid(
                    handoff.NodeId, nodeId, candidate.ConsumedFreeLiquid));
        }

        if (handoff != null)
        {
            var slaveLiquidNeeds = new HashSet<string>(
                slaveSteps.Select(x => x.Candidate.ConsumedFreeLiquid)
                    .OfType<string>(),
                StringComparer.Ordinal);
            foreach (var assigned in masterSteps)
            {
                var produced = assigned.Candidate.ProducedFreeLiquid;
                if (produced != null && slaveLiquidNeeds.Contains(produced))
                {
                    dependencies.Add(WorldOrderSymbolicOperationDependency.FreeLiquid(
                        "requirement-" + assigned.Candidate.RequirementIndex + "-master",
                        handoff.NodeId,
                        produced));
                }
            }
        }

        return new WorldOrderSymbolicOperationProgram(
            isBClass, masterRole, slaveRole, nodes, dependencies);
    }
}

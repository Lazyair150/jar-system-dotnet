using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Solving;

public enum WorldOrderSlaveBoundaryProjectionStatus
{
    Projected,
    Infeasible
}

/// <summary>
/// Projects one already ordered B-class symbolic program branch into its exact
/// Master-to-Slave boundary requirements. It does not enumerate alternative rules.
/// </summary>
public sealed class WorldOrderSlaveBoundaryProjectionResult
{
    public WorldOrderSlaveBoundaryProjectionStatus Status { get; }
    public WorldOrderResourceRequirementGroup? Group { get; }
    public string? Reason { get; }
    public int ReplayCount { get; }

    public bool IsProjected => Status == WorldOrderSlaveBoundaryProjectionStatus.Projected;

    internal WorldOrderSlaveBoundaryProjectionResult(
        WorldOrderSlaveBoundaryProjectionStatus status,
        WorldOrderResourceRequirementGroup? group,
        string? reason,
        int replayCount)
    {
        Status = status;
        Group = group;
        Reason = reason;
        ReplayCount = replayCount;
    }
}

/// <summary>
/// Derives one branch's boundary from its ordered Slave operations.
/// Jar state is taken from the unique handoff inputs and remains independent from
/// free-liquid staging. Free-liquid requirements are the least initial staging
/// vector needed to replay the Slave operation sequence, including local production,
/// rejection and aggregate staging-capacity overflow.
/// </summary>
public static class WorldOrderSlaveBoundaryProjector
{
    public static WorldOrderSlaveBoundaryProjectionResult Project(
        WorldOrderSymbolicOperationProgram program,
        string groupId,
        int slaveStagingCapacity,
        IEnumerable<string>? masterBannedLiquids = null,
        IEnumerable<string>? slaveRejectedLiquids = null)
    {
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentException("资源组身份不能为空", nameof(groupId));
        if (slaveStagingCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(slaveStagingCapacity));
        if (!program.IsBClass)
            throw new ArgumentException("从台边界投影只接受 B 类订单程序", nameof(program));

        var providerBanned = CopyNames(masterBannedLiquids, nameof(masterBannedLiquids));
        var consumerRejected = CopyNames(slaveRejectedLiquids, nameof(slaveRejectedLiquids));
        var handoff = program.Nodes.Single(x =>
            x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff);

        var requirements = new List<WorldOrderBoundaryRequirement>();
        foreach (var jar in handoff.InputJars)
        {
            var firstConsumer = program.Nodes.FirstOrDefault(node =>
                node.Kind == WorldOrderSymbolicOperationKind.Rule &&
                node.WorkshopRole == program.SlaveRole &&
                node.InputJars.Any(input =>
                    input.JarKey == jar.JarKey &&
                    input.Revision == checked(jar.Revision + 1)));
            requirements.Add(WorldOrderBoundaryRequirement.JarState(
                program.MasterRole,
                program.SlaveRole,
                jar.Shape,
                jar.Liquid,
                firstConsumer?.NodeId ?? handoff.NodeId,
                firstConsumer?.RequirementIndex));
        }

        var slaveOperations = program.Nodes
            .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                        x.WorkshopRole == program.SlaveRole)
            .OrderBy(x => x.Sequence)
            .ToArray();

        var initial = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var replayCount = 0;
        for (var replay = 0; replay <= slaveOperations.Length; replay++)
        {
            replayCount++;
            if (Total(initial) > slaveStagingCapacity)
                return Infeasible("从台所需的一次性交接游离液体总量超过从台暂存容量", replayCount);

            var balances = new SortedDictionary<string, int>(initial, StringComparer.Ordinal);
            var shortage = false;

            foreach (var node in slaveOperations)
            {
                var candidate = node.Candidate!;
                var consumed = candidate.ConsumedFreeLiquid;
                if (consumed != null)
                {
                    var available = balances.GetValueOrDefault(consumed);
                    if (available < 1)
                    {
                        if (consumerRejected.Contains(consumed))
                            return Infeasible("从台拒收所需游离液体，且从台本地操作无法提供：" + consumed, replayCount);
                        if (providerBanned.Contains(consumed))
                            return Infeasible("该分支需要主台 staging 提供被 ban 的游离液体：" + consumed, replayCount);

                        var deficit = 1 - available;
                        initial[consumed] = checked(initial.GetValueOrDefault(consumed) + deficit);
                        requirements.Add(WorldOrderBoundaryRequirement.FreeLiquid(
                            program.MasterRole,
                            program.SlaveRole,
                            consumed,
                            deficit,
                            node.NodeId,
                            node.RequirementIndex));
                        shortage = true;
                        break;
                    }

                    if (available == 1) balances.Remove(consumed);
                    else balances[consumed] = available - 1;
                }

                var produced = candidate.ProducedFreeLiquid;
                if (produced == null || consumerRejected.Contains(produced))
                    continue;

                if (Total(balances) < slaveStagingCapacity)
                    balances[produced] = checked(balances.GetValueOrDefault(produced) + 1);
                // Rejected and overflowed output is discarded and cannot satisfy later steps.
            }

            if (shortage)
                continue;

            var group = new WorldOrderResourceRequirementGroup(
                groupId,
                requirements,
                program.MasterRole,
                program.SlaveRole,
                slaveOperations.Select(x => x.NodeId));
            return new WorldOrderSlaveBoundaryProjectionResult(
                WorldOrderSlaveBoundaryProjectionStatus.Projected,
                group, null, replayCount);
        }

        return Infeasible("边界资源迭代未能收敛；该分支不能被证明可行", replayCount);

        WorldOrderSlaveBoundaryProjectionResult Infeasible(string reason, int count) =>
            new(WorldOrderSlaveBoundaryProjectionStatus.Infeasible, null, reason, count);
    }

    private static HashSet<string> CopyNames(IEnumerable<string>? names, string parameterName)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("液体名称不能为空", parameterName);
            result.Add(name);
        }
        return result;
    }

    private static int Total(IReadOnlyDictionary<string, int> balances)
    {
        var total = 0;
        foreach (var amount in balances.Values)
            total = checked(total + amount);
        return total;
    }
}

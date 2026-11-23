using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.World;

namespace JarSystem.Core.World.Solving;

public enum WorldOrderOrderedResourceReplayStatus
{
    Complete,
    Infeasible,
    Undetermined
}

/// <summary>
/// Result of ordered Master staging replay plus the existing Slave boundary projection.
/// Complete means the minimum free-liquid boundary is derived and supplied by the Master
/// resource source. It is still not a concrete execution admission or Manifest.
/// </summary>
public sealed class WorldOrderOrderedResourceReplayResult
{
    public WorldOrderOrderedResourceReplayStatus Status { get; }
    public IReadOnlyDictionary<string, int> MasterInitialLiquids { get; }
    public IReadOnlyDictionary<string, int> MasterFinalLiquids { get; }
    public IReadOnlyDictionary<string, int> TransferLiquids { get; }
    public int MasterStagingPeakCapacity { get; }
    public int SlaveStagingPeakCapacity { get; }
    public WorldOrderResourceRequirementGroup? SlaveBoundaryGroup { get; }
    /// <summary>Per-Master-step accepted free-liquid output; null means no output or policy/capacity discard.</summary>
    public IReadOnlyList<string?> MasterAcceptedProducedLiquids { get; }
    /// <summary>Per-Slave-step accepted free-liquid output; null means no output or policy/capacity discard.</summary>
    public IReadOnlyList<string?> SlaveAcceptedProducedLiquids { get; }
    /// <summary>Explicit Slave staging rejects, kept separate from the Slave workshop Ban.</summary>
    public IReadOnlyList<string> ExplicitSlaveRejectedLiquids { get; }
    public string? Reason { get; }
    public bool IsComplete => Status == WorldOrderOrderedResourceReplayStatus.Complete;

    internal WorldOrderOrderedResourceReplayResult(
        WorldOrderOrderedResourceReplayStatus status,
        IReadOnlyDictionary<string, int>? masterInitial,
        IReadOnlyDictionary<string, int>? masterFinal,
        IReadOnlyDictionary<string, int>? transfer,
        int peak,
        int slavePeak,
        WorldOrderResourceRequirementGroup? slaveBoundary,
        IEnumerable<string?>? masterAcceptedProducedLiquids,
        IEnumerable<string?>? slaveAcceptedProducedLiquids,
        IEnumerable<string>? explicitSlaveRejectedLiquids,
        string? reason)
    {
        Status = status;
        MasterInitialLiquids = Freeze(masterInitial);
        MasterFinalLiquids = Freeze(masterFinal);
        TransferLiquids = Freeze(transfer);
        MasterStagingPeakCapacity = peak;
        SlaveStagingPeakCapacity = slavePeak;
        SlaveBoundaryGroup = slaveBoundary;
        MasterAcceptedProducedLiquids = Array.AsReadOnly((masterAcceptedProducedLiquids ?? Array.Empty<string?>()).ToArray());
        SlaveAcceptedProducedLiquids = Array.AsReadOnly((slaveAcceptedProducedLiquids ?? Array.Empty<string?>()).ToArray());
        ExplicitSlaveRejectedLiquids = Array.AsReadOnly(
            (explicitSlaveRejectedLiquids ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Reason = reason;
    }

    private static IReadOnlyDictionary<string, int> Freeze(IReadOnlyDictionary<string, int>? source) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(source ?? new Dictionary<string, int>(), StringComparer.Ordinal));
}

/// <summary>
/// Replays free-liquid effects in symbolic operation order. Every operation consumes
/// before producing. Produced liquids enter only the current workshop staging account;
/// banned Master output and capacity overflow are discarded. B-class Slave requirements
/// are projected with the boundary projector, so Slave-local output is usable only after
/// it has actually been accepted by Slave staging.
/// </summary>
public static class WorldOrderOrderedResourceReplay
{
    public static WorldOrderOrderedResourceReplayResult Replay(
        WorldOrderSymbolicOperationProgram program,
        WorldSnapshot world,
        IEnumerable<string>? slaveRejectedLiquids = null,
        string boundaryGroupId = "materialized-branch")
    {
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (string.IsNullOrWhiteSpace(boundaryGroupId))
            throw new ArgumentException("资源边界组身份不能为空", nameof(boundaryGroupId));
        var explicitRejected = (slaveRejectedLiquids ?? Array.Empty<string>()).ToArray();
        if (explicitRejected.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("从台拒收液体名称不能为空", nameof(slaveRejectedLiquids));
        var explicitRejectedSnapshot = Array.AsReadOnly(
            explicitRejected.Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray());

        if (world.Master == null)
            return Failure(WorldOrderOrderedResourceReplayStatus.Undetermined, "缺少主台快照");
        if (program.IsBClass && (world.Slave == null || world.Slave.HasOpaqueSolverRules))
            return Failure(WorldOrderOrderedResourceReplayStatus.Undetermined, "B 类订单缺少从台快照或从台规则不可静态重放");

        var banned = new HashSet<string>(world.Master.Capability.Ban, StringComparer.Ordinal);
        // A Slave ban is an intrinsic receive/use restriction for free-liquid staging.
        // Explicit rejects add policy; they never replace the workshop's own ban.
        var rejected = new HashSet<string>(
            world.Slave?.Capability.Ban ?? Array.Empty<string>(), StringComparer.Ordinal);
        foreach (var liquid in explicitRejectedSnapshot)
            rejected.Add(liquid);

        WorldOrderResourceRequirementGroup? boundary = null;
        var slavePeak = 0;
        IReadOnlyDictionary<string, int> transfer = new Dictionary<string, int>(StringComparer.Ordinal);
        if (program.IsBClass)
        {
            if (world.Slave!.Jars.Count != 0 || world.Slave.Liquids.Values.Any(x => x != 0))
                return Failure(WorldOrderOrderedResourceReplayStatus.Infeasible, "B 类订单开始时从台必须为空");

            var projection = WorldOrderSlaveBoundaryProjector.Project(
                program,
                boundaryGroupId,
                world.Slave.LiquidStagingCapacity,
                banned,
                rejected);
            if (!projection.IsProjected || projection.Group == null)
                return Failure(WorldOrderOrderedResourceReplayStatus.Infeasible,
                    projection.Reason ?? "从台边界资源无法投影");
            boundary = projection.Group;
            slavePeak = projection.SlaveStagingPeakCapacity;
            transfer = boundary.RequiredFreeLiquid;
        }

        var masterNodes = program.Nodes
            .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                        x.WorkshopRole == program.MasterRole)
            .OrderBy(x => x.Sequence)
            .ToArray();
        var slaveNodes = program.Nodes
            .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                        x.WorkshopRole == program.SlaveRole)
            .OrderBy(x => x.Sequence)
            .ToArray();
        if (world.Master.HasOpaqueSolverRules && masterNodes.Length > 0)
            return Failure(WorldOrderOrderedResourceReplayStatus.Undetermined,
                "主台存在无法静态重放的规则，且当前 B 类程序需要执行主台操作");
        var capacity = world.Master.LiquidStagingCapacity;
        var initial = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var maxReplays = masterNodes.Length + transfer.Count + 2;

        for (var replay = 0; replay < maxReplays; replay++)
        {
            if (Total(initial) > capacity)
                return Failure(WorldOrderOrderedResourceReplayStatus.Infeasible,
                    "主台所需初始游离液体超过主台暂存容量");

            var balances = new SortedDictionary<string, int>(initial, StringComparer.Ordinal);
            var peak = Total(balances);
            var restart = false;
            var acceptedMasterOutputs = new List<string?>();

            foreach (var node in masterNodes)
            {
                var candidate = node.Candidate!;
                var consumed = candidate.ConsumedFreeLiquid;
                if (consumed != null)
                {
                    if (banned.Contains(consumed))
                        return Failure(WorldOrderOrderedResourceReplayStatus.Infeasible,
                            "主台规则需要使用被 ban 的 staging 游离液体：" + consumed);
                    var available = balances.GetValueOrDefault(consumed);
                    if (available < 1)
                    {
                        initial[consumed] = checked(initial.GetValueOrDefault(consumed) + 1 - available);
                        restart = true;
                        break;
                    }
                    if (available == 1) balances.Remove(consumed);
                    else balances[consumed] = available - 1;
                }

                var produced = candidate.ProducedFreeLiquid;
                string? accepted = null;
                if (produced != null && !banned.Contains(produced) && Total(balances) < capacity)
                {
                    balances[produced] = checked(balances.GetValueOrDefault(produced) + 1);
                    peak = Math.Max(peak, Total(balances));
                    accepted = produced;
                }
                // Banned output and capacity overflow are intentionally discarded.
                acceptedMasterOutputs.Add(accepted);
            }

            if (restart)
                continue;

            foreach (var requirement in transfer)
            {
                if (requirement.Value <= balances.GetValueOrDefault(requirement.Key))
                    continue;
                if (banned.Contains(requirement.Key))
                    return Failure(WorldOrderOrderedResourceReplayStatus.Infeasible,
                        "主台不能交接被 ban 的 staging 游离液体：" + requirement.Key);
                var deficit = requirement.Value - balances.GetValueOrDefault(requirement.Key);
                initial[requirement.Key] = checked(initial.GetValueOrDefault(requirement.Key) + deficit);
                restart = true;
            }
            if (restart)
                continue;

            var availableSupply = world.ShipLiquidInventory == null
                ? world.Master.Liquids.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
                : world.ShipLiquidInventory.Balances.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            foreach (var item in world.MasterPrefillLiquidBalances)
                availableSupply[item.Key] = checked(availableSupply.GetValueOrDefault(item.Key) + item.Value);
            foreach (var liquid in banned)
                availableSupply.Remove(liquid);

            if (initial.Any(x => availableSupply.GetValueOrDefault(x.Key) < x.Value))
                return Failure(WorldOrderOrderedResourceReplayStatus.Infeasible,
                    "自机液库/主台初始资源不足以提供有序重放所需的主台游离液体");

            var acceptedSlaveOutputs = new List<string?>();
            if (program.IsBClass)
            {
                var slaveReplay = ReplaySlave(slaveNodes, transfer,
                    world.Slave!.LiquidStagingCapacity, rejected);
                if (!slaveReplay.IsComplete)
                    return Failure(WorldOrderOrderedResourceReplayStatus.Undetermined,
                        slaveReplay.Reason ?? "从台有序重放与边界投影不一致");
                if (slaveReplay.PeakCapacity != slavePeak)
                    return Failure(WorldOrderOrderedResourceReplayStatus.Undetermined,
                        "从台有序重放峰值与边界投影不一致");
                acceptedSlaveOutputs.AddRange(slaveReplay.AcceptedOutputs);
            }

            return new WorldOrderOrderedResourceReplayResult(
                WorldOrderOrderedResourceReplayStatus.Complete,
                initial,
                balances,
                transfer,
                peak,
                slavePeak,
                boundary,
                acceptedMasterOutputs,
                acceptedSlaveOutputs,
                explicitRejectedSnapshot,
                null);
        }

        return Failure(WorldOrderOrderedResourceReplayStatus.Undetermined,
            "主台有序资源重放未在有限迭代内收敛");

        WorldOrderOrderedResourceReplayResult Failure(
            WorldOrderOrderedResourceReplayStatus status, string reason) =>
            new(status, null, null, null, 0, 0, null, null, null,
                explicitRejectedSnapshot, reason);
    }

    private static (bool IsComplete, string? Reason, IReadOnlyList<string?> AcceptedOutputs, int PeakCapacity)
        ReplaySlave(
            IReadOnlyList<WorldOrderSymbolicOperationNode> nodes,
            IReadOnlyDictionary<string, int> initial,
            int capacity,
            IReadOnlySet<string> rejected)
    {
        var balances = initial.Where(x => x.Value > 0)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var peak = Total(balances);
        if (peak > capacity)
            return (false, "从台边界初始液体超过暂存容量", Array.Empty<string?>(), 0);

        var acceptedOutputs = new List<string?>();
        foreach (var node in nodes)
        {
            var candidate = node.Candidate!;
            if (candidate.ConsumedFreeLiquid is { } consumed)
            {
                var available = balances.GetValueOrDefault(consumed);
                if (available <= 0)
                    return (false, "从台边界投影后仍缺少游离液体：" + consumed,
                        Array.Empty<string?>(), 0);
                if (available == 1) balances.Remove(consumed);
                else balances[consumed] = available - 1;
            }

            string? accepted = null;
            var produced = candidate.ProducedFreeLiquid;
            if (produced != null && !rejected.Contains(produced) && Total(balances) < capacity)
            {
                balances[produced] = checked(balances.GetValueOrDefault(produced) + 1);
                peak = Math.Max(peak, Total(balances));
                accepted = produced;
            }
            acceptedOutputs.Add(accepted);
        }

        return (true, null, Array.AsReadOnly(acceptedOutputs.ToArray()), peak);
    }

    private static int Total(IReadOnlyDictionary<string, int> balances)
    {
        var total = 0;
        foreach (var amount in balances.Values)
            total = checked(total + amount);
        return total;
    }
}

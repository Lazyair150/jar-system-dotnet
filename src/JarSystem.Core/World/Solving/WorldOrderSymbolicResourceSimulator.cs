using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JarSystem.Core.World.Solving;

public enum WorldOrderSymbolicResourceSimulationStatus
{
    Feasible,
    Infeasible
}

/// <summary>
/// Fixed inputs for simulating one already ordered symbolic program.
/// MasterInitialFreeLiquid is the order-scoped starting balance; Slave starts empty.
/// HandoffFreeLiquid is applied once, at the program's single handoff node.
/// </summary>
public sealed class WorldOrderSymbolicResourceSimulationRequest
{
    public int MasterStagingCapacity { get; }
    public int SlaveStagingCapacity { get; }
    public IReadOnlyDictionary<string, int> MasterInitialFreeLiquid { get; }
    public IReadOnlyDictionary<string, int> HandoffFreeLiquid { get; }

    public WorldOrderSymbolicResourceSimulationRequest(
        int masterStagingCapacity,
        int slaveStagingCapacity,
        IReadOnlyDictionary<string, int> masterInitialFreeLiquid,
        IReadOnlyDictionary<string, int> handoffFreeLiquid)
    {
        if (masterStagingCapacity < 0) throw new ArgumentOutOfRangeException(nameof(masterStagingCapacity));
        if (slaveStagingCapacity < 0) throw new ArgumentOutOfRangeException(nameof(slaveStagingCapacity));
        if (masterInitialFreeLiquid == null) throw new ArgumentNullException(nameof(masterInitialFreeLiquid));
        if (handoffFreeLiquid == null) throw new ArgumentNullException(nameof(handoffFreeLiquid));

        MasterStagingCapacity = masterStagingCapacity;
        SlaveStagingCapacity = slaveStagingCapacity;
        MasterInitialFreeLiquid = CopyAmounts(masterInitialFreeLiquid, nameof(masterInitialFreeLiquid));
        HandoffFreeLiquid = CopyAmounts(handoffFreeLiquid, nameof(handoffFreeLiquid));
        if (Total(MasterInitialFreeLiquid) > MasterStagingCapacity)
            throw new ArgumentException("主台初始订单液体余额超过暂存容量", nameof(masterInitialFreeLiquid));
    }

    private static IReadOnlyDictionary<string, int> CopyAmounts(
        IReadOnlyDictionary<string, int> source, string parameterName)
    {
        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (string.IsNullOrWhiteSpace(item.Key))
                throw new ArgumentException("液体名称不能为空", parameterName);
            if (item.Value <= 0)
                throw new ArgumentException("液体数量必须为正整数；零余额应省略", parameterName);
            result.Add(item.Key, item.Value);
        }
        return new ReadOnlyDictionary<string, int>(result);
    }

    private static int Total(IReadOnlyDictionary<string, int> amounts)
    {
        var total = 0;
        foreach (var amount in amounts.Values) total = checked(total + amount);
        return total;
    }
}

/// <summary>Immutable account snapshot after a resource simulation.</summary>
public sealed class WorldOrderSymbolicRoleResourceState
{
    public int StagingCapacity { get; }
    public int PeakStagingBalance { get; }
    public IReadOnlyDictionary<string, int> FinalFreeLiquid { get; }
    public IReadOnlyDictionary<string, int> AcceptedFreeLiquidProduction { get; }
    public IReadOnlyDictionary<string, int> DiscardedFreeLiquidProduction { get; }
    public IReadOnlyDictionary<string, int> ConsumedFreeLiquid { get; }
    public IReadOnlyDictionary<string, int> ConsumedJarLiquid { get; }

    internal WorldOrderSymbolicRoleResourceState(
        int stagingCapacity,
        int peakStagingBalance,
        IReadOnlyDictionary<string, int> finalFreeLiquid,
        IReadOnlyDictionary<string, int> acceptedFreeLiquidProduction,
        IReadOnlyDictionary<string, int> discardedFreeLiquidProduction,
        IReadOnlyDictionary<string, int> consumedFreeLiquid,
        IReadOnlyDictionary<string, int> consumedJarLiquid)
    {
        StagingCapacity = stagingCapacity;
        PeakStagingBalance = peakStagingBalance;
        FinalFreeLiquid = Copy(finalFreeLiquid);
        AcceptedFreeLiquidProduction = Copy(acceptedFreeLiquidProduction);
        DiscardedFreeLiquidProduction = Copy(discardedFreeLiquidProduction);
        ConsumedFreeLiquid = Copy(consumedFreeLiquid);
        ConsumedJarLiquid = Copy(consumedJarLiquid);
    }

    private static IReadOnlyDictionary<string, int> Copy(IReadOnlyDictionary<string, int> source) =>
        new ReadOnlyDictionary<string, int>(
            new SortedDictionary<string, int>(source, StringComparer.Ordinal));
}

/// <summary>
/// Result for this exact program, starting balance, capacity and fixed handoff vector.
/// Infeasible rejects this candidate schedule only; it is not a proof that the whole order is impossible.
/// </summary>
public sealed class WorldOrderSymbolicResourceSimulationResult
{
    public WorldOrderSymbolicResourceSimulationStatus Status { get; }
    public string? FailureNodeId { get; }
    public string? Reason { get; }
    public bool HandoffApplied { get; }
    public IReadOnlyDictionary<string, WorldOrderSymbolicRoleResourceState> Roles { get; }
    public IReadOnlyDictionary<string, int> HandoffTransferredFreeLiquid { get; }

    internal WorldOrderSymbolicResourceSimulationResult(
        WorldOrderSymbolicResourceSimulationStatus status,
        string? failureNodeId,
        string? reason,
        bool handoffApplied,
        IReadOnlyDictionary<string, WorldOrderSymbolicRoleResourceState> roles,
        IReadOnlyDictionary<string, int> handoffTransferredFreeLiquid)
    {
        Status = status;
        FailureNodeId = failureNodeId;
        Reason = reason;
        HandoffApplied = handoffApplied;
        Roles = new ReadOnlyDictionary<string, WorldOrderSymbolicRoleResourceState>(
            new SortedDictionary<string, WorldOrderSymbolicRoleResourceState>(roles, StringComparer.Ordinal));
        HandoffTransferredFreeLiquid = new ReadOnlyDictionary<string, int>(
            new SortedDictionary<string, int>(handoffTransferredFreeLiquid, StringComparer.Ordinal));
    }
}

/// <summary>
/// Simulates one fixed ordered program against role-local staging accounts.
/// Each rule operation consumes before producing. Produced free liquid is accepted
/// only while aggregate staging capacity remains; overflow is discarded.
/// Jar-internal liquid consumption never touches free-liquid staging.
/// The single B-class handoff is atomic and is never repeated.
/// </summary>
public static class WorldOrderSymbolicResourceSimulator
{
    public static WorldOrderSymbolicResourceSimulationResult Simulate(
        WorldOrderSymbolicOperationProgram program,
        WorldOrderSymbolicResourceSimulationRequest request)
    {
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (!program.IsBClass && request.HandoffFreeLiquid.Count != 0)
            throw new ArgumentException("A 类订单不能提供主从交接液体", nameof(request));

        var roles = new SortedDictionary<string, MutableRoleResourceState>(StringComparer.Ordinal)
        {
            [program.MasterRole] = new MutableRoleResourceState(
                request.MasterStagingCapacity, request.MasterInitialFreeLiquid)
        };
        if (program.IsBClass)
            roles.Add(program.SlaveRole, new MutableRoleResourceState(request.SlaveStagingCapacity));

        var transferred = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var handoffApplied = false;

        foreach (var node in program.Nodes)
        {
            if (node.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff)
            {
                if (handoffApplied)
                    throw new InvalidOperationException("符号程序违反唯一交接约束：交接节点被重复执行");

                var master = roles[program.MasterRole];
                var slave = roles[program.SlaveRole];
                var transferTotal = Total(request.HandoffFreeLiquid);
                if (transferTotal > slave.Capacity)
                    return Failure(node.NodeId, "一次性交接液体总量超过从台暂存容量");

                foreach (var item in request.HandoffFreeLiquid)
                {
                    if (master.Balance(item.Key) < item.Value)
                        return Failure(node.NodeId, "主台在唯一交接时刻缺少液体：" + item.Key);
                }

                // Validate the complete vector before mutating either account.
                foreach (var item in request.HandoffFreeLiquid)
                {
                    master.Consume(item.Key, item.Value);
                    slave.ReceiveExact(item.Key, item.Value);
                    transferred.Add(item.Key, item.Value);
                }
                master.UpdatePeak();
                slave.UpdatePeak();
                handoffApplied = true;
                continue;
            }

            var role = roles[node.WorkshopRole!];
            var candidate = node.Candidate!;
            if (candidate.ConsumedFreeLiquid != null)
            {
                if (!role.TryConsume(candidate.ConsumedFreeLiquid, 1))
                    return Failure(node.NodeId, "岗位游离液体不足：" + candidate.ConsumedFreeLiquid);
            }

            if (candidate.ConsumedJarLiquid != null)
                role.RecordJarLiquidConsumption(candidate.ConsumedJarLiquid);

            if (candidate.ProducedFreeLiquid != null)
                role.ReceiveOrDiscard(candidate.ProducedFreeLiquid, 1);
        }

        return BuildResult(
            WorldOrderSymbolicResourceSimulationStatus.Feasible,
            null, null, handoffApplied, roles, transferred);

        WorldOrderSymbolicResourceSimulationResult Failure(string nodeId, string reason) =>
            BuildResult(
                WorldOrderSymbolicResourceSimulationStatus.Infeasible,
                nodeId, reason, handoffApplied, roles, transferred);
    }

    private static int Total(IReadOnlyDictionary<string, int> amounts)
    {
        var total = 0;
        foreach (var amount in amounts.Values) total = checked(total + amount);
        return total;
    }

    private static WorldOrderSymbolicResourceSimulationResult BuildResult(
        WorldOrderSymbolicResourceSimulationStatus status,
        string? failureNodeId,
        string? reason,
        bool handoffApplied,
        IReadOnlyDictionary<string, MutableRoleResourceState> roles,
        IReadOnlyDictionary<string, int> transferred)
    {
        var snapshots = new SortedDictionary<string, WorldOrderSymbolicRoleResourceState>(StringComparer.Ordinal);
        foreach (var item in roles) snapshots.Add(item.Key, item.Value.Snapshot());
        return new WorldOrderSymbolicResourceSimulationResult(
            status, failureNodeId, reason, handoffApplied, snapshots, transferred);
    }

    private sealed class MutableRoleResourceState
    {
        private readonly SortedDictionary<string, int> _balances = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _accepted = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _discarded = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _consumedFree = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _consumedJar = new(StringComparer.Ordinal);

        public int Capacity { get; }
        public int Peak { get; private set; }

        public MutableRoleResourceState(int capacity)
        {
            Capacity = capacity;
            UpdatePeak();
        }

        public MutableRoleResourceState(int capacity, IReadOnlyDictionary<string, int> initial)
            : this(capacity)
        {
            foreach (var item in initial) _balances.Add(item.Key, item.Value);
            UpdatePeak();
        }

        public int Balance(string liquid) => _balances.GetValueOrDefault(liquid);

        public bool TryConsume(string liquid, int amount)
        {
            if (Balance(liquid) < amount) return false;
            Consume(liquid, amount);
            Add(_consumedFree, liquid, amount);
            UpdatePeak();
            return true;
        }

        public void Consume(string liquid, int amount)
        {
            var remaining = checked(Balance(liquid) - amount);
            if (remaining == 0) _balances.Remove(liquid);
            else _balances[liquid] = remaining;
        }

        public void ReceiveExact(string liquid, int amount) =>
            _balances[liquid] = checked(Balance(liquid) + amount);

        public void ReceiveOrDiscard(string liquid, int amount)
        {
            var freeCapacity = Capacity - TotalBalance();
            var accepted = Math.Min(freeCapacity, amount);
            var discarded = amount - accepted;
            if (accepted > 0)
            {
                _balances[liquid] = checked(Balance(liquid) + accepted);
                Add(_accepted, liquid, accepted);
            }
            if (discarded > 0) Add(_discarded, liquid, discarded);
            UpdatePeak();
        }

        public void RecordJarLiquidConsumption(string liquid) =>
            Add(_consumedJar, liquid, 1);

        public void UpdatePeak() => Peak = Math.Max(Peak, TotalBalance());

        public WorldOrderSymbolicRoleResourceState Snapshot() =>
            new(Capacity, Peak, _balances, _accepted, _discarded, _consumedFree, _consumedJar);

        private int TotalBalance()
        {
            var total = 0;
            foreach (var amount in _balances.Values) total = checked(total + amount);
            return total;
        }

        private static void Add(IDictionary<string, int> amounts, string liquid, int amount) =>
            amounts[liquid] = checked(amounts.GetValueOrDefault(liquid) + amount);
    }
}

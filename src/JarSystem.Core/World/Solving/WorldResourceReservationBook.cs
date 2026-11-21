using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 执行期间的资源占用账本。记录订单对罐子、主台初始游离液体及唯一从台岗位的独占声明，不修改真实库存。
/// </summary>
public sealed class WorldResourceReservationBook
{
    private readonly object _gate = new();
    private readonly Dictionary<int, OrderResourceLease> _byDemand = new();
    private readonly Dictionary<int, int> _jarOwners = new();
    // 当前世界只有一个从台岗位；B 类执行会独占它直到会话终结。
    private int? _slaveOwnerDemandId;

    public int Count { get { lock (_gate) return _byDemand.Count; } }

    /// <summary>捕获所有活动订单的罐子、液体及从台岗位占用声明。</summary>
    public WorldResourceReservationBookSnapshot Capture()
    {
        lock (_gate)
        {
            var copy = _byDemand.Values.OrderBy(item => item.DemandId)
                .Select(item => item.Reservation)
                .ToArray();
            return new WorldResourceReservationBookSnapshot(copy);
        }
    }

    /// <summary>
    /// 从快照重建预留簿。重建时重新生成罐子所有权索引与唯一从台所有权，
    /// 不直接信任快照中可能被篡改或重复的索引。
    /// </summary>
    public static WorldResourceReservationBook Restore(WorldResourceReservationBookSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var restored = new WorldResourceReservationBook();
        lock (restored._gate)
        {
            foreach (var reservation in snapshot.Reservations.OrderBy(item => item.DemandId))
            {
                if (reservation.DemandId <= 0 ||
                    string.IsNullOrWhiteSpace(reservation.WorldVersion) ||
                    restored._byDemand.ContainsKey(reservation.DemandId))
                    throw new InvalidOperationException("资源预留快照包含无效或重复订单。");
                if (reservation.LockedJarIds.Any(id => id <= 0) ||
                    reservation.LockedJarIds.Distinct().Count() != reservation.LockedJarIds.Count)
                    throw new InvalidOperationException("资源预留快照包含无效或重复罐子 ID。");
                foreach (var amount in reservation.MasterInitialLiquidAmounts)
                    if (string.IsNullOrWhiteSpace(amount.Key) || amount.Value < 0)
                        throw new InvalidOperationException("资源预留快照包含无效液体数量。");
                foreach (var jarId in reservation.LockedJarIds)
                {
                    if (restored._jarOwners.ContainsKey(jarId))
                        throw new InvalidOperationException("资源预留快照中的罐子被多个订单同时占用：" + jarId);
                    restored._jarOwners.Add(jarId, reservation.DemandId);
                }
                if (reservation.UsesSlave)
                {
                    if (restored._slaveOwnerDemandId.HasValue)
                        throw new InvalidOperationException("资源预留快照中存在多个从台占用者。");
                    restored._slaveOwnerDemandId = reservation.DemandId;
                }
                restored._byDemand.Add(reservation.DemandId, new OrderResourceLease(reservation));
            }
        }
        return restored;
    }

    public WorldResourceReservation? Find(int demandId)
    {
        lock (_gate) return _byDemand.TryGetValue(demandId, out var value) ? value.Reservation : null;
    }

    /// <summary>读取订单资源所有权聚合；新领域代码应优先使用此入口。</summary>
    public OrderResourceLease? FindLease(int demandId)
    {
        lock (_gate) return _byDemand.TryGetValue(demandId, out var lease) ? lease : null;
    }

    public void MarkMasterExecuting(int demandId)
    {
        lock (_gate) GetRequiredLease(demandId).MarkMasterExecuting();
    }

    public void MarkTransferPending(int demandId)
    {
        lock (_gate) GetRequiredLease(demandId).MarkTransferPending();
    }

    public void MarkTransferCommitted(int demandId)
    {
        lock (_gate) GetRequiredLease(demandId).MarkTransferCommitted();
    }

    public void MarkSlaveExecuting(int demandId)
    {
        lock (_gate) GetRequiredLease(demandId).MarkSlaveExecuting();
    }

    public void RestoreExecutionStage(int demandId, OrderResourceLeaseStage stage)
    {
        lock (_gate) GetRequiredLease(demandId).RestoreExecutionStage(stage);
    }

    private OrderResourceLease GetRequiredLease(int demandId) =>
        _byDemand.TryGetValue(demandId, out var lease)
            ? lease
            : throw new InvalidOperationException("订单缺少资源租约：" + demandId);

    public bool IsJarReserved(int jarId)
    {
        lock (_gate) return _jarOwners.ContainsKey(jarId);
    }

    public int? JarReservationOwner(int jarId)
    {
        lock (_gate) return _jarOwners.TryGetValue(jarId, out var owner) ? owner : null;
    }

    public int ReservedLiquidAmount(string liquid)
    {
        if (string.IsNullOrWhiteSpace(liquid))
            throw new ArgumentException("液体名称不能为空", nameof(liquid));
        lock (_gate)
            return _byDemand.Values.Sum(x => x.InitialLiquidSupply.GetValueOrDefault(liquid));
    }

    /// <summary>
    /// 在同一临界区内检查并登记一张订单的全部资源。
    /// availableLiquids 是准入时主台真实库存；既有预留会从可用量中扣除。
    /// </summary>
    public bool TryReserve(
        WorldOrderResourceManifest manifest,
        IReadOnlyDictionary<string, int> availableLiquids,
        out string? reason)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        if (availableLiquids == null) throw new ArgumentNullException(nameof(availableLiquids));

        lock (_gate)
        {
            if (_byDemand.ContainsKey(manifest.DemandId))
            {
                reason = "该订单已经持有资源预留";
                return false;
            }

            if (manifest.IsPartitioned && _slaveOwnerDemandId.HasValue)
            {
                reason = $"从工作台正由订单 {_slaveOwnerDemandId.Value} 占用";
                return false;
            }

            if (manifest.LockedJarIds.Any(id => id <= 0) ||
                manifest.LockedJarIds.Distinct().Count() != manifest.LockedJarIds.Count)
            {
                reason = "资源清单包含无效或重复罐子 ID";
                return false;
            }

            foreach (var jarId in manifest.LockedJarIds)
            {
                if (_jarOwners.TryGetValue(jarId, out var owner))
                {
                    reason = $"罐子 #{jarId} 已被订单 {owner} 预留";
                    return false;
                }
            }

            foreach (var item in manifest.MasterInitialLiquidAmounts)
            {
                if (string.IsNullOrWhiteSpace(item.Key) || item.Value < 0)
                {
                    reason = "资源清单包含无效液体预留";
                    return false;
                }

                var actual = availableLiquids.GetValueOrDefault(item.Key);
                var alreadyReserved = _byDemand.Values.Sum(
                    x => x.InitialLiquidSupply.GetValueOrDefault(item.Key));
                if (item.Value > actual - alreadyReserved)
                {
                    reason = $"液体「{item.Key}」可预留量不足：需要 {item.Value}，当前库存 {actual}，已有预留 {alreadyReserved}";
                    return false;
                }
            }

            // 所有检查通过后才写入，失败路径不会留下部分预留。
            var lease = new OrderResourceLease(manifest);
            _byDemand.Add(manifest.DemandId, lease);
            foreach (var jarId in lease.LockedJarIds)
                _jarOwners.Add(jarId, manifest.DemandId);
            if (lease.UsesSlave)
                _slaveOwnerDemandId = manifest.DemandId;

            reason = null;
            return true;
        }
    }

    /// <summary>将快照恢复到空预留簿；派生索引由 Restore 重新构建。</summary>
    public void RestoreFrom(WorldResourceReservationBookSnapshot snapshot)
    {
        var restored = Restore(snapshot);
        lock (_gate)
        {
            if (_byDemand.Count != 0 || _jarOwners.Count != 0 || _slaveOwnerDemandId.HasValue)
                throw new InvalidOperationException("只能向空资源预留簿恢复快照。");
            foreach (var pair in restored._byDemand)
                _byDemand.Add(pair.Key, pair.Value);
            foreach (var pair in restored._jarOwners)
                _jarOwners.Add(pair.Key, pair.Value);
            _slaveOwnerDemandId = restored._slaveOwnerDemandId;
        }
    }

    /// <summary>订单结束、取消或中断时释放占用声明；不回滚真实资源变化。</summary>
    public bool Release(int demandId)
    {
        lock (_gate)
        {
            if (!_byDemand.Remove(demandId, out var lease))
                return false;
            lease.MarkSettled();
            foreach (var jarId in lease.LockedJarIds)
            {
                if (_jarOwners.TryGetValue(jarId, out var owner) && owner == demandId)
                    _jarOwners.Remove(jarId);
            }
            if (_slaveOwnerDemandId == demandId)
                _slaveOwnerDemandId = null;
            return true;
        }
    }
}

/// <summary>一张订单当前持有的不可变资源预留记录。</summary>
public sealed class WorldResourceReservation
{
    public int DemandId { get; }
    public string WorldVersion { get; }
    public IReadOnlyList<int> LockedJarIds { get; }
    public IReadOnlyDictionary<string, int> MasterInitialLiquidAmounts { get; }
    public bool UsesSlave { get; }

    internal WorldResourceReservation(
        int demandId, string worldVersion, IEnumerable<int> jarIds,
        IReadOnlyDictionary<string, int> liquids, bool usesSlave)
    {
        DemandId = demandId;
        WorldVersion = worldVersion;
        UsesSlave = usesSlave;
        LockedJarIds = Array.AsReadOnly(jarIds.ToArray());
        MasterInitialLiquidAmounts = new ReadOnlyDictionary<string, int>(
            liquids.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
    }
}


/// <summary>预留簿的纯值快照；索引在恢复时重新构建，避免保存重复派生状态。</summary>
public sealed class WorldResourceReservationBookSnapshot
{
    public WorldResourceReservationBookSnapshot(IEnumerable<WorldResourceReservation> reservations)
    {
        Reservations = Array.AsReadOnly(reservations.Select(item =>
            new WorldResourceReservation(item.DemandId, item.WorldVersion, item.LockedJarIds,
                item.MasterInitialLiquidAmounts, item.UsesSlave)).ToArray());
    }

    public IReadOnlyList<WorldResourceReservation> Reservations { get; }
}

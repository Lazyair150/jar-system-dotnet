using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 执行期间的资源占用账本。只记录订单对罐子与主台初始游离液体的独占声明，不修改真实库存。
/// </summary>
public sealed class WorldResourceReservationBook
{
    private readonly object _gate = new();
    private readonly Dictionary<int, WorldResourceReservation> _byDemand = new();
    private readonly Dictionary<int, int> _jarOwners = new();

    public int Count { get { lock (_gate) return _byDemand.Count; } }

    public WorldResourceReservation? Find(int demandId)
    {
        lock (_gate) return _byDemand.TryGetValue(demandId, out var value) ? value : null;
    }

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
            return _byDemand.Values.Sum(x => x.MasterInitialLiquidAmounts.GetValueOrDefault(liquid));
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
                    x => x.MasterInitialLiquidAmounts.GetValueOrDefault(item.Key));
                if (item.Value > actual - alreadyReserved)
                {
                    reason = $"液体「{item.Key}」可预留量不足：需要 {item.Value}，当前库存 {actual}，已有预留 {alreadyReserved}";
                    return false;
                }
            }

            // 所有检查通过后才写入，失败路径不会留下部分预留。
            var reservation = new WorldResourceReservation(
                manifest.DemandId, manifest.WorldVersion,
                manifest.LockedJarIds, manifest.MasterInitialLiquidAmounts);
            _byDemand.Add(manifest.DemandId, reservation);
            foreach (var jarId in reservation.LockedJarIds)
                _jarOwners.Add(jarId, manifest.DemandId);

            reason = null;
            return true;
        }
    }

    /// <summary>订单结束、取消或中断时释放占用声明；不回滚真实资源变化。</summary>
    public bool Release(int demandId)
    {
        lock (_gate)
        {
            if (!_byDemand.Remove(demandId, out var reservation))
                return false;
            foreach (var jarId in reservation.LockedJarIds)
            {
                if (_jarOwners.TryGetValue(jarId, out var owner) && owner == demandId)
                    _jarOwners.Remove(jarId);
            }
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

    internal WorldResourceReservation(
        int demandId, string worldVersion, IEnumerable<int> jarIds,
        IReadOnlyDictionary<string, int> liquids)
    {
        DemandId = demandId;
        WorldVersion = worldVersion;
        LockedJarIds = Array.AsReadOnly(jarIds.ToArray());
        MasterInitialLiquidAmounts = new ReadOnlyDictionary<string, int>(
            liquids.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
    }
}

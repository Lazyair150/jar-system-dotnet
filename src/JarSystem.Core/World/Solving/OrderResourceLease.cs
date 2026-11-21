using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 一张订单的资源所有权聚合根。
/// Reservation 是旧 API 的只读投影；罐子锁、初始液体供给和从台独占声明由 Lease 统一持有。
/// 后续执行阶段、交接提交与结算状态也归入此聚合，不由 Admission/Session 各自复制所有权事实。
/// </summary>
public sealed class OrderResourceLease
{
    public int DemandId { get; }
    public string WorldVersion { get; }
    public IReadOnlyList<int> LockedJarIds { get; }
    public IReadOnlyDictionary<string, int> InitialLiquidSupply { get; }
    public bool UsesSlave { get; }

    /// <summary>兼容既有 ReservationBook/Restore API 的不可变只读视图。</summary>
    public WorldResourceReservation Reservation { get; }

    internal OrderResourceLease(WorldOrderResourceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        DemandId = manifest.DemandId;
        WorldVersion = manifest.WorldVersion;
        LockedJarIds = Array.AsReadOnly(manifest.LockedJarIds.ToArray());
        InitialLiquidSupply = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            manifest.MasterInitialLiquidAmounts.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
        UsesSlave = manifest.IsPartitioned;
        Reservation = new WorldResourceReservation(
            DemandId, WorldVersion, LockedJarIds, InitialLiquidSupply, UsesSlave);
    }

    internal OrderResourceLease(WorldResourceReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        DemandId = reservation.DemandId;
        WorldVersion = reservation.WorldVersion;
        LockedJarIds = Array.AsReadOnly(reservation.LockedJarIds.ToArray());
        InitialLiquidSupply = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            reservation.MasterInitialLiquidAmounts.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
        UsesSlave = reservation.UsesSlave;
        Reservation = new WorldResourceReservation(
            DemandId, WorldVersion, LockedJarIds, InitialLiquidSupply, UsesSlave);
    }
}

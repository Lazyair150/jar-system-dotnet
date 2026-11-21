using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Solving;

public enum OrderResourceLeaseStage
{
    Admitted,
    MasterExecuting,
    TransferPending,
    TransferCommitted,
    SlaveExecuting,
    Settled
}

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
    public OrderResourceLeaseStage Stage { get; private set; } = OrderResourceLeaseStage.Admitted;

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

    internal void MarkMasterExecuting()
    {
        if (Stage == OrderResourceLeaseStage.Admitted)
            Stage = OrderResourceLeaseStage.MasterExecuting;
        else if (Stage != OrderResourceLeaseStage.MasterExecuting)
            throw new InvalidOperationException("资源租约不能进入主台执行阶段。");
    }

    internal void MarkTransferPending()
    {
        if (!UsesSlave || Stage is not (OrderResourceLeaseStage.MasterExecuting or OrderResourceLeaseStage.Admitted))
            throw new InvalidOperationException("只有 B 类租约可以从主台执行进入交接待提交阶段。");
        Stage = OrderResourceLeaseStage.TransferPending;
    }

    internal void MarkTransferCommitted()
    {
        if (!UsesSlave || Stage != OrderResourceLeaseStage.TransferPending)
            throw new InvalidOperationException("资源租约当前不允许提交主从交接。");
        Stage = OrderResourceLeaseStage.TransferCommitted;
    }

    internal void MarkSlaveExecuting()
    {
        if (!UsesSlave || Stage != OrderResourceLeaseStage.TransferCommitted)
            throw new InvalidOperationException("从台执行必须建立在已提交的主从交接之上。");
        Stage = OrderResourceLeaseStage.SlaveExecuting;
    }

    internal void RestoreExecutionStage(OrderResourceLeaseStage stage)
    {
        if (Stage != OrderResourceLeaseStage.Admitted)
            throw new InvalidOperationException("只能恢复尚未进入执行阶段的资源租约。");
        switch (stage)
        {
            case OrderResourceLeaseStage.Admitted:
                return;
            case OrderResourceLeaseStage.MasterExecuting:
                MarkMasterExecuting();
                return;
            case OrderResourceLeaseStage.TransferPending:
                MarkTransferPending();
                return;
            case OrderResourceLeaseStage.TransferCommitted:
                MarkTransferPending();
                MarkTransferCommitted();
                return;
            case OrderResourceLeaseStage.SlaveExecuting:
                MarkTransferPending();
                MarkTransferCommitted();
                MarkSlaveExecuting();
                return;
            default:
                throw new InvalidOperationException("检查点中的资源租约阶段无效。");
        };
    }

    internal void MarkSettled()
    {
        if (Stage == OrderResourceLeaseStage.Settled) return;
        Stage = OrderResourceLeaseStage.Settled;
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
            DemandId, WorldVersion, LockedJarIds, InitialLiquidSupply, UsesSlave, reservation.Stage);
        RestoreExecutionStage(reservation.Stage);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;

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

/// <summary>订单液体结算的单调阶段；账本副作用完成前不能将结算视为完整。</summary>
public enum OrderResourceSettlementStage
{
    NotStarted,
    ResourcesReturned,
    LedgerRecorded
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
    public IReadOnlyDictionary<string, int> TransferLiquidAmounts { get; }
    public int MasterStagingPeakCapacity { get; }
    public int SlaveStagingPeakCapacity { get; }
    public bool UsesSlave { get; }
    public OrderResourceLeaseStage Stage { get; private set; } = OrderResourceLeaseStage.Admitted;
    private WorldResourceReservation? _reservationProjection;
    private (OrderLiquidScope Master, OrderLiquidScope? Slave)? _liquidScopes;
    public LiquidBatchReceipt? SettlementReceipt { get; private set; }
    public OrderResourceSettlementStage SettlementStage { get; private set; }
    /// <summary>稳定的订单结算操作标识，供后续账本去重/恢复协议使用。</summary>
    public string SettlementOperationId => WorldVersion + ":order:" + DemandId + ":settlement";
    internal bool HasLiquidScopes => _liquidScopes.HasValue;
    internal (OrderLiquidScope Master, OrderLiquidScope? Slave)? LiquidScopes => _liquidScopes;

    internal void AttachLiquidScopes(OrderLiquidScope master, OrderLiquidScope? slave)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (master.OrderId != DemandId || (slave != null && slave.OrderId != DemandId))
            throw new InvalidOperationException("订单液体范围与资源租约身份不一致。");
        if (UsesSlave != (slave != null))
            throw new InvalidOperationException("订单液体范围的主从结构与资源租约不一致。");
        if (_liquidScopes.HasValue)
            throw new InvalidOperationException("资源租约已经绑定液体范围。");
        _liquidScopes = (master, slave);
    }

    internal LiquidBatchReceipt RecordSettlement(LiquidBatchReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (SettlementReceipt != null) return SettlementReceipt;
        if (SettlementStage != OrderResourceSettlementStage.NotStarted)
            throw new InvalidOperationException("订单结算阶段与结算收据不一致。");
        SettlementReceipt = receipt;
        SettlementStage = OrderResourceSettlementStage.ResourcesReturned;
        return receipt;
    }

    internal void MarkSettlementLedgerRecorded()
    {
        if (SettlementStage == OrderResourceSettlementStage.LedgerRecorded) return;
        if (SettlementStage != OrderResourceSettlementStage.ResourcesReturned || SettlementReceipt == null)
            throw new InvalidOperationException("必须先完成订单资源返还，才能提交结算账本阶段。");
        SettlementStage = OrderResourceSettlementStage.LedgerRecorded;
    }

    internal void RestoreSettlement(
        OrderResourceSettlementStage stage,
        LiquidBatchReceipt? receipt)
    {
        if (SettlementStage != OrderResourceSettlementStage.NotStarted || SettlementReceipt != null)
            throw new InvalidOperationException("只能向尚未结算的资源租约恢复结算状态。");
        if (!Enum.IsDefined(stage))
            throw new InvalidOperationException("检查点中的订单结算阶段无效。");
        if (stage == OrderResourceSettlementStage.NotStarted)
        {
            if (receipt != null)
                throw new InvalidOperationException("未开始结算的订单不能包含结算收据。");
            return;
        }
        if (receipt == null)
            throw new InvalidOperationException("已开始结算的订单必须包含结算收据。");
        ValidateReceipt(receipt);
        SettlementReceipt = receipt;
        SettlementStage = stage;
    }

    private static void ValidateReceipt(LiquidBatchReceipt receipt)
    {
        ShipLiquidInventory.ValidateAmounts(receipt.Requested);
        ShipLiquidInventory.ValidateAmounts(receipt.Accepted);
        ShipLiquidInventory.ValidateAmounts(receipt.Discarded);
        var keys = receipt.Requested.Keys
            .Concat(receipt.Accepted.Keys)
            .Concat(receipt.Discarded.Keys)
            .Distinct(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var requested = receipt.Requested.GetValueOrDefault(key);
            var accepted = receipt.Accepted.GetValueOrDefault(key);
            var discarded = receipt.Discarded.GetValueOrDefault(key);
            if (requested != checked(accepted + discarded) ||
                (requested == 0 && (accepted != 0 || discarded != 0)))
                throw new InvalidOperationException("订单结算收据的请求、接收与丢弃数量不一致。");
        }
    }

    /// <summary>兼容既有 ReservationBook/Restore API 的不可变只读视图。</summary>
    public WorldResourceReservation Reservation => _reservationProjection ??= CaptureReservation();

    internal OrderResourceLease(WorldOrderResourceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        DemandId = manifest.DemandId;
        WorldVersion = manifest.WorldVersion;
        LockedJarIds = Array.AsReadOnly(manifest.LockedJarIds.ToArray());
        InitialLiquidSupply = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            manifest.MasterInitialLiquidAmounts.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
        TransferLiquidAmounts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            manifest.TransferLiquids.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
        MasterStagingPeakCapacity = manifest.MasterStagingPeakCapacity;
        SlaveStagingPeakCapacity = manifest.SlaveStagingPeakCapacity;
        UsesSlave = manifest.IsPartitioned;
    }

    internal void MarkMasterExecuting()
    {
        if (Stage == OrderResourceLeaseStage.Admitted)
        { Stage = OrderResourceLeaseStage.MasterExecuting; _reservationProjection = null; }
        else if (Stage != OrderResourceLeaseStage.MasterExecuting)
            throw new InvalidOperationException("资源租约不能进入主台执行阶段。");
    }

    internal void MarkTransferPending()
    {
        if (!UsesSlave || Stage is not (OrderResourceLeaseStage.MasterExecuting or OrderResourceLeaseStage.Admitted))
            throw new InvalidOperationException("只有 B 类租约可以从主台执行进入交接待提交阶段。");
        Stage = OrderResourceLeaseStage.TransferPending; _reservationProjection = null;
    }

    internal void MarkTransferCommitted()
    {
        if (!UsesSlave || Stage != OrderResourceLeaseStage.TransferPending)
            throw new InvalidOperationException("资源租约当前不允许提交主从交接。");
        Stage = OrderResourceLeaseStage.TransferCommitted; _reservationProjection = null;
    }

    internal void MarkSlaveExecuting()
    {
        if (!UsesSlave || Stage != OrderResourceLeaseStage.TransferCommitted)
            throw new InvalidOperationException("从台执行必须建立在已提交的主从交接之上。");
        Stage = OrderResourceLeaseStage.SlaveExecuting; _reservationProjection = null;
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
        Stage = OrderResourceLeaseStage.Settled; _reservationProjection = null;
    }

    internal WorldResourceReservation CaptureReservation() => new(
        DemandId, WorldVersion, LockedJarIds, InitialLiquidSupply, UsesSlave, Stage,
        TransferLiquidAmounts, MasterStagingPeakCapacity, SlaveStagingPeakCapacity);

    internal OrderResourceLease(WorldResourceReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        DemandId = reservation.DemandId;
        WorldVersion = reservation.WorldVersion;
        LockedJarIds = Array.AsReadOnly(reservation.LockedJarIds.ToArray());
        InitialLiquidSupply = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            reservation.MasterInitialLiquidAmounts.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
        TransferLiquidAmounts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            reservation.TransferLiquidAmounts.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
        MasterStagingPeakCapacity = reservation.MasterStagingPeakCapacity;
        SlaveStagingPeakCapacity = reservation.SlaveStagingPeakCapacity;
        UsesSlave = reservation.UsesSlave;
        RestoreExecutionStage(reservation.Stage);
    }
}

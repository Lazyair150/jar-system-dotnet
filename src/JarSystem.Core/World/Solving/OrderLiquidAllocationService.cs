using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 将一张订单的液体资源从自机液库/主台预置账户原子分配到工作台订单暂存范围。
/// Admission 只负责准入编排；本服务负责容量租约、扣款、暂存入账及失败补偿。
/// </summary>
internal sealed class OrderLiquidAllocationService
{
    private readonly ShipLiquidInventory _ship;
    private readonly WorldResourceReservationBook _reservations;
    private readonly Func<Workshop, WorkshopLiquidStaging> _getStaging;

    public OrderLiquidAllocationService(
        ShipLiquidInventory ship,
        WorldResourceReservationBook reservations,
        Func<Workshop, WorkshopLiquidStaging> getStaging)
    {
        _ship = ship ?? throw new ArgumentNullException(nameof(ship));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _getStaging = getStaging ?? throw new ArgumentNullException(nameof(getStaging));
    }

    public IReadOnlyDictionary<string, int> BuildReservationLiquidityView(
        WorldOrderResourceManifest manifest, Workshop master)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(master);
        var prefill = _getStaging(master).PrefillBalances;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var liquid in _ship.Balances.Keys
            .Concat(prefill.Keys)
            .Concat(manifest.MasterInitialLiquidAmounts.Keys)
            .Distinct(StringComparer.Ordinal))
        {
            // 真实扣款已发生的活动订单必须加回逻辑输入，再由 ReservationBook 统一扣除预留，
            // 避免把同一份资源按"库存扣款 + 逻辑预留"重复扣减。
            result[liquid] = checked(_ship.GetAmount(liquid) + prefill.GetValueOrDefault(liquid) +
                _reservations.ReservedLiquidAmount(liquid));
        }
        return result;
    }

    public bool TryAllocate(
        WorldOrderResourceManifest manifest,
        Workshop master,
        Workshop? slave,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(master);

        var masterStaging = _getStaging(master);
        WorkshopLiquidStaging? slaveStaging = null;

        // 预置液先从主台预置账户提取，再预留订单完整路径峰值，
        // 避免同一份预置液同时计入预置占用和订单占用。
        var prefillGrant = masterStaging.WithdrawPrefillUpTo(manifest.MasterInitialLiquidAmounts);
        var remainingFromShip = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in manifest.MasterInitialLiquidAmounts)
        {
            var remaining = item.Value - prefillGrant.GetValueOrDefault(item.Key);
            if (remaining > 0) remainingFromShip.Add(item.Key, remaining);
        }

        if (!masterStaging.TryReserveOrderCapacity(manifest.DemandId, manifest.MasterStagingPeakCapacity))
        {
            masterStaging.RestorePrefill(prefillGrant);
            reason = "主工作台订单液体峰值容量无法预留";
            return false;
        }

        var slaveReserved = false;
        if (manifest.IsPartitioned)
        {
            if (slave == null)
            {
                masterStaging.ReleaseOrderCapacity(manifest.DemandId);
                masterStaging.RestorePrefill(prefillGrant);
                reason = "B 类订单缺少从工作台";
                return false;
            }
            slaveStaging = _getStaging(slave);
            if (!slaveStaging.TryReserveOrderCapacity(manifest.DemandId, manifest.SlaveStagingPeakCapacity))
            {
                masterStaging.ReleaseOrderCapacity(manifest.DemandId);
                masterStaging.RestorePrefill(prefillGrant);
                reason = "从工作台订单液体峰值容量无法预留";
                return false;
            }
            slaveReserved = true;
        }

        if (!_ship.TryWithdraw(remainingFromShip, out _))
        {
            if (slaveReserved) slaveStaging!.ReleaseOrderCapacity(manifest.DemandId);
            masterStaging.ReleaseOrderCapacity(manifest.DemandId);
            masterStaging.RestorePrefill(prefillGrant);
            reason = "自机液库初始供给不足";
            return false;
        }

        foreach (var item in manifest.MasterInitialLiquidAmounts)
        {
            if (item.Value == 0) continue;
            if (!masterStaging.TryReceiveExact(manifest.DemandId, item.Key, item.Value, out var grant))
            {
                masterStaging.DrainOrder(manifest.DemandId);
                _ship.Return(remainingFromShip);
                masterStaging.RestorePrefill(prefillGrant);
                if (slaveReserved) slaveStaging!.ReleaseOrderCapacity(manifest.DemandId);
                masterStaging.ReleaseOrderCapacity(manifest.DemandId);
                reason = grant.Reason ?? "主工作台暂存区无法接收订单初始供给";
                return false;
            }
        }

        var masterScope = new OrderLiquidScope(manifest.DemandId, masterStaging, master.OwnedInstanceId);
        var slaveScope = slaveStaging == null ? null : new OrderLiquidScope(manifest.DemandId, slaveStaging, slave!.OwnedInstanceId);
        var lease = _reservations.FindLease(manifest.DemandId)
            ?? throw new InvalidOperationException("订单液体分配完成后缺少资源租约。");
        lease.AttachLiquidScopes(masterScope, slaveScope);
        reason = null;
        return true;
    }
}

using System;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World.Execution;

/// <summary>
/// 订单资源结算入口：负责把工作台暂存液体结算回自机液库、写入资源账本并释放准入占用。
/// 从台罐子回滚与参与罐子标记清理由执行会话在调用本服务前完成；本服务不改变 A/B 订单的执行归属。
/// </summary>
public sealed class OrderResourceSettlementService
{
    /// <summary>
    /// 结算订单液体并释放资源租约。tick 必须在调用方开始任何结算变更前预取。
    /// Admission 的结算收据保证同一活动范围内重复读取不会再次 Drain。
    /// </summary>
    public LiquidBatchReceipt? SettleAndRelease(
        int demandId,
        WorldOrderExecutionAdmission admission,
        ResourceLedger? ledger,
        int? settlementTick)
    {
        ArgumentNullException.ThrowIfNull(admission);

        var lease = admission.FindLease(demandId);
        var scopes = lease?.LiquidScopes;
        var hasBalance = scopes.HasValue &&
            (scopes.Value.Master.Balance.Values.Any(value => value > 0) ||
             (scopes.Value.Slave?.Balance.Values.Any(value => value > 0) ?? false));
        if (ledger != null && hasBalance && !settlementTick.HasValue)
            throw new InvalidOperationException("结算前必须取得资源账本逻辑 tick。");

        LiquidBatchReceipt? receipt = lease?.SettlementReceipt;
        if (lease?.SettlementStage == OrderResourceSettlementStage.LedgerRecorded &&
            ledger != null &&
            !ledger.HasRecordedOperation(lease.SettlementOperationId))
            throw new InvalidOperationException(
                "订单结算阶段报告账本已提交，但当前资源账本缺少对应的 Settlement operation receipt。");

        if (receipt == null && scopes.HasValue && admission.ShipLiquidInventory != null)
        {
            if (lease!.SettlementStage != OrderResourceSettlementStage.NotStarted)
                throw new InvalidOperationException("订单结算阶段已推进，但缺少可重放的结算收据。");

            // Aggregate and validate the complete request before draining either account.
            // Two independently bounded Workshops can together exceed Int32 even when
            // each account is valid; checked aggregation must never happen after a drain.
            var requested = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            Merge(requested, scopes.Value.Master.Balance);
            scopes.Value.Master.Staging.EnsureCanTransferOrderLiquids();
            if (scopes.Value.Slave != null)
            {
                Merge(requested, scopes.Value.Slave.Balance);
                scopes.Value.Slave.Staging.EnsureCanTransferOrderLiquids();
            }

            scopes.Value.Master.Staging.DrainOrder(demandId);
            if (scopes.Value.Slave != null)
                scopes.Value.Slave.Staging.DrainOrder(demandId);
            receipt = lease.RecordSettlement(admission.ShipLiquidInventory.Return(requested));
        }

        if (receipt != null && lease!.SettlementStage == OrderResourceSettlementStage.ResourcesReturned &&
            ledger != null)
        {
            var tick = settlementTick ?? 0;
            var mutations = receipt.Accepted.Select(item => new ResourceLedgerMutation(
                    ResourceCauses.Return, ResourceKinds.Liquid, item.Key, item.Value,
                    From: "订单暂存", To: "自机液库"))
                .Concat(receipt.Discarded.Select(item => new ResourceLedgerMutation(
                    ResourceCauses.Discard, ResourceKinds.Liquid, item.Key, item.Value,
                    From: "订单暂存", To: null, Reason: "自机液库总容量不足")));
            ledger.RecordBatchOnce(lease.SettlementOperationId, tick, mutations);
            lease.MarkSettlementLedgerRecorded();
        }

        if (!admission.ReleaseReservationOnly(demandId))
            throw new InvalidOperationException("执行会话结束时找不到对应的资源预留");

        return receipt;
    }

    private static void Merge(
        System.Collections.Generic.Dictionary<string, int> target,
        System.Collections.Generic.IReadOnlyDictionary<string, int> source)
    {
        foreach (var item in source)
            target[item.Key] = checked(target.GetValueOrDefault(item.Key) + item.Value);
    }
}

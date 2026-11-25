using System;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World.Execution;

/// <summary>Settlement 同步提交边界的测试故障注入端口。</summary>
internal interface IOrderSettlementFaultPort
{
    void AfterMasterDrain();
    void AfterSlaveDrain();
    void AfterShipReturnBeforeReceipt();
    void AfterLedgerBatchRecorded();
    void AfterLedgerStageMarked();
    void BeforeReservationRelease();
}

internal sealed class NoopOrderSettlementFaultPort : IOrderSettlementFaultPort
{
    public static NoopOrderSettlementFaultPort Instance { get; } = new();
    private NoopOrderSettlementFaultPort() { }
    public void AfterMasterDrain() { }
    public void AfterSlaveDrain() { }
    public void AfterShipReturnBeforeReceipt() { }
    public void AfterLedgerBatchRecorded() { }
    public void AfterLedgerStageMarked() { }
    public void BeforeReservationRelease() { }
}

/// <summary>
/// 订单资源结算入口：负责把工作台暂存液体结算回自机液库、写入资源账本并释放准入占用。
/// 从台罐子回滚与参与罐子标记清理由执行会话在调用本服务前完成；本服务不改变 A/B 订单的执行归属。
/// </summary>
public sealed class OrderResourceSettlementService
{
    private readonly IOrderSettlementFaultPort _faults;

    public OrderResourceSettlementService() : this(NoopOrderSettlementFaultPort.Instance) { }

    internal OrderResourceSettlementService(IOrderSettlementFaultPort faults) =>
        _faults = faults ?? throw new ArgumentNullException(nameof(faults));

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
            (ledger == null || !ledger.HasRecordedOperation(lease.SettlementOperationId)))
            throw new InvalidOperationException(
                "订单结算阶段报告账本已提交，但当前缺少对应的 ResourceLedger 或 Settlement operation receipt。");

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

            // The staging drains and ship return are one synchronous ownership transfer.
            // If anything fails before the lease receipt is committed, restore all three
            // authoritative accounts from their pre-mutation snapshots.
            var masterBefore = new Dictionary<string, int>(
                scopes.Value.Master.Balance, StringComparer.Ordinal);
            var slaveBefore = scopes.Value.Slave == null
                ? null
                : new Dictionary<string, int>(scopes.Value.Slave.Balance, StringComparer.Ordinal);
            var shipBefore = admission.ShipLiquidInventory.Capture();
            try
            {
                scopes.Value.Master.Staging.DrainOrder(demandId);
                _faults.AfterMasterDrain();
                if (scopes.Value.Slave != null)
                {
                    scopes.Value.Slave.Staging.DrainOrder(demandId);
                    _faults.AfterSlaveDrain();
                }

                var returned = admission.ShipLiquidInventory.Return(requested);
                _faults.AfterShipReturnBeforeReceipt();
                receipt = lease.RecordSettlement(returned);
            }
            catch (Exception settlementError)
            {
                try
                {
                    admission.ShipLiquidInventory.RestoreFrom(shipBefore);
                    scopes.Value.Master.Staging.RestoreOrderAccount(demandId, masterBefore);
                    if (scopes.Value.Slave != null)
                        scopes.Value.Slave.Staging.RestoreOrderAccount(demandId, slaveBefore!);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "订单液体结算在收据提交前失败，且自机/主从暂存账户补偿回滚失败；必须停止世界推进并保留故障现场。",
                        settlementError, rollbackError);
                }
                throw;
            }
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
                    From: "订单暂存", To: null, Reason: "自机液库总容量不足")))
                .ToArray();
            // Zero accepted and zero discarded means no resource changed; do not publish an
            // empty ledger operation, but still advance the settlement lease stage below.
            if (mutations.Length > 0)
                ledger.RecordBatchOnce(lease.SettlementOperationId, tick, mutations);
            _faults.AfterLedgerBatchRecorded();
            lease.MarkSettlementLedgerRecorded();
            _faults.AfterLedgerStageMarked();
        }

        _faults.BeforeReservationRelease();
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

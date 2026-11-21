using System;
using JarSystem.Core.Domain;
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

        var receipt = admission.SettleLiquidResources(demandId);
        if (receipt != null)
        {
            var tick = settlementTick ?? 0;
            foreach (var item in receipt.Accepted)
                ledger?.Record(tick, ResourceCauses.Return,
                    ResourceKinds.Liquid, item.Key, item.Value,
                    from: "订单暂存", to: "自机液库");
            foreach (var item in receipt.Discarded)
                ledger?.Record(tick, ResourceCauses.Discard,
                    ResourceKinds.Liquid, item.Key, item.Value,
                    from: "订单暂存", to: null, reason: "自机液库总容量不足");
        }

        if (!admission.Release(demandId))
            throw new InvalidOperationException("执行会话结束时找不到对应的资源预留");

        return receipt;
    }
}

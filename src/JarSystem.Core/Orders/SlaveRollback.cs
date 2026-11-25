using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;

namespace JarSystem.Core.Orders;

/// <summary>
/// **从台 → 主台的资源回落**（原 <c>OrderEvent.RollbackSlaveToMaster</c>，2026-09 抽出）。
///
/// 从台是 B 类订单临时升起的工具位，不是第二仓库。订单结束后，从台必须无资源残留：
/// <list type="bullet">
/// <item>交付罐先移除，其余罐子全部回落；包括订单未交付的工具罐；</item>
/// <item>从台自由液体全部回落；包括工具罐倒出后未消耗的液体，不能只按“转入液体”账目返还；</item>
/// <item>**禁令边界**：满罐中的液体不受主台禁令影响，原样随罐返回；游离液若主台禁收则丢弃；</item>
/// <item>**容量与丢弃**：主台接不下的罐子/液体**直接丢弃**（仓库单向流动，不回收），并留下日志。</item>
/// </list>
/// 该操作对成功、失败、取消三条收尾路径一致，并由过程互斥保证从台只属于一个 B 类订单。
/// </summary>
internal sealed class SlaveRollback
{
    /// <param name="master">主台（回滚资源的接收方）。</param>
    /// <param name="slave">从台（回滚来源）。</param>
    /// <param name="appendLog">追加执行日志的出口（由 <see cref="OrderEvent"/> 提供）——
    /// 日志本身不再对外可变，因此这里收回调而不是可变 List。</param>
    /// <param name="ledger">资源账本（只读投影）：回落记 `return`，主台收不下的记 `discard`。</param>
    /// <param name="tickOf">取当前逻辑 tick 的出口（账本只记录事实，不推进时间）。</param>
    public void Execute(
        Workshop master,
        Workshop slave,
        Action<ExecutionLogEntry> appendLog,
        Action<int>? countDiscardedJars = null,
        Action<string, int>? countDiscardedLiquids = null,
        ResourceLedger? ledger = null,
        Func<int>? tickOf = null,
        int? ledgerTickOverride = null)
    {
        if (ReferenceEquals(master, slave)) return;

        // 一次结算使用同一个账本 tick；成功交付路径可在不可逆交付之前预取并显式传入。
        // tick 来源由世界装配层提供，不在此换算旧/新时间轴。
        var ledgerTick = ledger == null ? 0 : ledgerTickOverride ?? tickOf?.Invoke() ?? 0;
        var slaveJars = new List<Jar>(slave.Jars);

        foreach (var jar in slaveJars)
        {
            if (jar == null) continue;
            if (jar.Id is not > 0)
                throw new DomainValidationException("从台回落账本事实缺少 canonical JarId。");
            var jarIdentity = jar.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            slave.TransferOut(jar);
            // 禁液令不影响满罐转移：装满被主台禁用液体的罐子原样归还，
            // 不在此清空罐内液体（禁令只作用于倒入、游离存储与改形成本）。
            if (!master.TransferIn(jar))
            {
                countDiscardedJars?.Invoke(1);
                ledger?.Record(ledgerTick, ResourceCauses.Discard, ResourceKinds.Jar,
                    jar.Shape, 1, from: slave.Name, to: null, reason: "主台罐子容量满",
                    resourceIdentity: jarIdentity);
                appendLog(new ExecutionLogEntry
                {
                    Phase = "rollback",
                    Command = "discard_jar",
                    Jar = jar.GetDescription(),
                    Reason = "主台罐子容量满，丢弃（仓库单向流动，不回收）"
                });
            }
            else
            {
                ledger?.Record(ledgerTick, ResourceCauses.Return, ResourceKinds.Jar,
                    jar.Shape, 1, from: slave.Name, to: master.Name,
                    resourceIdentity: jarIdentity);
            }
        }

        foreach (var kv in slave.LiquidStorage.ToList())
        {
            var liquid = kv.Key;
            var amount = kv.Value;
            if (amount > 0)
            {
                slave.RemoveLiquid(liquid, amount);
                // 禁令与容量是两种独立拒收原因：被主台禁用的游离液体全部丢弃；
                // 未被禁用时才按容量部分接收。满罐中的液体随罐返回，不经过这里。
                int accepted = master.IsBanned(liquid)
                    ? 0
                    : master.TryReceiveLiquid(liquid, amount);
                int remainder = amount - accepted;
                ledger?.Record(ledgerTick, ResourceCauses.Return, ResourceKinds.Liquid,
                    liquid, accepted, from: slave.Name, to: master.Name, resourceIdentity: liquid);
                if (remainder > 0)
                {
                    countDiscardedLiquids?.Invoke(liquid, remainder);
                    ledger?.Record(ledgerTick, ResourceCauses.Discard, ResourceKinds.Liquid,
                        liquid, remainder, from: slave.Name, to: null,
                        reason: master.IsBanned(liquid) ? "主台禁" + liquid : "主台液体容量满",
                        resourceIdentity: liquid);
                    appendLog(new ExecutionLogEntry
                    {
                        Phase = "rollback",
                        Command = "discard_liquid",
                        Reason = (master.IsBanned(liquid) ? "主台禁" + liquid : "主台液体容量满") + "，" + liquid + "×" + remainder + " 丢弃（仓库单向流动，不回收）"
                    });
                }
            }
        }
    }
}

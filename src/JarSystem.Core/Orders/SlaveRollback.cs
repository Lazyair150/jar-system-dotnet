using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>
/// **从台 → 主台的回滚**（原 <c>OrderEvent.RollbackSlaveToMaster</c>，2026-09 抽出）。
///
/// 为什么值得独立：这段规则很细，却和"交付判定""命令执行""报告生成"混在同一个事件对象里 ⇒ 无法单独测试。
/// 它同时涉及三类语义：
/// <list type="bullet">
/// <item>**归属判定**：只回滚"本次转移过来"的罐子（按 Id 区分"转移来的"与"从台原有的"）；</item>
/// <item>**禁令边界**：**禁液令不影响满罐转移** —— 装满被主台禁用液体的罐子原样归还，不清空罐内液体；</item>
/// <item>**容量与丢弃**：主台接不下的罐子/液体**直接丢弃**（仓库单向流动，不回收），并留下日志。</item>
/// </list>
/// 语义与抽取前**逐字一致**（等价性由 5000 用例 + S13/S14/S19 守护）。
/// </summary>
internal sealed class SlaveRollback
{
    /// <param name="master">主台（回滚资源的接收方）。</param>
    /// <param name="slave">从台（回滚来源）。</param>
    /// <param name="transferredJarIds">本次订单实际转移过来的罐子 Id。</param>
    /// <param name="slaveOriginalJarIds">订单开始时从台**原有**罐子的 Id（这些不参与回滚）。</param>
    /// <param name="transferredLiquids">本次转移到从台的液体账目。</param>
    /// <param name="log">执行日志（对外报告的数据源）。</param>
    public void Execute(
        Workshop master,
        Workshop slave,
        HashSet<int> transferredJarIds,
        HashSet<int> slaveOriginalJarIds,
        Dictionary<string, int> transferredLiquids,
        List<ExecutionLogEntry> log)
    {
        var slaveJars = new List<Jar>(slave.Jars);

        foreach (var jar in slaveJars)
        {
            if (jar == null) continue;
            var isTransferred = jar.Id.HasValue && transferredJarIds.Contains(jar.Id.Value);
            var isOriginal = jar.Id.HasValue && slaveOriginalJarIds.Contains(jar.Id.Value);

            if (isTransferred && !isOriginal)
            {
                slave.TransferOut(jar);
                // 禁液令不影响满罐转移：装满被主台禁用液体的罐子原样归还，
                // 不在此清空罐内液体（禁令只作用于倒入、游离存储与改形成本）。
                if (!master.TransferIn(jar))
                {
                    log.Add(new ExecutionLogEntry
                    {
                        Phase = "rollback",
                        Command = "discard_jar",
                        Jar = jar.GetDescription(),
                        Reason = "主台罐子容量满，丢弃（仓库单向流动，不回收）"
                    });
                }
            }
        }

        foreach (var kv in transferredLiquids)
        {
            var liquid = kv.Key;
            var amount = Math.Min(kv.Value, slave.LiquidStorage.GetValueOrDefault(liquid, 0));
            if (amount > 0)
            {
                slave.RemoveLiquid(liquid, amount);
                // 部分填充：主台可接收多少就接收多少，剩余丢弃（仓库单向流动，不回收）
                int accepted = master.TryReceiveLiquid(liquid, amount);
                int remainder = amount - accepted;
                if (remainder > 0)
                {
                    log.Add(new ExecutionLogEntry
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

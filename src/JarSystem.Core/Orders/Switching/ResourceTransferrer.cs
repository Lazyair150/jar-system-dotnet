using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>
/// 切换事件的资源转移（原 <c>WorkshopSwitchEvent.TransferAll</c>）：
/// 罐子按「新主台价值」升序转移（容量不足时丢弃低价值），液体按新主台禁令/容量转移或丢弃，全程记录日志。
///
/// 语义（不可改动）：旧台资源一律先移出（`TransferOut`/`DrainLiquid`），
/// 新台收不下即丢弃并记 `discard_*`；「仓库单向流动」，丢弃的资源不回流中央仓库。
/// </summary>
internal sealed class ResourceTransferrer
{
    private readonly JarValuePolicy _valuePolicy;

    public ResourceTransferrer(JarValuePolicy valuePolicy)
    {
        _valuePolicy = valuePolicy;
    }

    public void TransferAll(Workshop oldMaster, Workshop newMaster, SwitchOperationLog log, string caseType)
    {
        // 罐子按「新主台价值」排序转移：容量不足时优先保留高价值罐子（丢弃低价值）。
        var jars = oldMaster.Jars.OrderBy(_valuePolicy.ValueOf).ToList();
        foreach (var jar in jars)
        {
            oldMaster.TransferOut(jar);
            jar.Unmark();
            if (newMaster.HasJarCapacity())
            {
                newMaster.AddJar(jar);
                log.Add("transfer_jar", $"转移 {jar.GetDescription()} → 新主台");
            }
            else
            {
                log.Add("discard_jar", $"丢弃 {jar.GetDescription()} (新主台容量已满)");
            }
        }

        // Transfer liquids
        foreach (var l in Constants.LIQUIDS)
        {
            var amt = oldMaster.LiquidStorage.GetValueOrDefault(l, 0);
            if (amt > 0)
            {
                oldMaster.DrainLiquid(l);
                if (!newMaster.IsBanned(l))
                {
                    if (newMaster.AddLiquid(l, amt))
                        log.Add("transfer_liquid", $"转移 {l}×{amt} → 新主台");
                    else
                        log.Add("discard_liquid", $"丢弃 {l}×{amt} (新主台容量已满)");
                }
                else
                {
                    log.Add("discard_liquid", $"丢弃 {l}×{amt} (新主台禁{l})");
                }
            }
        }

        log.Add("complete", $"切换完成: {oldMaster.Name} → {newMaster.Name} [{caseType}]");
    }
}

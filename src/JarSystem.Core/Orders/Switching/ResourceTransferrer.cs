using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;

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

    private readonly JarPool? _pool;
    private readonly Action<int>? _countDiscardedJars;
    private readonly Action<string, int>? _countDiscardedLiquids;

    // 资源账本（只读投影）：转移记 transfer、收不下的记 discard，让"主台切换"这段
    // 资源移动也有唯一原因可指，而不是只留一条操作日志。null 表示调用方不要溯源（测试/旧路径）。
    private readonly ResourceLedger? _ledger;
    private readonly Func<int>? _tickOf;

    // 转移阶段只暂存外部副作用；只有整个迁徙提交后才真正写账、计数或归还对象池。
    private readonly List<Jar> _jarsToReturn = new();
    private readonly List<PendingLedgerRecord> _ledgerRecords = new();
    private readonly Dictionary<string, int> _discardedLiquidCounts = new();
    private int _discardedJarCount;
    private bool _effectsCommitted;

    public ResourceTransferrer(JarValuePolicy valuePolicy, JarPool? pool = null,
        Action<int>? countDiscardedJars = null,
        Action<string, int>? countDiscardedLiquids = null,
        ResourceLedger? ledger = null, Func<int>? tickOf = null)
    {
        _valuePolicy = valuePolicy;
        _pool = pool;
        _countDiscardedJars = countDiscardedJars;
        _countDiscardedLiquids = countDiscardedLiquids;
        _ledger = ledger;
        _tickOf = tickOf;
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
                Record(ResourceCauses.Transfer, ResourceKinds.Jar, jar.Shape, 1,
                    oldMaster.Name, newMaster.Name);
                log.Add("transfer_jar", $"转移 {jar.GetDescription()} → 新主台");
            }
            else
            {
                _discardedJarCount++;
                Record(ResourceCauses.Discard, ResourceKinds.Jar, jar.Shape, 1,
                    oldMaster.Name, to: null, reason: "新主台容量已满");
                log.Add("discard_jar", $"丢弃 {jar.GetDescription()} (新主台容量已满)");
                // 只暂存回收请求。迁徙提交前不能 Return，否则取消回滚时快照中的原对象已失效。
                _jarsToReturn.Add(jar);
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
                    // 新主台可能只收下一部分（容量是"最多能装多少"，不是"要么全收要么全不收"）：
                    // 收下的记 transfer、收不下的记 discard —— 否则那半份会凭空消失，而账本正是
                    // 为了"每一份资源都指得到原因"而存在的。
                    var had = newMaster.LiquidStorage.GetValueOrDefault(l, 0);
                    newMaster.AddLiquid(l, amt);
                    var accepted = newMaster.LiquidStorage.GetValueOrDefault(l, 0) - had;
                    var lost = amt - accepted;

                    if (accepted > 0)
                    {
                        Record(ResourceCauses.Transfer, ResourceKinds.Liquid, l, accepted,
                            oldMaster.Name, newMaster.Name);
                        log.Add("transfer_liquid", $"转移 {l}×{accepted} → 新主台");
                    }
                    if (lost > 0)
                    {
                        AddDiscardedLiquid(l, lost);
                        Record(ResourceCauses.Discard, ResourceKinds.Liquid, l, lost,
                            oldMaster.Name, to: null, reason: "新主台容量已满");
                        log.Add("discard_liquid", $"丢弃 {l}×{lost} (新主台容量已满)");
                    }
                }
                else
                {
                    AddDiscardedLiquid(l, amt);
                    Record(ResourceCauses.Discard, ResourceKinds.Liquid, l, amt,
                        oldMaster.Name, to: null, reason: "新主台禁" + l);
                    log.Add("discard_liquid", $"丢弃 {l}×{amt} (新主台禁{l})");
                }
            }
        }

        log.Add("complete", $"切换完成: {oldMaster.Name} → {newMaster.Name} [{caseType}]");
    }

    private void AddDiscardedLiquid(string liquid, int amount)
    {
        _discardedLiquidCounts[liquid] = _discardedLiquidCounts.GetValueOrDefault(liquid) + amount;
    }

    private void Record(string cause, string kind, string name, int amount,
        string? from, string? to = null, string? reason = null)
    {
        if (amount > 0)
            _ledgerRecords.Add(new PendingLedgerRecord(cause, kind, name, amount, from, to, reason));
    }

    /// <summary>
    /// 迁徙与岗位提交成功后调用。对象池归还放在所有账本/统计写入之后。
    /// 注入的 tick 来源、统计回调及对象池依赖必须遵守非抛异常契约；任意外部副作用都无法由工作台快照自动撤销。
    /// 调用失败可能意味着前面的账本/统计已经生效，因此上层必须禁止回滚和自动重试。
    /// </summary>
    public void CommitEffects()
    {
        if (_effectsCommitted) return;

        foreach (var entry in _ledgerRecords)
            _ledger?.Record(_tickOf?.Invoke() ?? 0, entry.Cause, entry.Kind, entry.Name,
                entry.Amount, entry.From, entry.To, entry.Reason);

        if (_discardedJarCount > 0)
            _countDiscardedJars?.Invoke(_discardedJarCount);

        foreach (var entry in _discardedLiquidCounts)
            _countDiscardedLiquids?.Invoke(entry.Key, entry.Value);

        // 必须最后归还：归还后原对象会变成不可读写的回收对象。
        foreach (var jar in _jarsToReturn)
            _pool?.Return(jar);

        _effectsCommitted = true;
    }

    /// <summary>取消尚未提交的迁徙时丢弃暂存副作用。</summary>
    public void DiscardPendingEffects()
    {
        if (_effectsCommitted) return;
        _jarsToReturn.Clear();
        _ledgerRecords.Clear();
        _discardedLiquidCounts.Clear();
        _discardedJarCount = 0;
    }

    private sealed record PendingLedgerRecord(
        string Cause, string Kind, string Name, int Amount,
        string? From, string? To, string? Reason);
}

using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>
/// 工作台切换事件 - 在切换主台时最大化资源传递效率。
///
/// 阶段 2 拆分后的职责仅剩**编排**：快照 → 选择分支（禁令相同/不同）→ 执行分支 → 转移全部剩余资源 → 异常回滚。
/// 协作者：<see cref="ISwitchCase"/>（Case1/Case2 分支策略）、<see cref="ResourceTransferrer"/>（罐子/液体转移与丢弃）、
/// <see cref="JarValuePolicy"/>（罐子对新主台的价值排序）、<see cref="SwitchOperationLog"/>（操作日志）。
/// </summary>
public class WorkshopSwitchEvent
{
    private static readonly ISwitchCase SameBansCaseInstance = new SameBansCase();
    private static readonly ISwitchCase DifferentBansCaseInstance = new DifferentBansCase();

    private readonly SwitchOperationLog _log = new();

    public Workshop OldMaster { get; }
    public Workshop NewMaster { get; }

    /// <summary>本次切换的分支类型（Constants.SwitchCaseType 取值：same_bans / different_bans）。</summary>
    public string CaseType { get; private set; } = "";

    /// <summary>本次切换的操作日志（只读视图；对外事件历史与前端动画的数据源）。</summary>
    public IReadOnlyList<SwitchLogEntry> Operations => _log.Entries;

    public WorkshopSwitchEvent(Workshop oldMaster, Workshop newMaster)
    {
        OldMaster = oldMaster;
        NewMaster = newMaster;
    }

    /// <summary>执行切换事件，返回是否成功；失败时两个工作台已回滚到执行前状态。</summary>
    public (bool Success, string? Error) Execute()
    {
        _log.Clear();
        var oldSnap = OldMaster.CreateSnapshot();
        var newSnap = NewMaster.CreateSnapshot();

        try
        {
            var switchCase = ResolveCase(OldMaster, NewMaster);
            CaseType = switchCase.CaseType;

            switchCase.Execute(OldMaster, NewMaster, _log);

            // Transfer all remaining resources from old to new master
            new ResourceTransferrer(new JarValuePolicy(NewMaster))
                .TransferAll(OldMaster, NewMaster, _log, CaseType);

            return (true, null);
        }
        catch (Exception ex)
        {
            // 快照回滚：任何异常都恢复两个工作台，保证切换事件原子性
            OldMaster.RestoreFromSnapshot(oldSnap);
            NewMaster.RestoreFromSnapshot(newSnap);
            _log.Add("error", "切换事件失败，已回滚: " + ex.Message);
            return (false, "切换事件失败: " + ex.Message);
        }
    }

    /// <summary>按禁令是否相同选择分支策略（与重构前的 sameBans 判定完全一致）。</summary>
    private static ISwitchCase ResolveCase(Workshop oldMaster, Workshop newMaster) =>
        BanComparison.Same(oldMaster, newMaster) ? SameBansCaseInstance : DifferentBansCaseInstance;
}

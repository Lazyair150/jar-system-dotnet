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

    private readonly JarPool? _pool;

    public WorkshopSwitchEvent(Workshop oldMaster, Workshop newMaster, JarPool? pool = null)
    {
        OldMaster = oldMaster;
        NewMaster = newMaster;
        _pool = pool;
    }

    private WorkshopSnapshot? _oldSnap;
    private WorkshopSnapshot? _newSnap;
    private ISwitchCase? _case;

    /// <summary>
    /// 执行切换事件，返回是否成功；失败时两个工作台已回滚到执行前状态。
    ///
    /// 实现上是 <see cref="Begin"/> → <see cref="RunReshape"/> → <see cref="RunTransfer"/> 的组合，
    /// **语义与拆分前逐字一致**（三步在同一个调用里跑完，异常同样整体回滚）。
    /// 拆开是为了让「迁徙过程」能按 tick 一段一段地做（见 <c>Process.Execution.SwitchProcessAdvancer</c>），
    /// 而不必复制一份搬运逻辑 —— 一份定义，两条轨。
    /// </summary>
    public (bool Success, string? Error) Execute()
    {
        Begin();
        var (ok, err) = RunReshape();
        if (!ok) return (false, err);
        return RunTransfer();
    }

    /// <summary>第一阶段：清理日志、拍快照、按禁令是否相同选分支。不做任何搬动。</summary>
    public void Begin()
    {
        _log.Clear();
        _oldSnap = OldMaster.CreateSnapshot();
        _newSnap = NewMaster.CreateSnapshot();
        _case = ResolveCase(OldMaster, NewMaster);
        CaseType = _case.CaseType;
    }

    /// <summary>第二阶段：按新主台的标记与禁令重组旧主台的罐液。失败则两台回滚。</summary>
    public (bool Ok, string? Error) RunReshape()
    {
        if (_case == null) Begin();
        try
        {
            _case!.Execute(OldMaster, NewMaster, _log);
            return (true, null);
        }
        catch (Exception ex)
        {
            return Rollback(ex);
        }
    }

    /// <summary>第三阶段：把剩余资源整体搬到新主台（按 <see cref="JarValuePolicy"/> 排序）。失败则两台回滚。</summary>
    public (bool Ok, string? Error) RunTransfer()
    {
        if (_case == null) Begin();
        try
        {
            new ResourceTransferrer(new JarValuePolicy(NewMaster), _pool)
                .TransferAll(OldMaster, NewMaster, _log, CaseType);
            return (true, null);
        }
        catch (Exception ex)
        {
            return Rollback(ex);
        }
    }

    /// <summary>
    /// **把两台恢复到 <see cref="Begin"/> 拍的快照**（幂等：没有快照时什么都不做）。
    ///
    /// 谁需要它：迁徙被**取消**时。取消走的是 `ProcessSystem.Cancel` → `MarkFailed` → `OnFinished` → `run.Abort`，
    /// 而 `RunReshape` 可能已经把旧台的罐子改过形了。异常路径本来就有回滚，取消路径原先没有 ——
    /// 于是"迁徙做了一半被取消"会留下一个改了一半的工作台。这里补上同一条退路。
    /// </summary>
    public void Rollback()
    {
        if (_oldSnap != null) OldMaster.RestoreFromSnapshot(_oldSnap);
        if (_newSnap != null) NewMaster.RestoreFromSnapshot(_newSnap);
    }

    private (bool Ok, string? Error) Rollback(Exception ex)
    {
        if (_oldSnap != null) OldMaster.RestoreFromSnapshot(_oldSnap);
        if (_newSnap != null) NewMaster.RestoreFromSnapshot(_newSnap);
        _log.Add("error", "切换事件失败，已回滚: " + ex.Message);
        return (false, "切换事件失败: " + ex.Message);
    }

    /// <summary>按禁令是否相同选择分支策略（与重构前的 sameBans 判定完全一致）。</summary>
    private static ISwitchCase ResolveCase(Workshop oldMaster, Workshop newMaster) =>
        BanComparison.Same(oldMaster, newMaster) ? SameBansCaseInstance : DifferentBansCaseInstance;
}

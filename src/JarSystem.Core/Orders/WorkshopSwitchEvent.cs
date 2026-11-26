using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;

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
    private enum Lifecycle
    {
        Created,
        Prepared,
        Reshaped,
        Transferred,
        Committing,
        Committed,
        CommitFailed,
        RolledBack
    }

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
    private readonly Action<int>? _countDiscardedJars;
    private readonly Action<string, int>? _countDiscardedLiquids;
    private readonly ResourceLedger? _ledger;
    private readonly Func<int>? _tickOf;

    public WorkshopSwitchEvent(Workshop oldMaster, Workshop newMaster, JarPool? pool = null,
        Action<int>? countDiscardedJars = null,
        Action<string, int>? countDiscardedLiquids = null,
        ResourceLedger? ledger = null, Func<int>? tickOf = null)
    {
        OldMaster = oldMaster ?? throw new ArgumentNullException(nameof(oldMaster));
        NewMaster = newMaster ?? throw new ArgumentNullException(nameof(newMaster));
        _pool = pool;
        _countDiscardedJars = countDiscardedJars;
        _countDiscardedLiquids = countDiscardedLiquids;
        _ledger = ledger;
        _tickOf = tickOf;
    }

    private WorkshopSnapshot? _oldSnap;
    private WorkshopSnapshot? _newSnap;
    private ISwitchCase? _case;
    private ResourceTransferrer? _pendingTransferrer;
    private Lifecycle _lifecycle = Lifecycle.Created;

    /// <summary>
    /// 执行切换事件，返回是否成功；失败时两个工作台已回滚到执行前状态。
    /// </summary>
    public (bool Success, string? Error) Execute()
    {
        Begin();
        var (ok, err) = RunReshape();
        if (!ok) return (false, err);
        var transfer = RunTransfer();
        if (!transfer.Ok) return (false, transfer.Error);
        CommitTransferEffects();
        return (true, null);
    }

    /// <summary>
    /// 第一阶段：拍摄唯一一份迁徙前快照并选择分支。
    /// 每个事件实例只能 Begin 一次；重复调用会抛异常，避免覆盖事务起点。
    /// </summary>
    public void Begin()
    {
        EnsureLifecycle(Lifecycle.Created, "迁徙事件只能初始化一次");

        // 先在局部变量中完成准备，避免快照创建中途失败时留下半初始化状态。
        var oldSnapshot = OldMaster.CreateSnapshot();
        var newSnapshot = NewMaster.CreateSnapshot();
        var migrationCase = ResolveCase(OldMaster, NewMaster);

        _oldSnap = oldSnapshot;
        _newSnap = newSnapshot;
        _case = migrationCase;
        CaseType = migrationCase.CaseType;
        _log.Clear();
        _lifecycle = Lifecycle.Prepared;
    }

    /// <summary>第二阶段：按新主台的标记与禁令重组旧主台的罐液。失败则两台回滚。</summary>
    public (bool Ok, string? Error) RunReshape()
    {
        // 兼容旧版过程路径：首次直接调用 RunReshape 时自动完成 Begin。
        if (_lifecycle == Lifecycle.Created)
            Begin();
        EnsureLifecycle(Lifecycle.Prepared, "只有完成 Begin 的迁徙事件才能执行重组");

        try
        {
            _case!.Execute(OldMaster, NewMaster, _log);
            _lifecycle = Lifecycle.Reshaped;
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
        EnsureLifecycle(Lifecycle.Reshaped, "必须先成功完成重组，才能执行资源转移");

        try
        {
            var transferrer = new ResourceTransferrer(new JarValuePolicy(NewMaster), _pool,
                _countDiscardedJars, _countDiscardedLiquids, _ledger, _tickOf);
            transferrer.TransferAll(OldMaster, NewMaster, _log, CaseType);
            _pendingTransferrer = transferrer;
            _lifecycle = Lifecycle.Transferred;
            return (true, null);
        }
        catch (Exception ex)
        {
            return Rollback(ex);
        }
    }

    /// <summary>
    /// 提交转移阶段暂存的账本、统计与对象池副作用。岗位提交成功后调用。
    /// 一旦开始提交就禁止回滚；提交失败后也不自动重试，以免重复执行部分外部副作用。
    /// </summary>
    public void CommitTransferEffects()
    {
        if (_lifecycle == Lifecycle.Committed)
            return;
        EnsureLifecycle(Lifecycle.Transferred, "只有完成资源转移的迁徙事件才能提交副作用");

        _lifecycle = Lifecycle.Committing;
        var pending = _pendingTransferrer;
        _pendingTransferrer = null;
        try
        {
            pending?.CommitEffects();
            _lifecycle = Lifecycle.Committed;
        }
        catch
        {
            _lifecycle = Lifecycle.CommitFailed;
            throw;
        }
    }

    /// <summary>
    /// 把两台恢复到 Begin 拍摄的快照。回滚可重复调用；提交开始后禁止回滚。
    /// </summary>
    public void Rollback()
    {
        if (_lifecycle == Lifecycle.RolledBack)
            return;
        if (_lifecycle is Lifecycle.Committing or Lifecycle.Committed or Lifecycle.CommitFailed)
            throw new InvalidOperationException("迁徙副作用提交已经开始，不能再回滚工作台快照");

        RestoreSnapshotAndDiscardEffects();
        _lifecycle = Lifecycle.RolledBack;
    }

    private (bool Ok, string? Error) Rollback(Exception ex)
    {
        if (_lifecycle is Lifecycle.Committing or Lifecycle.Committed or Lifecycle.CommitFailed)
            return (false, "迁徙副作用提交已经开始，不能再回滚工作台快照: " + ex.Message);

        RestoreSnapshotAndDiscardEffects();
        _lifecycle = Lifecycle.RolledBack;
        _log.Add("error", "切换事件失败，已回滚: " + ex.Message);
        return (false, "切换事件失败: " + ex.Message);
    }

    private void RestoreSnapshotAndDiscardEffects()
    {
        _pendingTransferrer?.DiscardPendingEffects();
        _pendingTransferrer = null;
        if (_oldSnap != null) OldMaster.RestoreFromSnapshot(_oldSnap);
        if (_newSnap != null) NewMaster.RestoreFromSnapshot(_newSnap);
    }

    private void EnsureLifecycle(Lifecycle expected, string message)
    {
        if (_lifecycle != expected)
            throw new InvalidOperationException(message + $"（当前阶段：{_lifecycle}）");
    }

    /// <summary>按禁令是否相同选择分支策略（与重构前的 sameBans 判定完全一致）。</summary>
    private static ISwitchCase ResolveCase(Workshop oldMaster, Workshop newMaster) =>
        BanComparison.Same(oldMaster, newMaster) ? SameBansCaseInstance : DifferentBansCaseInstance;
}

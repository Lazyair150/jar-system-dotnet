using System;
using System.Linq;
using Godot;
using JarSystem.Core.World;

namespace JarWorldV2;

/// <summary>
/// 纯表现层：接收 Host 节点的状态变化通知，再主动读取 DTO 快照。
/// 不直接访问 WorldV2Context、HostService 或 Core 领域实体。
/// </summary>
public partial class Main : Control
{
    private const long ReplacementPhaseDurationLogicalMilli = 1_000;

    private WorldV2HostNode _host = null!;
    private Label _statusLabel = null!;
    private RichTextLabel _snapshotLabel = null!;
    private Label _replacementStatusLabel = null!;
    private OptionButton _masterTargetOption = null!;
    private OptionButton _slaveTargetOption = null!;
    private Button _masterReplacementButton = null!;
    private Button _slaveReplacementButton = null!;
    private Button _cancelReplacementButton = null!;
    private Button _restoreCheckpointButton = null!;
    private string? _checkpointJson;
    private string[] _knownMasterTargets = Array.Empty<string>();
    private string[] _knownSlaveTargets = Array.Empty<string>();

    public override void _Ready()
    {
        _host = GetNode<WorldV2HostNode>("WorldV2Host");
        _statusLabel = GetNode<Label>("Root/StatusLabel");
        _snapshotLabel = GetNode<RichTextLabel>("Root/SnapshotLabel");
        _replacementStatusLabel = GetNode<Label>("Root/ReplacementStatusLabel");
        _masterTargetOption = GetNode<OptionButton>("Root/MasterReplacementControls/MasterTargetOption");
        _slaveTargetOption = GetNode<OptionButton>("Root/SlaveReplacementControls/SlaveTargetOption");
        _masterReplacementButton = GetNode<Button>("Root/MasterReplacementControls/ReplaceMasterButton");
        _slaveReplacementButton = GetNode<Button>("Root/SlaveReplacementControls/ReplaceSlaveButton");
        _cancelReplacementButton = GetNode<Button>("Root/ReplacementActions/CancelReplacementButton");
        _restoreCheckpointButton = GetNode<Button>("Root/Controls/RestoreCheckpointButton");

        _host.SnapshotChanged += Refresh;
        GetNode<Button>("Root/Controls/PauseButton").Pressed += _host.PauseWorld;
        GetNode<Button>("Root/Controls/ResumeButton").Pressed += _host.ResumeWorld;
        GetNode<Button>("Root/Controls/NormalSpeedButton").Pressed += () => _host.SetWorldSpeedRatio(1.0);
        GetNode<Button>("Root/Controls/DoubleSpeedButton").Pressed += () => _host.SetWorldSpeedRatio(2.0);
        GetNode<Button>("Root/Controls/SaveCheckpointButton").Pressed += SaveCheckpoint;
        _restoreCheckpointButton.Pressed += RestoreCheckpoint;
        _masterReplacementButton.Pressed += () => BeginReplacement(WorkshopRole.Master, _masterTargetOption);
        _slaveReplacementButton.Pressed += () => BeginReplacement(WorkshopRole.Slave, _slaveTargetOption);
        _cancelReplacementButton.Pressed += CancelReplacement;
        _restoreCheckpointButton.Disabled = true;

        Refresh();
    }

    public override void _ExitTree()
    {
        if (_host != null)
            _host.SnapshotChanged -= Refresh;
    }

    private void SaveCheckpoint()
    {
        _checkpointJson = _host.CaptureExecutionCheckpointJson();
        _restoreCheckpointButton.Disabled = false;
        Refresh();
        _statusLabel.Text = "检查点已保存到当前运行内存，可继续推进后恢复";
    }

    private void RestoreCheckpoint()
    {
        if (string.IsNullOrWhiteSpace(_checkpointJson))
            return;

        _host.RestoreExecutionCheckpointJson(_checkpointJson);
        Refresh();
    }

    private void BeginReplacement(WorkshopRole role, OptionButton targetOption)
    {
        if (targetOption.Selected < 0)
        {
            _replacementStatusLabel.Text = "没有可用的备用工作台个体";
            return;
        }

        var targetName = targetOption.GetItemText(targetOption.Selected);
        var result = _host.BeginWorkshopReplacement(
            role, targetName, ReplacementPhaseDurationLogicalMilli);
        _replacementStatusLabel.Text = result.Success
            ? $"已开始替换：{result.Replacement?.OldWorkshopName} → {result.Replacement?.TargetWorkshopName}"
            : "替换未启动：" + (result.Error ?? "Core 未提供原因");
    }

    private void CancelReplacement()
    {
        var result = _host.CancelWorkshopReplacement();
        _replacementStatusLabel.Text = result.Success
            ? "替换已取消；已提交的订单中断不会撤销"
            : "取消未完成：" + (result.Error ?? "Core 未提供原因");
    }

    private static string? SelectedName(OptionButton option)
    {
        return option.Selected >= 0 ? option.GetItemText(option.Selected) : null;
    }

    private static void ReplaceOptions(OptionButton option, string[] names, string? preferred)
    {
        option.Clear();
        foreach (var name in names)
            option.AddItem(name);

        if (names.Length == 0)
            return;

        var selectedIndex = Array.IndexOf(names, preferred ?? string.Empty);
        option.Select(selectedIndex >= 0 ? selectedIndex : 0);
    }

    private void RefreshReplacementControls(WorldV2SnapshotDto snapshot)
    {
        var masterTargets = snapshot.SpareWorkshops.Select(item => item.Name).ToArray();
        var slaveTargets = snapshot.SpareWorkshops
            .Where(item => !item.HasResources)
            .Select(item => item.Name)
            .ToArray();

        if (!_knownMasterTargets.SequenceEqual(masterTargets, StringComparer.Ordinal))
        {
            var preferred = SelectedName(_masterTargetOption);
            ReplaceOptions(_masterTargetOption, masterTargets, preferred);
            _knownMasterTargets = masterTargets;
        }

        if (!_knownSlaveTargets.SequenceEqual(slaveTargets, StringComparer.Ordinal))
        {
            var preferred = SelectedName(_slaveTargetOption);
            ReplaceOptions(_slaveTargetOption, slaveTargets, preferred);
            _knownSlaveTargets = slaveTargets;
        }

        var replacement = snapshot.Replacement;
        var active = replacement is { IsFinished: false };
        var blocked = active || snapshot.RuntimeFaulted;
        _masterReplacementButton.Disabled = blocked || masterTargets.Length == 0;
        _slaveReplacementButton.Disabled = blocked || slaveTargets.Length == 0;
        _cancelReplacementButton.Disabled = !active || snapshot.RuntimeFaulted;

        if (replacement == null)
        {
            _replacementStatusLabel.Text = "当前没有工作台替换过程；每个阶段持续 1 个逻辑时间单位";
        }
        else if (active)
        {
            _replacementStatusLabel.Text =
                $"替换进行中：{replacement.Role} {replacement.OldWorkshopName} → {replacement.TargetWorkshopName}，" +
                $"阶段 {replacement.CompletedPhases + 1}，" +
                $"本阶段 {replacement.PhaseElapsedLogicalMilli}/{replacement.PhaseDurationLogicalMilli} ms";
        }
        else
        {
            _replacementStatusLabel.Text = replacement.Succeeded
                ? $"替换完成：{replacement.TargetWorkshopName} 已接任 {replacement.Role}"
                : "替换结束但未成功：" + (replacement.FailureReason ?? "未提供原因");
            if (!string.IsNullOrWhiteSpace(replacement.CommitWarning))
                _replacementStatusLabel.Text += "；警告：" + replacement.CommitWarning;
        }
    }

    private void Refresh()
    {
        var snapshot = _host.Snapshot;
        if (snapshot == null)
        {
            _statusLabel.Text = "世界尚未创建";
            _snapshotLabel.Text = "";
            return;
        }

        _statusLabel.Text = snapshot.RuntimeFaulted
            ? "运行故障：" + (snapshot.FaultReason ?? "未提供原因")
            : snapshot.IsPaused ? "世界已暂停" : "世界运行中";

        RefreshReplacementControls(snapshot);

        _snapshotLabel.Text =
            $"世界时间：{snapshot.WorldTimeMilli} ms\n" +
            $"逻辑时间：{snapshot.LogicalTimeMilli} ms\n" +
            $"流速倍率：{snapshot.SpeedRatioMilli / 1000.0:0.###}x\n" +
            $"主工作台：{snapshot.Master?.Name ?? "-"}\n" +
            $"从工作台：{snapshot.Slave?.Name ?? "-"}\n" +
            $"备用工作台：{string.Join("、", snapshot.SpareWorkshops.Select(item => item.Name))}\n" +
            $"订单数量：{snapshot.Demands.Count}\n" +
            $"执行会话：{snapshot.ActiveSessions.Count}\n" +
            $"预约数量：{snapshot.ReservationCount}\n" +
            $"运行故障：{snapshot.RuntimeFaulted}";
    }
}

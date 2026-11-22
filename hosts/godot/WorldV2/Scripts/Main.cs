using Godot;

namespace JarWorldV2;

/// <summary>
/// 纯表现层：接收 Host 节点的状态变化通知，再主动读取 DTO 快照。
/// 不直接访问 WorldV2Context、HostService 或 Core 领域实体。
/// </summary>
public partial class Main : Control
{
    private WorldV2HostNode _host = null!;
    private Label _statusLabel = null!;
    private RichTextLabel _snapshotLabel = null!;
    private Button _restoreCheckpointButton = null!;
    private string? _checkpointJson;

    public override void _Ready()
    {
        _host = GetNode<WorldV2HostNode>("WorldV2Host");
        _statusLabel = GetNode<Label>("Root/StatusLabel");
        _snapshotLabel = GetNode<RichTextLabel>("Root/SnapshotLabel");
        _restoreCheckpointButton = GetNode<Button>("Root/Controls/RestoreCheckpointButton");

        _host.SnapshotChanged += Refresh;
        GetNode<Button>("Root/Controls/PauseButton").Pressed += _host.PauseWorld;
        GetNode<Button>("Root/Controls/ResumeButton").Pressed += _host.ResumeWorld;
        GetNode<Button>("Root/Controls/NormalSpeedButton").Pressed += () => _host.SetWorldSpeedRatio(1.0);
        GetNode<Button>("Root/Controls/DoubleSpeedButton").Pressed += () => _host.SetWorldSpeedRatio(2.0);
        GetNode<Button>("Root/Controls/SaveCheckpointButton").Pressed += SaveCheckpoint;
        _restoreCheckpointButton.Pressed += RestoreCheckpoint;
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

        _snapshotLabel.Text =
            $"世界时间：{snapshot.WorldTimeMilli} ms\n" +
            $"逻辑时间：{snapshot.LogicalTimeMilli} ms\n" +
            $"流速倍率：{snapshot.SpeedRatioMilli / 1000.0:0.###}x\n" +
            $"主工作台：{snapshot.Master?.Name ?? "-"}\n" +
            $"从工作台：{snapshot.Slave?.Name ?? "-"}\n" +
            $"订单数量：{snapshot.Demands.Count}\n" +
            $"执行会话：{snapshot.ActiveSessions.Count}\n" +
            $"预约数量：{snapshot.ReservationCount}\n" +
            $"运行故障：{snapshot.RuntimeFaulted}";
    }
}

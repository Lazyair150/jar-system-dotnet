using System;
using Godot;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using JarSystem.WorldV2Bridge;

namespace JarWorldV2;

/// <summary>
/// Godot 侧的宿主适配节点。
///
/// 职责仅限：
/// 1. 创建并持有 Bridge 会话；
/// 2. 把 Godot 的墙钟 delta 转成世界时间毫秒；
/// 3. 将用户的暂停/倍速操作转交给 Bridge；
/// 4. 在状态变化后通知 UI 重新读取 DTO 快照。
///
/// 不在这里实现生产、资源流动、订单调度、冷却或工作台规则。
/// </summary>
public partial class WorldV2HostNode : Node
{
    private WorldV2BridgeSession? _session;
    private double _worldTimeRemainderMilli;

    /// <summary>仅通知状态变化；不把 DTO 或 Core 对象作为 Signal 参数。</summary>
    public event Action? SnapshotChanged;

    public WorldV2SnapshotDto? Snapshot { get; private set; }

    public override void _Ready()
    {
        _session = new WorldV2BridgeSession();
        _session.CreateOrReplaceWorld(CreateDemoOptions());
        RefreshSnapshot();
    }

    public override void _Process(double delta)
    {
        if (_session == null)
            return;

        // delta 是 Godot 提供的墙钟秒数。
        // 这里只转换单位，不乘 speed ratio；倍速和暂停由 Core WorldClock 统一处理。
        _worldTimeRemainderMilli += delta * 1_000.0;
        var wholeWorldTimeMilli = (long)Math.Floor(_worldTimeRemainderMilli);
        if (wholeWorldTimeMilli <= 0)
            return;

        _worldTimeRemainderMilli -= wholeWorldTimeMilli;
        _session.AdvanceWorld(wholeWorldTimeMilli);
        RefreshSnapshot();
    }

    public void PauseWorld()
    {
        RequireSession().PauseWorld();
        RefreshSnapshot();
    }

    public void ResumeWorld()
    {
        RequireSession().ResumeWorld();
        RefreshSnapshot();
    }

    public void SetWorldSpeedRatio(double ratio)
    {
        RequireSession().SetWorldSpeedRatio(ratio);
        RefreshSnapshot();
    }

    /// <summary>导出完整活动执行检查点 JSON；返回值可交由宿主文件层保存。</summary>
    public string CaptureExecutionCheckpointJson(bool indented = true) =>
        RequireSession().CaptureExecutionCheckpointJson(indented);

    /// <summary>从版本化 JSON 恢复并原子替换当前世界；配置必须与存档匹配。</summary>
    public void RestoreExecutionCheckpointJson(string json)
    {
        RequireSession().RestoreExecutionCheckpointJson(CreateDemoOptions(), json);
        RefreshSnapshot();
    }

    public override void _ExitTree()
    {
        _session?.Dispose();
        _session = null;
        Snapshot = null;
    }

    private WorldV2BridgeSession RequireSession() =>
        _session ?? throw new InvalidOperationException("World v2 会话尚未初始化");

    private void RefreshSnapshot()
    {
        Snapshot = RequireSession().GetWorldSnapshot();
        SnapshotChanged?.Invoke();
    }

    private static WorldV2Options CreateDemoOptions() => new()
    {
        Shapes = new() { "圆罐", "方罐" },
        Liquids = new() { "酒", "蜜" },
        Producers =
        {
            new WorldV2ProducerOptions
            {
                Name = "陶工",
                Rates = new() { ["圆罐"] = 1.0 }
            }
        },
        LiquidProductionRates = new()
        {
            ["酒"] = 0,
            ["蜜"] = 0
        },
        InitialLiquidStock = new() { ["酒"] = 10 },
        JarFlowRatePerLogicalTime = 1,
        LiquidFlowRatePerLogicalTime = 1,
        Workshops =
        {
            new WorldV2WorkshopOptions
            {
                Name = "主台",
                BaseMarks = new() { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10
            },
            new WorldV2WorkshopOptions
            {
                Name = "从台",
                BaseMarks = new() { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10
            }
        },
        MasterWorkshopName = "主台",
        SlaveWorkshopName = "从台",
        ExecutionLogicalTimePerUnit = 1_000,
        PassiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot,
        ActiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot
    };
}

using System;
using System.Threading;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;

namespace JarSystem.WorldV2Bridge;

/// <summary>
/// Godot 宿主使用的 World v2 会话组合根。
///
/// 本项目不引用 Godot API，因此可以先通过普通 .NET 测试验证端到端链路。
/// 会话拥有 Host；ApplicationService 只负责命令/查询，不拥有 Host。
/// 所有业务规则仍由 JarSystem.Core 的 World v2 实现。
/// </summary>
public sealed class WorldV2BridgeSession : IDisposable
{
    private readonly WorldV2HostService _host;
    private readonly WorldV2ApplicationService _application;
    private int _disposed;

    public WorldV2BridgeSession()
    {
        _host = new WorldV2HostService();
        _application = new WorldV2ApplicationService(_host);
    }

    /// <summary>当前会话是否已创建世界。</summary>
    public bool HasWorld
    {
        get
        {
            ThrowIfDisposed();
            return _host.HasWorld;
        }
    }

    /// <summary>创建或原子替换当前世界。</summary>
    public void CreateOrReplaceWorld(WorldV2Options options)
    {
        ThrowIfDisposed();
        _application.CreateOrReplaceWorld(options);
    }

    /// <summary>推进指定的世界时间毫秒数；逻辑时间换算由 Core 的 WorldClock 完成。</summary>
    public WorldV2AdvanceResultDto AdvanceWorld(long deltaWorldTimeMilli)
    {
        ThrowIfDisposed();
        return _application.AdvanceWorld(deltaWorldTimeMilli);
    }

    public void PauseWorld()
    {
        ThrowIfDisposed();
        _application.PauseWorld();
    }

    public void ResumeWorld()
    {
        ThrowIfDisposed();
        _application.ResumeWorld();
    }

    /// <summary>设置流速倍率；1000 表示 1 倍，0 表示逻辑时间不增长。</summary>
    public void SetWorldSpeedRatio(double ratio)
    {
        ThrowIfDisposed();
        _application.SetWorldSpeedRatio(ratio);
    }

    public int AddJarDemand(WorldV2AddJarDemandRequest request)
    {
        ThrowIfDisposed();
        return _application.AddJarDemand(request);
    }

    public bool MoveDemand(int demandId, DemandAreaKind targetArea)
    {
        ThrowIfDisposed();
        return _application.MoveDemand(demandId, targetArea);
    }

    public WorldV2StartDemandResultDto StartActiveDemand(int demandId)
    {
        ThrowIfDisposed();
        return _application.StartActiveDemand(demandId);
    }

    public WorldV2WorkshopReplacementResultDto BeginWorkshopReplacement(
        WorkshopRole role,
        string targetWorkshopName,
        long phaseDurationLogicalMilli)
    {
        ThrowIfDisposed();
        return _application.BeginWorkshopReplacement(
            role, targetWorkshopName, phaseDurationLogicalMilli);
    }

    public WorldV2WorkshopReplacementResultDto CancelWorkshopReplacement(
        string reason = "工作台替换已取消")
    {
        ThrowIfDisposed();
        return _application.CancelWorkshopReplacement(reason);
    }

    /// <summary>返回 ApplicationService 创建的脱离式展示快照。</summary>
    public WorldV2SnapshotDto GetWorldSnapshot()
    {
        ThrowIfDisposed();
        return _application.GetWorldSnapshot();
    }

    /// <summary>捕获当前 World v2 执行检查点（纯托管数据，不包含 Godot 对象引用）。</summary>
    public WorldV2ExecutionResourceCheckpoint CaptureExecutionCheckpoint()
    {
        ThrowIfDisposed();
        return _application.CaptureExecutionResourceCheckpoint();
    }

    /// <summary>将执行检查点恢复到已创建的兼容世界（旧兼容入口）。</summary>
    public void RestoreExecutionCheckpoint(WorldV2ExecutionResourceCheckpoint checkpoint)
    {
        ThrowIfDisposed();
        _application.RestoreExecutionResourceCheckpoint(checkpoint);
    }

    /// <summary>以新配置准备并原子替换 Host 当前世界。</summary>
    public void RestoreExecutionCheckpoint(
        WorldV2Options options,
        WorldV2ExecutionResourceCheckpoint checkpoint)
    {
        ThrowIfDisposed();
        _application.RestoreExecutionResourceCheckpoint(options, checkpoint);
    }

    /// <summary>捕获完整根级检查点并编码为版本化 JSON。</summary>
    public string CaptureExecutionCheckpointJson(bool indented = false)
    {
        ThrowIfDisposed();
        return WorldV2CheckpointJsonCodec.EncodeExecutionResourceCheckpoint(
            _application.CaptureExecutionResourceCheckpoint(), indented);
    }

    /// <summary>解码根级 JSON 检查点并通过新 Context 原子替换当前世界。</summary>
    public void RestoreExecutionCheckpointJson(WorldV2Options options, string json)
    {
        ThrowIfDisposed();
        _application.RestoreExecutionResourceCheckpointJson(options, json);
    }

    /// <summary>
    /// 释放会话拥有的 Host 与当前 World Context。
    /// Host 会等待正在执行的独占操作完成；重复释放安全。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _host.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WorldV2BridgeSession));
    }
}

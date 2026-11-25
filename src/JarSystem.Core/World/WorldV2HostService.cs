using System;
using System.Linq;
using System.Threading;
using JarSystem.Core.Domain;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 世界实例的生命周期所有者与宿主操作边界。
///
/// 外部只能在 ExecuteExclusive 回调期间访问当前 Context；
/// Replace 与 Dispose 使用同一个操作门，因此不会与世界操作重叠。
/// 回调必须同步完成，不能把 Context 或其可变子对象保存到回调之外。
///
/// 本类型不推进逻辑时间、不启动后台线程，也不决定宿主的墙钟节奏。
/// </summary>
public sealed class WorldV2HostService : IDisposable
{
    private readonly object _sync = new();
    // 世界操作、替换和关闭共用一个串行门。
    private readonly object _operationSync = new();
    private WorldV2Context? _current;
    private bool _disposed;

    /// <summary>是否已经创建当前世界。</summary>
    public bool HasWorld
    {
        get
        {
            lock (_sync) return !_disposed && _current != null;
        }
    }

    /// <summary>
    /// 首次创建或原子替换当前世界。
    /// 新世界完整创建成功后才发布；配置校验或装配失败时，当前世界保持不变。
    /// 替换与正在执行的 ExecuteExclusive 回调串行，不会提前释放正在使用的 Context。
    /// </summary>
    public void Replace(WorldV2Options options)
    {
        var worldInstanceId = CreateWorldInstanceId();
        var ownerIdentities = EnsureOwnerIdentityMap(options, worldInstanceId);
        Replace(options, worldInstanceId, ownerIdentities);
    }

    internal static WorldInstanceId CreateWorldInstanceId() =>
        new(Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Assigns opaque local instance IDs at the host composition boundary when a legacy
    /// definition omits them, then builds one exact immutable owner map for this World.
    /// Existing IDs (including Board-assigned Workshop IDs) are preserved verbatim.
    /// </summary>
    internal static WorldV2OwnerIdentityMap EnsureOwnerIdentityMap(
        WorldV2Options options,
        WorldInstanceId worldInstanceId)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!worldInstanceId.IsValid)
            throw new ArgumentException("WorldInstanceId 无效。", nameof(worldInstanceId));
        if (options.Producers == null || options.Workshops == null)
            throw new ArgumentException("World V2 Producers / Workshops 不能为 null。", nameof(options));

        foreach (var producer in options.Producers)
            if (producer.ProducerInstanceId == null)
                producer.ProducerInstanceId = "producer-" + Guid.NewGuid().ToString("N");

        var assignments = options.BoardAssignmentSnapshot?.Assignments;
        foreach (var workshop in options.Workshops)
        {
            if (workshop.OwnedInstanceId != null) continue;
            var assigned = assignments?.FirstOrDefault(item =>
                item != null &&
                string.Equals(item.ComponentKind, "workshop", StringComparison.Ordinal) &&
                string.Equals(item.SlotId, workshop.BoardSlotId, StringComparison.Ordinal));
            workshop.OwnedInstanceId = assigned?.OwnedInstanceId
                ?? "workshop-" + Guid.NewGuid().ToString("N");
        }

        return new WorldV2OwnerIdentityMap(
            worldInstanceId,
            "ship-" + Guid.NewGuid().ToString("N"),
            "circuit-board-" + Guid.NewGuid().ToString("N"),
            "production-area-" + Guid.NewGuid().ToString("N"),
            "warehouse-" + Guid.NewGuid().ToString("N"),
            options.Producers.Select(item => item.ProducerInstanceId
                ?? throw new ArgumentException("ProducerInstanceId 不能为 null。", nameof(options))),
            options.Workshops.Select(item => item.OwnedInstanceId
                ?? throw new ArgumentException("OwnedInstanceId 不能为 null。", nameof(options))));
    }

    /// <summary>
    /// 使用宿主显式提供的身份创建或替换世界。宿主可在存档/重放边界复用已持久化身份；
    /// 普通新世界由上层 Replace(options) 在 Host 边界生成一次。
    /// </summary>
    public void Replace(WorldV2Options options, WorldInstanceId worldInstanceId)
    {
        var ownerIdentities = EnsureOwnerIdentityMap(options, worldInstanceId);
        Replace(options, worldInstanceId, ownerIdentities);
    }

    /// <summary>使用已持久化的 World 与 Owner 身份映射装配世界。</summary>
    public void Replace(
        WorldV2Options options,
        WorldInstanceId worldInstanceId,
        WorldV2OwnerIdentityMap? ownerIdentities)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (!worldInstanceId.IsValid)
            throw new ArgumentException("WorldInstanceId 无效。", nameof(worldInstanceId));
        ownerIdentities?.ValidateForWorld(worldInstanceId);
        ThrowIfOperationReentrant();

        lock (_operationSync)
        {
            lock (_sync) ThrowIfDisposed();

            // Factory 失败时尚未改动当前世界。
            var next = WorldV2Factory.Create(options, worldInstanceId: worldInstanceId, ownerIdentities: ownerIdentities);
            WorldV2Context? previous;
            lock (_sync)
            {
                ThrowIfDisposed();
                previous = _current;
                _current = next;
            }

            // 操作门仍被持有，因此没有宿主操作正在使用旧 Context。
            previous?.Dispose();
        }
    }

    /// <summary>
    /// 通过新 Context 执行检查点恢复，并在全部恢复校验成功后一次性替换当前世界。
    /// 准备阶段或恢复失败时，新 Context 被释放，当前 Context 保持原样；
    /// 成功时先发布恢复完成的新 Context，再释放旧 Context。
    /// </summary>
    public void RestoreExecutionResourceCheckpoint(
        WorldV2Options options,
        WorldV2ExecutionResourceCheckpoint checkpoint,
        WorldInstanceId? legacyMigrationWorldInstanceId = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
        ThrowIfOperationReentrant();

        lock (_operationSync)
        {
            lock (_sync) ThrowIfDisposed();

            // Prepare 对象拥有未发布 Context；异常或提前退出时自动释放。
            // Restore 成功后 TakeContext 才把所有权交给 Host 的唯一发布点。
            using var preparedRestore = PreparedWorldV2Restore.Create(options, checkpoint, legacyMigrationWorldInstanceId);
            var next = preparedRestore.TakeContext();

            // Commit：唯一发布点。Host 操作门保证没有调用方同时使用旧 Context。
            WorldV2Context? previous;
            lock (_sync)
            {
                ThrowIfDisposed();
                previous = _current;
                _current = next;
            }

            previous?.Dispose();
        }
    }

    /// <summary>
    /// 在 Host 独占操作范围内访问当前世界。
    ///
    /// 回调是同步操作：禁止 async/await、async void，禁止把 Context 或其可变子对象
    /// 保存到回调之外。需要向外返回数据时，应在回调内复制为独立 DTO/值对象。
    /// 不要在回调中等待另一个会再次调用同一 Host 的任务。
    /// </summary>
    public void ExecuteExclusive(Action<WorldV2Context> operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        ThrowIfOperationReentrant();

        lock (_operationSync)
        {
            WorldV2Context current;
            lock (_sync)
            {
                ThrowIfDisposed();
                current = _current ?? throw new InvalidOperationException("World v2 尚未创建");
            }

            operation(current);
        }
    }

    /// <summary>
    /// 关闭 Host。若有世界操作正在执行，关闭会等待该操作结束；
    /// 随后退役并释放当前 Context。重复调用安全。
    /// </summary>
    public void Dispose()
    {
        ThrowIfOperationReentrant();

        lock (_operationSync)
        {
            WorldV2Context? previous;
            lock (_sync)
            {
                if (_disposed) return;

                _disposed = true;
                previous = _current;
                _current = null;
            }

            previous?.Dispose();
        }
    }

    private void ThrowIfOperationReentrant()
    {
        if (Monitor.IsEntered(_operationSync))
            throw new InvalidOperationException(
                "World v2 Host 独占操作不可重入，也不能在操作回调内替换或关闭 Host");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorldV2HostService));
    }
}

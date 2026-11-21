using System;
using System.Threading;

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
        if (options == null) throw new ArgumentNullException(nameof(options));
        ThrowIfOperationReentrant();

        lock (_operationSync)
        {
            lock (_sync) ThrowIfDisposed();

            // Factory 失败时尚未改动当前世界。
            var next = WorldV2Factory.Create(options);
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
        WorldV2ExecutionResourceCheckpoint checkpoint)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
        ThrowIfOperationReentrant();

        lock (_operationSync)
        {
            lock (_sync) ThrowIfDisposed();

            WorldV2Context? prepared = null;
            try
            {
                // Prepare：完全独立地装配新世界并恢复快照。此期间 _current 不可见地改变。
                prepared = WorldV2Factory.Create(options);
                prepared.RestoreExecutionResourceCheckpoint(checkpoint);
            }
            catch
            {
                prepared?.Dispose();
                throw;
            }

            var next = prepared
                ?? throw new InvalidOperationException("检查点恢复准备阶段没有产生新 Context。");

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

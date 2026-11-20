using System;
using System.Threading;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 世界实例的生命周期所有者。
///
/// Host 管理当前 Context 的发布与退役。调用方通过 Lease 声明一次独占操作期；
/// 同一 Host 同一时刻只允许一个 Context Lease 存续，避免多个调用者并发修改世界。
/// 替换或关闭 Host 时，旧 Context 只会在最后一个 Lease 释放后 Dispose。
/// 本类型不推进逻辑时间、不启动后台线程，也不决定宿主的墙钟节奏。
/// </summary>
public sealed class WorldV2HostService : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private WorldEntry? _current;
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
    /// 获取当前世界的独占操作租约。
    /// 租约存续期间，其他 AcquireCurrent/Replace 调用会等待；调用方必须使用 using/finally 释放。
    /// </summary>
    public WorldV2ContextLease AcquireCurrent()
    {
        _operationGate.Wait();
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                var entry = _current ?? throw new InvalidOperationException("World v2 尚未创建");
                return Acquire(entry);
            }
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    /// <summary>
    /// 等待当前独占操作结束后，首次创建或原子替换当前世界，并返回新世界的独占操作租约。
    /// 配置校验或装配失败时，当前世界保持不变。
    /// 旧世界在所有已发放租约释放后才会被 Dispose。
    /// </summary>
    public WorldV2ContextLease Replace(WorldV2Options options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        _operationGate.Wait();
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                // 完整创建成功前不修改 _current。
                var next = new WorldEntry(WorldV2Factory.Create(options));
                var previous = _current;
                _current = next;

                if (previous != null)
                    Retire(previous);

                return Acquire(next);
            }
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    /// <summary>
    /// 关闭 Host 并退役当前世界。已有租约仍可安全完成工作；
    /// 最后一个租约释放时，世界才真正 Dispose。重复调用安全。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;

            _disposed = true;
            var previous = _current;
            _current = null;

            if (previous != null)
                Retire(previous);
        }
    }

    private WorldV2ContextLease Acquire(WorldEntry entry)
    {
        if (entry.Retired || entry.Disposed)
            throw new InvalidOperationException("不能为已退役的世界创建租约");

        entry.LeaseCount++;
        return new WorldV2ContextLease(entry.Context, () =>
        {
            try
            {
                Release(entry);
            }
            finally
            {
                _operationGate.Release();
            }
        });
    }

    private void Release(WorldEntry entry)
    {
        lock (_sync)
        {
            if (entry.LeaseCount <= 0)
                throw new InvalidOperationException("World v2 租约计数不平衡");

            entry.LeaseCount--;
            DisposeIfUnused(entry);
        }
    }

    private void Retire(WorldEntry entry)
    {
        entry.Retired = true;
        DisposeIfUnused(entry);
    }

    private static void DisposeIfUnused(WorldEntry entry)
    {
        if (!entry.Retired || entry.LeaseCount != 0 || entry.Disposed)
            return;

        entry.Disposed = true;
        entry.Context.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorldV2HostService));
    }

    private sealed class WorldEntry(WorldV2Context context)
    {
        public WorldV2Context Context { get; } = context;
        public int LeaseCount { get; set; }
        public bool Retired { get; set; }
        public bool Disposed { get; set; }
    }
}

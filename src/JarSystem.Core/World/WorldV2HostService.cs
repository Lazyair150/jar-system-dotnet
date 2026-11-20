using System;
using System.Threading;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 世界实例的生命周期所有者。
///
/// Host 管理当前 Context 的发布与退役。调用方通过 Lease 声明一次使用期；
/// 替换或关闭 Host 时，旧 Context 只会在最后一个 Lease 释放后 Dispose。
/// 本类型不推进逻辑时间、不启动后台线程，也不决定宿主的墙钟节奏。
/// </summary>
public sealed class WorldV2HostService : IDisposable
{
    private readonly object _sync = new();
    // 所有由宿主协调的世界操作共用一个串行门；生命周期锁只保护 Entry 元数据。
    private readonly object _operationSync = new();
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
    /// 获取当前世界的生命周期租约。
    /// 租约只阻止 Context 被释放，不提供并发访问/修改互斥。
    /// 若要访问或修改世界状态，优先使用 ExecuteExclusive。
    /// </summary>
    public WorldV2ContextLease AcquireCurrent()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var entry = _current ?? throw new InvalidOperationException("World v2 尚未创建");
            return Acquire(entry);
        }
    }

    /// <summary>
    /// 在没有宿主协调操作正在执行时，首次创建或原子替换当前世界。
    /// 配置校验或装配失败时，当前世界保持不变。
    /// 旧世界在所有已发放生命周期租约释放后才会被 Dispose。
    /// </summary>
    public WorldV2ContextLease Replace(WorldV2Options options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

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

    /// <summary>
    /// 关闭 Host 并退役当前世界。已有租约仍可安全完成工作；
    /// 最后一个租约释放时，世界才真正 Dispose。重复调用安全。
    /// </summary>
    public void Dispose()
    {
        lock (_operationSync)
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

    /// <summary>
    /// 在 Host 的独占操作门内访问当前世界。
    /// 同一 Host 上的 ExecuteExclusive、Replace 与 Dispose 串行执行；
    /// 回调必须同步完成，不能把 Context 或其可变子对象保存到回调之外。
    /// 返回值应是脱离世界对象图的结果数据，而不是 Context/Runtime/Board 等对象引用。
    /// </summary>
    public TResult ExecuteExclusive<TResult>(Func<WorldV2Context, TResult> operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        if (Monitor.IsEntered(_operationSync))
            throw new InvalidOperationException("World v2 Host 独占操作不可重入");

        lock (_operationSync)
        {
            using var lease = AcquireCurrent();
            return operation(lease.Context);
        }
    }

    /// <summary>执行不需要返回结果的独占世界操作。</summary>
    public void ExecuteExclusive(Action<WorldV2Context> operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        ExecuteExclusive(context =>
        {
            operation(context);
            return true;
        });
    }

    private WorldV2ContextLease Acquire(WorldEntry entry)
    {
        if (entry.Retired || entry.Disposed)
            throw new InvalidOperationException("不能为已退役的世界创建租约");

        entry.LeaseCount++;
        return new WorldV2ContextLease(entry.Context, () => Release(entry));
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

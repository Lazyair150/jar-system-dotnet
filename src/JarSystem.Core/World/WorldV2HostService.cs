using System;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 世界实例的生命周期所有者。
///
/// 本类型只管理 Context 的创建、切换和释放：不推进逻辑时间、不启动后台线程，
/// 也不决定宿主的墙钟节奏。宿主仍须在自己的串行边界内协调对 Current 的使用与 Replace。
/// </summary>
public sealed class WorldV2HostService : IDisposable
{
    private readonly object _sync = new();
    private WorldV2Context? _current;
    private bool _disposed;

    /// <summary>
    /// 当前世界。尚未创建或宿主已释放时抛出异常。
    /// 返回的是 Context 本身；调用方必须避免在并发 Replace/Dispose 期间继续使用旧引用。
    /// </summary>
    public WorldV2Context Current
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                return _current ?? throw new InvalidOperationException("World v2 尚未创建");
            }
        }
    }

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
    /// 新 Context 完整创建成功后才发布；配置校验或装配失败时，旧 Context 保持不变。
    /// 成功切换后释放旧 Context。该操作由内部锁串行化。
    /// </summary>
    public WorldV2Context Replace(WorldV2Options options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        lock (_sync)
        {
            ThrowIfDisposed();

            // 工厂在发布前完成完整校验与装配；此行抛出时 _current 尚未改变。
            var next = WorldV2Factory.Create(options);
            var previous = _current;

            _current = next;

            // Context.Dispose 当前只解除内部事件订阅，不回滚世界状态。
            // 在切换之后释放，避免释放失败时把当前引用退回一个半释放对象。
            previous?.Dispose();

            return next;
        }
    }

    /// <summary>释放当前世界并关闭宿主；重复调用安全。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;

            _disposed = true;
            var previous = _current;
            _current = null;
            previous?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WorldV2HostService));
    }
}

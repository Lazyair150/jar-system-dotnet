using System;
using System.Threading;

namespace JarSystem.Core.World;

/// <summary>
/// 对某个 World v2 Context 的生命周期租约。
/// 持有租约期间，即使 Host 替换或关闭，该 Context 也不会被释放。
/// 使用者必须在完成本次访问后 Dispose 租约。
/// </summary>
public sealed class WorldV2ContextLease : IDisposable
{
    private readonly Action _release;
    private WorldV2Context? _context;

    internal WorldV2ContextLease(WorldV2Context context, Action release)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    /// <summary>租约保护的世界；租约释放后不可再访问。</summary>
    public WorldV2Context Context =>
        Volatile.Read(ref _context) ??
        throw new ObjectDisposedException(nameof(WorldV2ContextLease));

    public bool IsDisposed => Volatile.Read(ref _context) == null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _context, null) == null)
            return;

        _release();
    }
}

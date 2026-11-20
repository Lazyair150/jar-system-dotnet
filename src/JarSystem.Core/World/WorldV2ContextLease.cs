using System;
using System.Threading;

namespace JarSystem.Core.World;

/// <summary>
/// 对某个 World v2 Context 的独占操作租约。
/// 持有租约期间，Context 不会被释放，且同一 Host 不会向其他调用者发放操作租约。
/// 使用者必须在完成本次操作后 Dispose 租约。
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

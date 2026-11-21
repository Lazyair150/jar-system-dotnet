using System;

namespace JarSystem.Core.World;

/// <summary>
/// 已完成配置装配和检查点恢复、尚未发布到 Host 的独立世界。
/// 此对象拥有 Context；只有 TakeContext 才能把所有权转交给 Host。
/// 未提交的准备结果在 Dispose 时自动释放。
/// </summary>
internal sealed class PreparedWorldV2Restore : IDisposable
{
    private WorldV2Context? _context;

    private PreparedWorldV2Restore(WorldV2Context context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public static PreparedWorldV2Restore Create(
        WorldV2Options options,
        WorldV2ExecutionResourceCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(checkpoint);

        var context = WorldV2Factory.Create(options);
        try
        {
            context.RestoreExecutionResourceCheckpoint(checkpoint);
            return new PreparedWorldV2Restore(context);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>将已准备完成的 Context 所有权一次性转交给发布者。</summary>
    public WorldV2Context TakeContext()
    {
        var context = _context
            ?? throw new InvalidOperationException("Prepared Restore 已经提交或释放。");
        _context = null;
        return context;
    }

    public void Dispose()
    {
        var context = _context;
        _context = null;
        context?.Dispose();
    }
}

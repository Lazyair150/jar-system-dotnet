using System;
using JarSystem.Core.Domain;

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
        WorldV2ExecutionResourceCheckpoint checkpoint,
        WorldInstanceId? legacyMigrationWorldInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (legacyMigrationWorldInstanceId.HasValue &&
            !legacyMigrationWorldInstanceId.Value.IsValid)
            throw new ArgumentException("Legacy migration WorldInstanceId 无效。",
                nameof(legacyMigrationWorldInstanceId));
        if (checkpoint.WorldInstanceId.HasValue &&
            legacyMigrationWorldInstanceId.HasValue &&
            checkpoint.WorldInstanceId.Value != legacyMigrationWorldInstanceId.Value)
            throw new InvalidOperationException(
                "迁移身份不能覆盖检查点中已持久化的 WorldInstanceId。");

        var targetWorldInstanceId = checkpoint.WorldInstanceId ?? legacyMigrationWorldInstanceId;
        if (!targetWorldInstanceId.HasValue)
            throw new InvalidOperationException(
                "旧检查点缺少 WorldInstanceId；恢复前必须由宿主显式提供并持久化迁移身份。");

        var restoreCheckpoint = checkpoint;
        if (!checkpoint.WorldInstanceId.HasValue)
        {
            var wire = WorldV2ExecutionCheckpointWireDto.FromCheckpoint(checkpoint);
            wire.WorldInstanceId = targetWorldInstanceId.Value.Value;
            restoreCheckpoint = wire.ToCheckpoint(checkpoint.ConfigurationFingerprint);
        }

        var context = WorldV2Factory.Create(
            options,
            worldInstanceId: targetWorldInstanceId.Value,
            ownerIdentities: restoreCheckpoint.OwnerIdentities);
        try
        {
            context.RestoreExecutionResourceCheckpoint(restoreCheckpoint);
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

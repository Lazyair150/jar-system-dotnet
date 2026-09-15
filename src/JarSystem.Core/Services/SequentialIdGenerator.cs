using System.Threading;
using JarSystem.Core.Abstractions;

namespace JarSystem.Core.Services;

/// <summary>
/// 进程内单调递增的 ID 生成器（<see cref="IIdGenerator"/> 的默认实现）。
/// 语义与重构前的 <c>Jar._nextGlobalId</c> / <c>Order._idCounter</c> 一致：从 1 开始、只增不减、<c>Reset()</c> 不清零。
/// </summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _value;

    /// <inheritdoc />
    public int Next() => Interlocked.Increment(ref _value);
}

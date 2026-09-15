namespace JarSystem.Core.Abstractions;

/// <summary>
/// 唯一 ID 源契约（阶段 2：替代 <c>Jar.NextId()</c> 与 <c>Order._idCounter</c> 两个静态可变计数器）。
/// 默认实现保持现有语义：进程内单调递增、<c>Reset()</c> 不清零。
/// </summary>
public interface IIdGenerator
{
    /// <summary>取下一个唯一 ID（大于 0）。</summary>
    int Next();
}

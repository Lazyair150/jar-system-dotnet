namespace JarSystem.Core.Domain;

/// <summary>
/// 唯一 ID 源契约（阶段 2：替代 <c>Jar.NextId()</c> 与 <c>Order._idCounter</c> 两个静态可变计数器）。
/// 默认实现保持现有语义：进程内单调递增、<c>Reset()</c> 不清零。
///
/// <para>
/// **为什么住在 <c>Domain</c> 而不是 <c>Abstractions</c>**：它约束的是领域实体的身份怎么来，
/// 是领域概念，不是求解器/过程那样的技术契约。需要隔离的测试或多世界场景可注入独立生成器。
/// </para>
/// </summary>
public interface IIdGenerator
{
    /// <summary>取下一个唯一 ID（大于 0）。</summary>
    int Next();
}

/// <summary>进程内单调递增的 ID 生成器。</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _value;
    public int Next() => Interlocked.Increment(ref _value);
}

/// <summary>默认共享 ID 源。不同实体种类使用独立计数器，避免改变旧订单/罐子 ID 契约。</summary>
public static class DefaultIdGenerators
{
    /// <summary>罐子 ID 源。</summary>
    public static readonly IIdGenerator Jars = new SequentialIdGenerator();

    /// <summary>旧订单 ID 源。</summary>
    public static readonly IIdGenerator Orders = new SequentialIdGenerator();

    /// <summary>World v2 需求 ID 源；与旧订单 ID 空间隔离。</summary>
    public static readonly IIdGenerator Demands = new SequentialIdGenerator();
}

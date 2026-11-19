namespace JarSystem.Core.Domain;

/// <summary>
/// 唯一 ID 源契约（阶段 2：替代 <c>Jar.NextId()</c> 与 <c>Order._idCounter</c> 两个静态可变计数器）。
/// 默认实现保持现有语义：进程内单调递增、<c>Reset()</c> 不清零。
///
/// <para>
/// **为什么住在 <c>Domain</c> 而不是 <c>Abstractions</c>**：它约束的是"罐子与订单的身份怎么来"，
/// 是领域概念（<see cref="Jar.Id"/>、<c>Order.Id</c> 的唯一性来源），不是求解器/过程那样的技术契约。
/// 放在这里之后，<c>Domain</c> 不再需要 <c>using ...Abstractions</c> 才能提到自己的身份来源，
/// 而"默认值的单一出处"也就不必再下沉到用例层（原先那行 <c>DefaultIdGenerators.Jars</c>
/// 让整个 <c>Domain</c> 依赖了 <c>Services</c>）。
/// </para>
/// </summary>
public interface IIdGenerator
{
    /// <summary>取下一个唯一 ID（大于 0）。</summary>
    int Next();
}

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

/// <summary>
/// 默认共享 ID 源（唯一持有「进程级计数器」语义的地方，取代原先藏在 Jar/Order 里的静态字段）。
///
/// 为什么仍是共享实例：旧世界跨工作台全局唯一的罐子 ID 是既有行为契约（B 类回滚按 ID 判定归属），
/// 且 <c>Reset()</c> 不清零。旧罐子与旧订单继续使用各自的计数器，避免改变旧 JSON 中 <c>id</c> 的取值；
/// 新世界需求另设独立计数器，不与旧订单或罐子 ID 空间混用。
///
/// 需要隔离的测试（或未来的多世界场景）可构造 <see cref="SequentialIdGenerator"/> 注入，而不必改动全局状态。
/// </summary>
public static class DefaultIdGenerators
{
    /// <summary>罐子 ID 源。</summary>
    public static readonly IIdGenerator Jars = new SequentialIdGenerator();

    /// <summary>订单 ID 源。</summary>
    public static readonly IIdGenerator Orders = new SequentialIdGenerator();

    /// <summary>World v2 需求 ID 源；与旧订单 ID 空间隔离。</summary>
    public static readonly IIdGenerator Demands = new SequentialIdGenerator();
}

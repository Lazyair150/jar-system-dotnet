using JarSystem.Core.Abstractions;

namespace JarSystem.Core.Services;

/// <summary>
/// 默认共享 ID 源（唯一持有「进程级计数器」语义的地方，取代原先藏在 Jar/Order 里的静态字段）。
///
/// 为什么仍是共享实例：跨工作台全局唯一的罐子 ID 是既有行为契约（B 类回滚按 ID 判定归属），
/// 且 <c>Reset()</c> 不清零。为什么是**两个**计数器：重构前罐子与订单各自从 1 开始编号，
/// 共用一个计数器会改变对外 JSON 中 <c>id</c> 的取值。
///
/// 需要隔离的测试（或未来的多世界场景）可构造 <see cref="SequentialIdGenerator"/> 注入，而不必改动全局状态。
/// </summary>
public static class DefaultIdGenerators
{
    /// <summary>罐子 ID 源。</summary>
    public static readonly IIdGenerator Jars = new SequentialIdGenerator();

    /// <summary>订单 ID 源。</summary>
    public static readonly IIdGenerator Orders = new SequentialIdGenerator();
}

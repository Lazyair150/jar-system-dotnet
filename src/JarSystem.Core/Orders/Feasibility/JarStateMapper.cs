using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>
/// **罐子 → 求解器词汇**的映射（把 `Abstractions → Domain` 的成环解开）。
///
/// `JarState` 是契约词汇（无身份、纯值），本来属于 `Abstractions`；
/// 但它原先带一个 `static JarState From(Jar jar)` 工厂，于是抽象层反向认识实体层。
/// 映射是实体的职责，不是词汇的职责 —— 所以它搬到这里：`Domain → Abstractions`，单向。
/// </summary>
public static class JarStateMapper
{
    /// <summary>
    /// 把工作台里的罐子投影成无身份的求解器输入。
    /// <c>MasterOrigin</c> **不在这里推导**：它是"这只罐子属于从台求解池且来自主台"的选择性转移标记，
    /// 由组装求解池的调用方（`FeasibilityChecks`）按上下文显式设置，实体本身没有这个属性。
    /// </summary>
    public static JarState From(Jar jar) => new()
    {
        Id = jar.Id,
        Shape = jar.Shape,
        Liquid = jar.Liquid,
        IsMarked = jar.IsMarked,
    };
}

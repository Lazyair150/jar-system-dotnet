using System.Collections.Generic;

namespace JarSystem.Core.Config;

/// <summary>
/// 世界词汇表：**有哪些形状、哪些液体**。
///
/// 这是形状/液体目录的**唯一权威**。规则语义**不在**词表里 —— 形状只是名字，
/// 它能怎么变由工作台的 <c>WorkshopCapability</c>（标记）决定；
/// 因此"加/减形状"只需要换一份词表即可，不必改规则引擎、求解器或执行器。
///
/// 边界（与用户的玩法定位一致）：
/// <list type="bullet">
/// <item>**形状**：可变（数据）；</item>
/// <item>**液体**：**必须恰好两种**（玩法语义），词表在此只承载它们的**名字**。</item>
/// </list>
///
/// <see cref="Default"/> 就是重构前的内置词表（<see cref="Constants.SHAPES"/> / <see cref="Constants.LIQUIDS"/>），
/// 因此**不注入时行为与重构前逐字相同**。
/// </summary>
public sealed class Vocabulary
{
    private readonly List<string> _shapes;
    private readonly List<string> _liquids;

    public Vocabulary(IEnumerable<string> shapes, IEnumerable<string> liquids)
    {
        _shapes = new List<string>(shapes);
        _liquids = new List<string>(liquids);
    }

    /// <summary>形状目录（顺序 = 契约快照里形状相关键的出现顺序）。</summary>
    public IReadOnlyList<string> Shapes => _shapes;

    /// <summary>液体目录（恰好两种；顺序 = 契约快照里 `liquids` 的键序）。</summary>
    public IReadOnlyList<string> Liquids => _liquids;

    public bool IsValidShape(string shape) => _shapes.Contains(shape);

    public bool IsValidLiquid(string liquid) => _liquids.Contains(liquid);

    /// <summary>内置默认词表（= 重构前的常量，8 形状 / 2 液体）。</summary>
    public static Vocabulary Default { get; } = new(Constants.SHAPES, Constants.LIQUIDS);
}

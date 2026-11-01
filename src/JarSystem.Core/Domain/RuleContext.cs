using JarSystem.Core.Domain;

namespace JarSystem.Core.Domain;

/// <summary>
/// 规则读取的唯一入口：基础法则（<see cref="WorkshopCapability"/>）+ 活跃修饰器。
///
/// 为什么要有它：修饰器要能影响"能不能倒液/改形/被禁"，而规则读取散在 <c>Workshop</c>、
/// 快照与求解器里。把读取收敛到一个对象后：
/// <list type="bullet">
/// <item>默认空栈就等于今天的行为（逐项等价，可用于等价性论证）；</item>
/// <item>修饰器只影响这一个入口，不必在每处规则判断里各加一段分支；</item>
/// <item>求解器与执行器可以共用同一个上下文对象（P3b-2 接），口径不会漂。</item>
/// </list>
/// </summary>
public sealed class RuleContext
{
    private readonly WorkshopCapability _capability;

    /// <summary>由基础能力构造（无修饰器：与改造前的规则判定逐项相同）。</summary>
    public static RuleContext From(WorkshopCapability capability) => new(capability, ModifierStack.Empty);

    /// <summary>由基础能力 + 修饰器栈构造。</summary>
    public RuleContext(WorkshopCapability capability, ModifierStack stack)
    {
        _capability = capability;
        Stack = stack;
    }

    /// <summary>基础能力（只读；快照与对外投影仍读它）。</summary>
    public WorkshopCapability Capability => _capability;

    /// <summary>活跃修饰器栈（与工作台、过程系统共用同一实例：一处入栈，处处生效）。</summary>
    public ModifierStack Stack { get; }

    public bool IsBaseMarked(string shape) => _capability.IsBaseMarked(shape);

    /// <summary>是否属于附加标记（基础 + 被 <see cref="GrantModifier"/> 授予的形状）。</summary>
    public bool IsAdditionalMarked(string shape)
    {
        if (_capability.IsAdditionalMarked(shape)) return true;
        if (Stack.IsEmpty) return false;                       // 热路径：空栈不做任何分配
        foreach (var granted in Stack.GrantedShapes())
            if (granted == shape) return true;
        return false;
    }

    public bool IsBanned(string liquid) => _capability.IsBanned(liquid);

    /// <summary>可改形目标：基础能力给出的 + 被授予的形状（后者视作附加标记）。</summary>
    public List<string> GetPossibleTransforms(string shape)
    {
        var granted = Stack.GrantedShapes();
        return granted.Count == 0
            ? _capability.GetPossibleTransforms(shape)
            : _capability.GetPossibleTransforms(shape, granted);
    }

    public bool CanTransform(string from, string to) => GetPossibleTransforms(from).Contains(to);

    /// <summary>该操作现在是否被修饰器抑制（返回原因或 null）。</summary>
    public string? SuppressionReason(string? operation, string? shape, string? liquid) =>
        Stack.SuppressionReason(operation, shape, liquid);
}

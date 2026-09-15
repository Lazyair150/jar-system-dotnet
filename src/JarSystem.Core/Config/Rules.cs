using System.Collections.Generic;

namespace JarSystem.Core.Config;

/// <summary>
/// 转换规则表 - 数据驱动核心。
/// 模板为不可变数据：只在类型初始化时构造一次（原实现为属性，每次访问都会重新分配数组）。
/// </summary>
public static class Rules
{
    public static readonly WorkshopTemplate[] WORKSHOP_TEMPLATES =
    [
        new() { Name = "玄武台", BaseMarks = ["圆罐", "方罐"], AdditionalMarks = ["三角罐"], Ban = ["酒"] },
        new() { Name = "朱雀台", BaseMarks = ["高罐"], AdditionalMarks = ["长罐"], Ban = ["酒"] }
    ];
}

/// <summary>工作台模板（不可变）：由 <see cref="Rules.WORKSHOP_TEMPLATES"/> 提供默认数据。</summary>
public class WorkshopTemplate
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> BaseMarks { get; init; }
    public IReadOnlyList<string> AdditionalMarks { get; init; } = [];
    public IReadOnlyList<string> Ban { get; init; } = [];
}

/// <summary>
/// 形状转换规则唯一来源。Workshop / WorkshopInfo 都委托到这里，避免多份实现漂移。
/// 规则：基础标记 → 其它基础标记 + 全部附加标记；附加标记 → 全部基础标记；不在任何标记中 → 全部基础标记。
/// </summary>
public static class ShapeRules
{
    public static List<string> GetPossibleTransforms(
        IReadOnlyList<string> baseMarks, IReadOnlyList<string> additionalMarks, string shape)
    {
        var targets = new HashSet<string>();
        if (baseMarks.Contains(shape))
        {
            foreach (var m in baseMarks) if (m != shape) targets.Add(m);
            foreach (var m in additionalMarks) if (m != shape) targets.Add(m);
        }
        else
        {
            // 附加标记 → 全部基础标记；不在任何标记中 → 全部基础标记（两种情形结果相同，原实现为同体双分支）
            foreach (var m in baseMarks) if (m != shape) targets.Add(m);
        }
        return targets.ToList();
    }
}

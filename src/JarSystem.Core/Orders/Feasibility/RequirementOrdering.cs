using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.Orders;

/// <summary>
/// 需求执行顺序的唯一实现（原先在可行性分析与执行器里各写一份 `OrderBy(r =&gt; r.Liquid != null ? 0 : 1)`）。
///
/// 语义：**满罐需求优先于空罐需求**。订单列表本身遵守「空罐在前、满罐在后」的展示约定（见 `规则.txt`），
/// 但执行顺序以本规则为准。`OrderBy` 是稳定排序，同键需求保持原有相对顺序。
/// </summary>
public static class RequirementOrdering
{
    /// <summary>按执行顺序排列需求：满罐（有液体）在前，空罐在后。</summary>
    public static List<Requirement> FullFirst(IEnumerable<Requirement> requirements) =>
        requirements.OrderBy(r => r.Liquid != null ? 0 : 1).ToList();
}

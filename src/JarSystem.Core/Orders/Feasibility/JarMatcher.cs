using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>
/// 需求 ↔ 罐子匹配的唯一实现。原先同一规则在三个地方各写一份：
/// <see cref="Order.IsSatisfiedBy"/>、可行性划分搜索里的「交付罐匹配」、<see cref="OrderEvent.Deliver"/> 的交付挑选。
///
/// 规则：形状相同，且「满罐需求比较液体、空罐需求判断是否为空」；
/// 同一个罐子只能命中一个需求，按需求顺序贪心取**首次命中**。
/// </summary>
public static class JarMatcher
{
    /// <summary>真实罐子是否满足需求。</summary>
    public static bool Matches(Jar jar, Requirement req) =>
        jar.Shape == req.Shape && (req.Liquid != null ? jar.Liquid == req.Liquid : jar.IsEmpty());

    /// <summary>求解器罐子状态是否满足需求（`Liquid == null` 即空罐）。</summary>
    public static bool Matches(JarState jar, Requirement req) =>
        jar.Shape == req.Shape && (req.Liquid != null ? jar.Liquid == req.Liquid : jar.Liquid == null);

    /// <summary>
    /// 按需求顺序贪心匹配，返回每个需求命中的罐子下标；未命中为 -1。
    /// 命中即占用该下标，后续需求不再复用它。
    /// </summary>
    public static int[] MatchAll<T>(
        IReadOnlyList<T> jars, IReadOnlyList<Requirement> requirements, Func<T, Requirement, bool> matches)
    {
        var result = new int[requirements.Count];
        var used = new bool[jars.Count];
        for (int r = 0; r < requirements.Count; r++)
        {
            result[r] = -1;
            for (int i = 0; i < jars.Count; i++)
            {
                if (used[i]) continue;
                if (!matches(jars[i], requirements[r])) continue;
                used[i] = true;
                result[r] = i;
                break;
            }
        }
        return result;
    }
}

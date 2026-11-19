using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;

namespace JarSystem.Core.Domain;

/// <summary>
/// 当前罐子世界对订单内容的解释。
/// A/B 类型、罐子需求、词表与需求顺序属于玩法领域，不属于通用订单区域管理。
/// </summary>
public sealed class JarOrderSpecification : IOrderSpecification
{
    public string Type { get; }
    public IReadOnlyList<Requirement> Requirements { get; }

    public JarOrderSpecification(
        IEnumerable<Requirement> requirements,
        string type = Constants.OrderType.A,
        Vocabulary? vocabulary = null)
    {
        if (requirements == null) throw new ArgumentNullException(nameof(requirements));
        if (type != Constants.OrderType.A && type != Constants.OrderType.B)
            throw new DomainValidationException($"订单类型只能是 A 或 B，收到: {type}");

        var words = vocabulary ?? Vocabulary.Default;
        var copied = requirements.Select(r =>
        {
            if (r == null) throw new DomainValidationException("订单需求不能为 null");
            if (!words.IsValidShape(r.Shape))
                throw new DomainValidationException($"无效形状: {r.Shape}");
            if (r.Liquid != null && !words.IsValidLiquid(r.Liquid))
                throw new DomainValidationException($"无效液体: {r.Liquid}");
            return new Requirement { Shape = r.Shape, Liquid = r.Liquid };
        }).ToList();

        if (copied.Count == 0)
            throw new DomainValidationException("订单至少需要一条需求");
        if (copied.Count > Constants.MAX_ORDER_REQUIREMENTS)
            throw new DomainValidationException(
                $"订单需求数量不能超过 {Constants.MAX_ORDER_REQUIREMENTS} 个");

        var seenFull = false;
        foreach (var requirement in copied)
        {
            if (requirement.Liquid != null)
                seenFull = true;
            else if (seenFull)
                throw new DomainValidationException(
                    "订单需求列表约定: 空罐需求在前、满罐需求在后（执行顺序为满罐优先）");
        }

        Type = type;
        Requirements = copied.AsReadOnly();
    }
}

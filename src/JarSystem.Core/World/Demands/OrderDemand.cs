using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// 新世界中的一张订单委托。它保存订单内容与自身 CD；
/// 是否留在订单区域、成功后如何处理，由所属区域决定。
/// </summary>
public sealed class OrderDemand
{
    private readonly IReadOnlyList<Requirement> _requirements;
    private long _cooldownRemainingLogicalMilli;

    public int Id { get; }
    public string Type { get; }
    public IReadOnlyList<Requirement> Requirements => _requirements;
    public long CooldownDurationLogicalMilli { get; }
    public long CooldownRemainingLogicalMilli => _cooldownRemainingLogicalMilli;
    public bool IsCoolingDown => _cooldownRemainingLogicalMilli > 0;

    /// <summary>
    /// 阶段四求解资格的订单自身条件。是否仍登记在区域中由 OrderDemandBoard 判断。
    /// 这不代表已经求解，也不代表拥有方案。
    /// </summary>
    public bool ReadyForSolve => !IsCoolingDown;

    public OrderDemand(
        IEnumerable<Requirement> requirements,
        string type,
        long cooldownDurationLogicalMilli,
        IIdGenerator? ids = null,
        Vocabulary? vocabulary = null)
    {
        if (requirements == null) throw new ArgumentNullException(nameof(requirements));
        if (type != Constants.OrderType.A && type != Constants.OrderType.B)
            throw new DomainValidationException($"订单类型只能是 A 或 B，收到: {type}");
        if (cooldownDurationLogicalMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(cooldownDurationLogicalMilli));

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

        Id = (ids ?? DefaultIdGenerators.Demands).Next();
        if (Id <= 0) throw new InvalidOperationException("需求 ID 生成器必须返回正整数");
        Type = type;
        CooldownDurationLogicalMilli = cooldownDurationLogicalMilli;
        _requirements = copied.AsReadOnly();
    }

    /// <summary>
    /// 显式进入本需求的 CD。已经处于 CD 的需求不能重复启动/重置 CD。
    /// CD 时长为 0 时不产生等待，返回 false。
    /// </summary>
    internal bool EnterCooldown()
    {
        if (IsCoolingDown || CooldownDurationLogicalMilli == 0)
            return false;

        _cooldownRemainingLogicalMilli = CooldownDurationLogicalMilli;
        return true;
    }

    /// <summary>按逻辑时间推进 CD；返回值表示本次刚好结束 CD。</summary>
    internal bool AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));
        if (!IsCoolingDown || deltaLogicalTimeMilli == 0)
            return false;

        if (deltaLogicalTimeMilli >= _cooldownRemainingLogicalMilli)
        {
            _cooldownRemainingLogicalMilli = 0;
            return true;
        }

        _cooldownRemainingLogicalMilli -= deltaLogicalTimeMilli;
        return false;
    }
}

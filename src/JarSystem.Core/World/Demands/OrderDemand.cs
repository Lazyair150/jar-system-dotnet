using System;
using JarSystem.Core.Abstractions;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// 通用订单委托。它只拥有身份、外部领域规格引用和自身 CD；
/// 不解释规格内容，也不决定成功后是否继续留在订单区域。
/// </summary>
public sealed class OrderDemand
{
    private long _cooldownRemainingLogicalMilli;

    public int Id { get; }
    public IOrderSpecification Specification { get; }
    public long CooldownDurationLogicalMilli { get; }
    public long CooldownRemainingLogicalMilli => _cooldownRemainingLogicalMilli;
    public bool IsCoolingDown => _cooldownRemainingLogicalMilli > 0;

    /// <summary>
    /// 阶段四求解资格的订单自身条件。是否仍登记在区域中由 OrderDemandBoard 判断。
    /// 这不代表已经求解，也不代表拥有方案。
    /// </summary>
    public bool ReadyForSolve => !IsCoolingDown;

    public OrderDemand(
        IOrderSpecification specification,
        long cooldownDurationLogicalMilli,
        IIdGenerator? ids = null)
    {
        Specification = specification ?? throw new ArgumentNullException(nameof(specification));
        if (cooldownDurationLogicalMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(cooldownDurationLogicalMilli));

        Id = (ids ?? DefaultIdGenerators.Demands).Next();
        if (Id <= 0) throw new InvalidOperationException("需求 ID 生成器必须返回正整数");
        CooldownDurationLogicalMilli = cooldownDurationLogicalMilli;
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

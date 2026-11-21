using System;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

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

    private OrderDemand(
        int id,
        IOrderSpecification specification,
        long cooldownDurationLogicalMilli,
        long cooldownRemainingLogicalMilli)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        Specification = specification ?? throw new ArgumentNullException(nameof(specification));
        if (cooldownDurationLogicalMilli < 0 ||
            cooldownRemainingLogicalMilli < 0 ||
            cooldownRemainingLogicalMilli > cooldownDurationLogicalMilli)
            throw new ArgumentOutOfRangeException(nameof(cooldownRemainingLogicalMilli));

        Id = id;
        CooldownDurationLogicalMilli = cooldownDurationLogicalMilli;
        _cooldownRemainingLogicalMilli = cooldownRemainingLogicalMilli;
    }

    internal static OrderDemand Restore(
        int id,
        IOrderSpecification specification,
        long cooldownDurationLogicalMilli,
        long cooldownRemainingLogicalMilli) =>
        new(id, specification, cooldownDurationLogicalMilli, cooldownRemainingLogicalMilli);

    internal bool EnterCooldown()
    {
        if (IsCoolingDown || CooldownDurationLogicalMilli == 0)
            return false;

        _cooldownRemainingLogicalMilli = CooldownDurationLogicalMilli;
        return true;
    }

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

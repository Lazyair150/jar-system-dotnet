using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// 新世界中的一张需求。需求内容不可变；开放状态与 CD 是两个独立状态轴。
/// 它不拥有工作台资源，也不保存求解方案或执行过程。
/// </summary>
public sealed class OrderDemand
{
    private readonly IReadOnlyList<Requirement> _requirements;
    private long _cooldownRemainingLogicalMilli;

    public int Id { get; }
    public string Type { get; }
    public IReadOnlyList<Requirement> Requirements => _requirements;
    public DemandPersistence Persistence { get; }
    public DemandLifecycleState Lifecycle { get; private set; } = DemandLifecycleState.Open;
    public long CooldownDurationLogicalMilli { get; }
    public long CooldownRemainingLogicalMilli => _cooldownRemainingLogicalMilli;
    public bool IsCoolingDown => _cooldownRemainingLogicalMilli > 0;

    /// <summary>阶段四求解入口的资格条件；不代表已经求解，也不代表拥有方案。</summary>
    public bool ReadyForSolve => Lifecycle == DemandLifecycleState.Open && !IsCoolingDown;

    public OrderDemand(
        IEnumerable<Requirement> requirements,
        string type,
        DemandPersistence persistence,
        long cooldownDurationLogicalMilli,
        IIdGenerator? ids = null,
        Vocabulary? vocabulary = null)
    {
        if (requirements == null) throw new ArgumentNullException(nameof(requirements));
        if (type != Constants.OrderType.A && type != Constants.OrderType.B)
            throw new DomainValidationException($"订单类型只能是 A 或 B，收到: {type}");
        if (!Enum.IsDefined(persistence))
            throw new ArgumentOutOfRangeException(nameof(persistence));
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
        Persistence = persistence;
        CooldownDurationLogicalMilli = cooldownDurationLogicalMilli;
        _requirements = copied.AsReadOnly();
    }

    /// <summary>
    /// 显式进入本需求的 CD。已经关闭或正在 CD 的需求不能重复启动/重置 CD。
    /// CD 时长为 0 时不产生等待，返回 false。
    /// </summary>
    public bool EnterCooldown()
    {
        if (Lifecycle != DemandLifecycleState.Open)
            throw new InvalidOperationException("已关闭的需求不能进入 CD");
        if (IsCoolingDown || CooldownDurationLogicalMilli == 0)
            return false;

        _cooldownRemainingLogicalMilli = CooldownDurationLogicalMilli;
        return true;
    }

    /// <summary>按逻辑时间推进 CD；返回值表示本次刚好结束 CD。</summary>
    public bool AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));
        if (Lifecycle == DemandLifecycleState.Closed || !IsCoolingDown || deltaLogicalTimeMilli == 0)
            return false;

        if (deltaLogicalTimeMilli >= _cooldownRemainingLogicalMilli)
        {
            _cooldownRemainingLogicalMilli = 0;
            return true;
        }

        _cooldownRemainingLogicalMilli -= deltaLogicalTimeMilli;
        return false;
    }

    /// <summary>用户显式关闭需求。关闭与 CD 是不同状态变化；关闭后不再参与求解。</summary>
    public bool Close()
    {
        if (Lifecycle == DemandLifecycleState.Closed) return false;
        Lifecycle = DemandLifecycleState.Closed;
        _cooldownRemainingLogicalMilli = 0;
        return true;
    }

    /// <summary>
    /// 记录一次成功满足。一次性需求关闭；持续性需求仍保持开放。
    /// 普通成功是否进入 CD 尚未定义，因此这里不会擅自启动 CD。
    /// </summary>
    public void RecordSuccessfulFulfillment()
    {
        if (!ReadyForSolve)
            throw new InvalidOperationException("需求未开放或仍处于 CD，不能记录成功满足");
        if (Persistence == DemandPersistence.OneShot)
            Close();
    }
}

public enum DemandPersistence
{
    OneShot,
    Continuous
}

public enum DemandLifecycleState
{
    Open,
    Closed
}

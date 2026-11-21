using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.World.Demands;

/// <summary>
/// 新世界唯一的需求板，包含主动与被动两个区域。
/// 它管理订单归属、身份唯一性、被动顺序、成功后的区域处理和 CD 时间推进；不负责求解或执行。
/// </summary>
public sealed class OrderDemandBoard
{
    /// <summary>订单归属、区域顺序或 CD 就绪状态发生变化时通知外部调度/求解状态簿。</summary>
    public event Action<OrderDemandBoardChange>? Changed;

    public OrderDemandArea Passive { get; }
    public OrderDemandArea Active { get; }

    public IReadOnlyList<OrderDemand> AllDemands =>
        Passive.Demands.Concat(Active.Demands).ToList().AsReadOnly();

    public int Count => Passive.Demands.Count + Active.Demands.Count;

    public OrderDemandBoard(
        DemandAreaFulfillmentPolicy passiveFulfillmentPolicy,
        DemandAreaFulfillmentPolicy activeFulfillmentPolicy)
    {
        Passive = new OrderDemandArea(DemandAreaKind.Passive, passiveFulfillmentPolicy);
        Active = new OrderDemandArea(DemandAreaKind.Active, activeFulfillmentPolicy);
    }

    public void AddDemand(OrderDemand demand, DemandAreaKind area)
    {
        if (demand == null) throw new ArgumentNullException(nameof(demand));
        if (Find(demand.Id) != null)
            throw new InvalidOperationException($"需求 ID 已登记: {demand.Id}");
        GetArea(area).Add(demand);
        Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.Added, demand.Id, area, area));
    }

    /// <summary>从区域卸载订单不等于销毁订单对象，也不回滚已发生的世界变化。</summary>
    public bool RemoveDemand(int demandId)
    {
        var demand = Find(demandId);
        if (demand == null) return false;
        var area = AreaOf(demand);
        if (!area.Remove(demand)) return false;
        Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.Removed, demand.Id,
            area.Kind, null));
        return true;
    }

    /// <summary>
    /// 执行过程报告订单成功后，由区域策略决定是否卸载该订单。
    /// 持续订单保留在原区域并启动自身 CD；CD 为 0 时立即具备下一轮求解资格。
    /// 本方法不验证执行结果，也不回滚已发生的世界变化。
    /// </summary>
    public DemandFulfillmentResult RecordSuccessfulFulfillment(int demandId)
    {
        var demand = Find(demandId);
        if (demand == null) return DemandFulfillmentResult.NotFound;

        var area = AreaOf(demand);
        if (area.RemoveAfterSuccessfulFulfillment)
        {
            area.Remove(demand);
            Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.Removed, demand.Id,
                area.Kind, null));
            return DemandFulfillmentResult.RemovedFromArea;
        }

        // 持续订单成功代表一个执行周期结束。订单仍留在原区域，但必须等待
        // 自己配置的逻辑 CD；CD 为 0 时 EnterCooldown 不制造等待。
        demand.EnterCooldown();
        Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.FulfilledRetained, demand.Id,
            area.Kind, area.Kind));
        return DemandFulfillmentResult.RetainedInArea;
    }

    /// <summary>
    /// 订单执行被工作台替换等外部事件中断时启动该订单自己的 CD。
    /// 普通执行失败不自动进入 CD；失败后由求解簿重新评估当前世界。
    /// </summary>
    public bool StartCooldown(int demandId)
    {
        var demand = Find(demandId);
        if (demand == null || !demand.EnterCooldown()) return false;
        var area = AreaOf(demand);
        Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.CooldownStarted,
            demand.Id, area.Kind, area.Kind));
        return true;
    }

    /// <summary>将需求移到另一个区域；跨区移动追加到目标区域末尾。</summary>
    public bool MoveToArea(int demandId, DemandAreaKind targetArea)
    {
        var demand = Find(demandId);
        if (demand == null) return false;

        var source = AreaOf(demand);
        var target = GetArea(targetArea);
        if (ReferenceEquals(source, target)) return true;

        // 所有可能失败的参数检查在移除来源之前完成。
        source.Remove(demand);
        target.Add(demand);
        Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.Moved, demand.Id,
            source.Kind, target.Kind));
        return true;
    }

    /// <summary>调整被动区域中的优先顺序；0 为最高优先级。</summary>
    public bool MovePassiveDemand(int demandId, int newIndex)
    {
        var demand = Passive.Demands.FirstOrDefault(x => x.Id == demandId);
        if (demand == null) return false;
        var oldIndex = Passive.Demands.ToList().IndexOf(demand);
        if (!Passive.Move(demand, newIndex)) return false;
        if (oldIndex != newIndex)
            Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.PassiveOrderChanged,
                demand.Id, DemandAreaKind.Passive, DemandAreaKind.Passive));
        return true;
    }

    public OrderDemand? Find(int demandId) =>
        Passive.Demands.Concat(Active.Demands).FirstOrDefault(x => x.Id == demandId);

    /// <summary>
    /// 推进所有已装载订单的 CD，返回本次刚结束 CD 的订单 ID。
    /// 调用方可在阶段四据此触发重新求解。
    /// </summary>
    internal IReadOnlyList<int> AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));

        var ready = new List<int>();
        foreach (var demand in Passive.Demands.Concat(Active.Demands).ToArray())
            if (demand.AdvanceLogicalTime(deltaLogicalTimeMilli))
                ready.Add(demand.Id);

        // 在枚举结束后再通知，避免订阅者在回调中增删订单破坏当前遍历。
        foreach (var demandId in ready)
        {
            var demand = Find(demandId);
            if (demand == null) continue;
            var area = AreaOf(demand);
            Changed?.Invoke(new OrderDemandBoardChange(OrderDemandBoardChangeKind.CooldownElapsed,
                demandId, area.Kind, area.Kind));
        }
        return ready.AsReadOnly();
    }

    /// <summary>
    /// 捕获需求板运行态。当前可恢复的规格契约是 JarOrderSpecification；
    /// 遇到未定义快照格式的自定义规格时明确拒绝，不能静默丢失订单语义。
    /// </summary>
    public OrderDemandBoardSnapshot CaptureRuntimeState(SequentialIdGenerator demandIds)
    {
        ArgumentNullException.ThrowIfNull(demandIds);
        var maximumAssignedId = AllDemands.Select(item => item.Id).DefaultIfEmpty(0).Max();
        return new OrderDemandBoardSnapshot(
            Math.Max(demandIds.CurrentValue, maximumAssignedId),
            Passive.FulfillmentPolicy,
            Active.FulfillmentPolicy,
            CaptureArea(Passive),
            CaptureArea(Active));
    }

    /// <summary>
    /// 从需求板快照重建一个新需求板。调用方必须提供与世界词表匹配的 Vocabulary
    /// 和该世界专属的需求 ID 生成器；恢复只推进 ID 序列，不会复用已发出的 ID。
    /// </summary>
    public static OrderDemandBoard RestoreRuntimeState(
        OrderDemandBoardSnapshot snapshot,
        Vocabulary vocabulary,
        SequentialIdGenerator demandIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(demandIds);

        var board = new OrderDemandBoard(
            snapshot.PassiveFulfillmentPolicy,
            snapshot.ActiveFulfillmentPolicy);
        RestoreArea(board.Passive, snapshot.PassiveDemands, vocabulary);
        RestoreArea(board.Active, snapshot.ActiveDemands, vocabulary);
        demandIds.RestoreAtLeast(snapshot.DemandIdSequence);
        return board;
    }

    /// <summary>
    /// 将检查点装载到现有需求板实例，保留 SolveBook/Scheduler 对该板的对象引用。
    /// 仅允许区域策略相同；通过 Changed 事件让现有求解状态簿清除旧归属并登记新订单。
    /// </summary>
    public void RestoreRuntimeStateFrom(
        OrderDemandBoardSnapshot snapshot,
        Vocabulary vocabulary,
        SequentialIdGenerator demandIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentNullException.ThrowIfNull(demandIds);
        if (snapshot.PassiveFulfillmentPolicy != Passive.FulfillmentPolicy ||
            snapshot.ActiveFulfillmentPolicy != Active.FulfillmentPolicy)
            throw new InvalidOperationException("检查点需求区域成功策略与当前世界配置不一致");

        var staged = RestoreRuntimeState(snapshot, vocabulary, new SequentialIdGenerator());
        var oldPassive = Passive.Clear();
        var oldActive = Active.Clear();
        foreach (var demand in oldPassive)
            Changed?.Invoke(new OrderDemandBoardChange(
                OrderDemandBoardChangeKind.Removed, demand.Id, DemandAreaKind.Passive, null));
        foreach (var demand in oldActive)
            Changed?.Invoke(new OrderDemandBoardChange(
                OrderDemandBoardChangeKind.Removed, demand.Id, DemandAreaKind.Active, null));

        foreach (var demand in staged.Passive.Demands)
        {
            Passive.Add(demand);
            Changed?.Invoke(new OrderDemandBoardChange(
                OrderDemandBoardChangeKind.Added, demand.Id,
                null, DemandAreaKind.Passive));
        }
        foreach (var demand in staged.Active.Demands)
        {
            Active.Add(demand);
            Changed?.Invoke(new OrderDemandBoardChange(
                OrderDemandBoardChangeKind.Added, demand.Id,
                null, DemandAreaKind.Active));
        }

        demandIds.RestoreAtLeast(snapshot.DemandIdSequence);
    }

    private static IReadOnlyList<OrderDemandSnapshot> CaptureArea(OrderDemandArea area) =>
        Array.AsReadOnly(area.Demands.Select(demand =>
        {
            if (demand.Specification is not JarOrderSpecification specification)
                throw new NotSupportedException(
                    "需求板检查点暂不支持规格类型: " + demand.Specification.GetType().FullName);

            return new OrderDemandSnapshot(
                demand.Id,
                specification.Type,
                specification.Requirements.Select(item =>
                    new OrderRequirementSnapshot(item.Shape, item.Liquid)),
                demand.CooldownDurationLogicalMilli,
                demand.CooldownRemainingLogicalMilli);
        }).ToArray());

    private static void RestoreArea(
        OrderDemandArea area,
        IReadOnlyList<OrderDemandSnapshot> demands,
        Vocabulary vocabulary)
    {
        foreach (var item in demands)
        {
            var specification = new JarOrderSpecification(
                item.Requirements.Select(requirement => new Requirement
                {
                    Shape = requirement.Shape,
                    Liquid = requirement.Liquid
                }),
                item.Type,
                vocabulary);
            area.Add(OrderDemand.Restore(
                item.Id,
                specification,
                item.CooldownDurationLogicalMilli,
                item.CooldownRemainingLogicalMilli));
        }
    }

    private OrderDemandArea AreaOf(OrderDemand demand) =>
        Passive.Demands.Contains(demand) ? Passive : Active;

    private OrderDemandArea GetArea(DemandAreaKind area) => area switch
    {
        DemandAreaKind.Passive => Passive,
        DemandAreaKind.Active => Active,
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };
}

public enum DemandFulfillmentResult
{
    NotFound,
    RemovedFromArea,
    RetainedInArea
}


/// <summary>需求板向外发布的结构变化；不携带玩法规格内容。</summary>
public sealed record OrderDemandBoardChange(
    OrderDemandBoardChangeKind Kind,
    int DemandId,
    DemandAreaKind? PreviousArea,
    DemandAreaKind? CurrentArea);

public enum OrderDemandBoardChangeKind
{
    Added,
    Removed,
    Moved,
    PassiveOrderChanged,
    CooldownStarted,
    CooldownElapsed,
    FulfilledRetained
}

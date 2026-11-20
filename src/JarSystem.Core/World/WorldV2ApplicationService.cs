using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 的外部应用入口。
///
/// Host 管世界实例的生命周期与互斥；ApplicationService 管外部命令/查询的语义，
/// 并把 Core 内部对象转换成脱离式 DTO。调用方不得直接接触 WorldV2Context。
/// 本服务不拥有 Host；Host 的创建与释放由组合根负责。
/// </summary>
public sealed class WorldV2ApplicationService
{
    private readonly WorldV2HostService _host;

    public WorldV2ApplicationService(WorldV2HostService host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>创建或替换世界。配置在 Factory 内完成校验和复制。</summary>
    public void CreateOrReplaceWorld(WorldV2Options options) => _host.Replace(options);

    /// <summary>
    /// 推进世界并运行一轮调度。deltaWorldTimeMilli 的 1000 表示 1 个世界时间单位。
    /// 调度顺序由 WorldOrderScheduler 决定；这里仅将结果复制成 DTO。
    /// </summary>
    public WorldV2AdvanceResultDto AdvanceWorld(long deltaWorldTimeMilli)
    {
        WorldV2AdvanceResultDto? result = null;
        _host.ExecuteExclusive(context =>
        {
            var step = context.Scheduler.AdvanceWorldAndSchedule(deltaWorldTimeMilli);
            var resource = step.WorldStep.Resources;
            result = new WorldV2AdvanceResultDto(
                step.WorldStep.LogicalTimeDeltaMilli,
                resource.ProducedJarCount,
                CopyList(resource.ProducedLiquids.Select(x => new WorldV2LiquidAmountDto(x.Liquid, x.Amount))),
                resource.FlowedJarCount,
                CopyDictionary(resource.FlowedLiquids),
                CopyList(step.Scheduling.StartedDemandIds),
                step.Scheduling.VisitedDemandCount,
                context.Runtime.IsFaulted,
                context.Runtime.FaultReason);
        });
        return result!;
    }

    /// <summary>添加罐子订单。输入仅包含玩法数据，不接受外部构造的领域对象。</summary>
    public int AddJarDemand(WorldV2AddJarDemandRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.Requirements == null) throw new ArgumentException("订单需求不能为空", nameof(request));
        int demandId = 0;

        _host.ExecuteExclusive(context =>
        {
            var vocabulary = new Vocabulary(
                context.Resources.Producers.Generators.Keys,
                context.Resources.Inventory.LiquidWarehouse.Keys);
            var requirements = request.Requirements.Select(item =>
            {
                if (item == null) throw new ArgumentException("订单需求项不能为 null", nameof(request));
                return new Requirement { Shape = item.Shape, Liquid = item.Liquid };
            }).ToList();

            var specification = new JarOrderSpecification(
                requirements, request.Type, vocabulary: vocabulary);
            var demand = new OrderDemand(specification, request.CooldownDurationLogicalMilli);
            context.DemandBoard.AddDemand(demand, request.Area);
            demandId = demand.Id;
        });

        return demandId;
    }

    /// <summary>将已登记订单移动到主动区或被动区；返回 false 表示订单不存在。</summary>
    public bool MoveDemand(int demandId, DemandAreaKind targetArea)
    {
        var moved = false;
        _host.ExecuteExclusive(context => moved = context.DemandBoard.MoveToArea(demandId, targetArea));
        return moved;
    }

    /// <summary>显式启动主动区域订单。被动订单不会通过此入口启动。</summary>
    public WorldV2StartDemandResultDto StartActiveDemand(int demandId)
    {
        WorldV2StartDemandResultDto? result = null;
        _host.ExecuteExclusive(context =>
        {
            var start = context.Scheduler.TryStartActiveDemand(demandId);
            result = new WorldV2StartDemandResultDto(start.Success, start.Reason);
        });
        return result!;
    }

    /// <summary>
    /// 请求替换主/从工作台。订单中断由 World v2 Runtime 负责；当前应用服务没有外部订单系统，
    /// 因此传入无副作用适配器，Runtime 随后仍会中断它自己持有的所有执行会话。
    /// </summary>
    public WorldV2WorkshopReplacementResultDto BeginWorkshopReplacement(
        WorkshopRole role, string targetWorkshopName, long phaseDurationLogicalMilli)
    {
        if (!Enum.IsDefined(role))
            return new WorldV2WorkshopReplacementResultDto(false, "工作台岗位类型无效", null);
        if (string.IsNullOrWhiteSpace(targetWorkshopName))
            return new WorldV2WorkshopReplacementResultDto(false, "目标工作台名称不能为空", null);
        if (phaseDurationLogicalMilli <= 0)
            return new WorldV2WorkshopReplacementResultDto(false, "替换阶段时长必须大于零", null);

        WorldV2WorkshopReplacementResultDto? result = null;
        _host.ExecuteExclusive(context =>
        {
            var target = context.Roster.Find(targetWorkshopName);
            if (target == null)
            {
                result = new WorldV2WorkshopReplacementResultDto(
                    false, "找不到目标工作台: " + targetWorkshopName, null);
                return;
            }

            try
            {
                var begin = context.Runtime.BeginReplacement(
                    role, target, NoExternalOrderInterruption.Instance, phaseDurationLogicalMilli);
                result = new WorldV2WorkshopReplacementResultDto(
                    begin.Success, begin.Error, ToReplacementDto(context.Runtime.ActiveReplacement));
            }
            catch (ArgumentException ex)
            {
                result = new WorldV2WorkshopReplacementResultDto(false, ex.Message, null);
            }
            catch (InvalidOperationException ex)
            {
                result = new WorldV2WorkshopReplacementResultDto(false, ex.Message,
                    ToReplacementDto(context.Runtime.ActiveReplacement));
            }
        });
        return result!;
    }


    /// <summary>
    /// 捕获当前世界的展示快照。所有集合和元素均为复制值，不包含 Core 可变实体引用。
    /// 该 DTO 不是 Solver 使用的 WorldSnapshot。
    /// </summary>
    public WorldV2SnapshotDto GetWorldSnapshot()
    {
        WorldV2SnapshotDto? snapshot = null;
        _host.ExecuteExclusive(context =>
        {
            var clock = context.Runtime.Clock;
            var master = context.Roster.Master;
            var slave = context.Roster.Slave;
            var demands = context.DemandBoard.AllDemands.Select(demand =>
            {
                var area = context.DemandBoard.Passive.Demands.Any(x => x.Id == demand.Id)
                    ? DemandAreaKind.Passive
                    : DemandAreaKind.Active;
                var requirements = demand.Specification is JarOrderSpecification jarSpec
                    ? jarSpec.Requirements.Select(x => new WorldV2RequirementDto(x.Shape, x.Liquid)).ToArray()
                    : Array.Empty<WorldV2RequirementDto>();
                var type = demand.Specification is JarOrderSpecification typed
                    ? typed.Type
                    : demand.Specification.GetType().Name;
                return new WorldV2DemandDto(
                    demand.Id, area, type, CopyList(requirements),
                    demand.CooldownDurationLogicalMilli,
                    demand.CooldownRemainingLogicalMilli,
                    demand.IsCoolingDown);
            }).ToArray();

            var sessions = context.Runtime.ActiveExecutionSessions.Select(session =>
                new WorldV2ExecutionSessionDto(
                    session.DemandId, session.Stage.ToString(),
                    session.UnitDurationLogicalMilli, session.UnitElapsedLogicalMilli,
                    session.CompletedMasterSteps, session.CompletedSlaveSteps,
                    session.IsTerminal, session.Succeeded, session.Reason)).ToArray();

            snapshot = new WorldV2SnapshotDto(
                clock.WorldTimeMilli,
                clock.LogicalTimeMilli,
                clock.SpeedRatioMilli,
                clock.IsPaused,
                context.Runtime.IsFaulted,
                context.Runtime.FaultReason,
                CopyList(context.Resources.Inventory.Jars.Select(ToJarDto)),
                CopyDictionary(context.Resources.Inventory.LiquidWarehouse),
                ToWorkshopDto(master),
                ToWorkshopDto(slave),
                CopyList(demands),
                CopyList(sessions),
                context.Reservations.Count,
                ToReplacementDto(context.Runtime.ActiveReplacement));
        });
        return snapshot!;
    }

    private static WorldV2WorkshopReplacementDto? ToReplacementDto(
        WorkshopReplacementProcess? process)
    {
        if (process == null) return null;
        return new WorldV2WorkshopReplacementDto(
            process.Role,
            process.IsFinished,
            process.Succeeded,
            process.InterruptionCommitted,
            process.InterruptionOutcomeUncertain,
            process.CompletedPhases,
            process.FailureReason,
            process.CommitWarning);
    }

    private sealed class NoExternalOrderInterruption : IOrderInterruptionPort
    {
        public static NoExternalOrderInterruption Instance { get; } = new();
        public (bool Success, string? Error) InterruptForWorkshopReplacement() => (true, null);
    }

    private static WorldV2WorkshopDto? ToWorkshopDto(Workshop? workshop)
    {
        if (workshop == null) return null;
        return new WorldV2WorkshopDto(
            workshop.Name,
            workshop.MaxJars,
            workshop.MaxLiquids,
            CopyList(workshop.BaseMarks),
            CopyList(workshop.AdditionalMarks),
            CopyList(workshop.Ban),
            CopyList(workshop.Jars.Select(ToJarDto)),
            CopyDictionary(workshop.LiquidStorage));
    }

    private static WorldV2JarDto ToJarDto(Jar jar) =>
        new(jar.Id, jar.Shape, jar.Liquid, jar.IsMarked);

    private static IReadOnlyList<T> CopyList<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private static IReadOnlyDictionary<string, int> CopyDictionary(
        IEnumerable<KeyValuePair<string, int>> values) =>
        new ReadOnlyDictionary<string, int>(
            values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
}

public sealed record WorldV2RequirementInput(string Shape, string? Liquid);

public sealed record WorldV2AddJarDemandRequest(
    IReadOnlyList<WorldV2RequirementInput> Requirements,
    string Type,
    DemandAreaKind Area,
    long CooldownDurationLogicalMilli);

public sealed record WorldV2RequirementDto(string Shape, string? Liquid);
public sealed record WorldV2JarDto(int? Id, string Shape, string? Liquid, bool IsMarked);
public sealed record WorldV2LiquidAmountDto(string Liquid, int Amount);

public sealed record WorldV2WorkshopDto(
    string Name,
    int MaxJars,
    int MaxLiquids,
    IReadOnlyList<string> BaseMarks,
    IReadOnlyList<string> AdditionalMarks,
    IReadOnlyList<string> Ban,
    IReadOnlyList<WorldV2JarDto> Jars,
    IReadOnlyDictionary<string, int> Liquids);

public sealed record WorldV2DemandDto(
    int Id,
    DemandAreaKind Area,
    string SpecificationType,
    IReadOnlyList<WorldV2RequirementDto> Requirements,
    long CooldownDurationLogicalMilli,
    long CooldownRemainingLogicalMilli,
    bool IsCoolingDown);

public sealed record WorldV2ExecutionSessionDto(
    int DemandId,
    string Stage,
    long UnitDurationLogicalMilli,
    long UnitElapsedLogicalMilli,
    int CompletedMasterSteps,
    int CompletedSlaveSteps,
    bool IsTerminal,
    bool Succeeded,
    string? Reason);

public sealed record WorldV2AdvanceResultDto(
    long LogicalTimeDeltaMilli,
    int ProducedJarCount,
    IReadOnlyList<WorldV2LiquidAmountDto> ProducedLiquids,
    int FlowedJarCount,
    IReadOnlyDictionary<string, int> FlowedLiquids,
    IReadOnlyList<int> StartedDemandIds,
    int VisitedDemandCount,
    bool RuntimeFaulted,
    string? FaultReason);

public sealed record WorldV2StartDemandResultDto(bool Success, string? Reason);

public sealed record WorldV2WorkshopReplacementResultDto(
    bool Success,
    string? Error,
    WorldV2WorkshopReplacementDto? Replacement);

public sealed record WorldV2WorkshopReplacementDto(
    WorkshopRole Role,
    bool IsFinished,
    bool Succeeded,
    bool InterruptionCommitted,
    bool InterruptionOutcomeUncertain,
    int CompletedPhases,
    string? FailureReason,
    string? CommitWarning);

public sealed record WorldV2SnapshotDto(
    long WorldTimeMilli,
    long LogicalTimeMilli,
    long SpeedRatioMilli,
    bool IsPaused,
    bool RuntimeFaulted,
    string? FaultReason,
    IReadOnlyList<WorldV2JarDto> ResourceJars,
    IReadOnlyDictionary<string, int> ResourceLiquids,
    WorldV2WorkshopDto? Master,
    WorldV2WorkshopDto? Slave,
    IReadOnlyList<WorldV2DemandDto> Demands,
    IReadOnlyList<WorldV2ExecutionSessionDto> ActiveSessions,
    int ReservationCount,
    WorldV2WorkshopReplacementDto? Replacement);

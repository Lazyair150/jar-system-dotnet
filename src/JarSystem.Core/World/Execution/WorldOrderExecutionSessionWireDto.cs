using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World.Execution;

/// <summary>
/// JSON 专用的活动订单会话表示。它只保存不可变执行计划和执行游标，
/// 不引用 Workshop、Admission 或任何运行时服务对象。
/// </summary>
public sealed class WorldOrderExecutionSessionWireDto
{
    public int DemandId { get; init; }
    public string Stage { get; init; } = string.Empty;
    public int CompletedMasterSteps { get; init; }
    public int CompletedSlaveSteps { get; init; }
    public long UnitElapsedLogicalMilli { get; init; }
    public long UnitDurationLogicalMilli { get; init; }
    public bool SlaveTransferCommitted { get; init; }
    public string? Reason { get; init; }
    public WorldOrderResourceManifestWireDto Manifest { get; init; } = new();
    public WorldOrderSolvePlanWireDto Plan { get; init; } = new();

    public static WorldOrderExecutionSessionWireDto FromSnapshot(
        WorldOrderExecutionSessionProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new WorldOrderExecutionSessionWireDto
        {
            DemandId = snapshot.DemandId,
            Stage = snapshot.Stage.ToString(),
            CompletedMasterSteps = snapshot.CompletedMasterSteps,
            CompletedSlaveSteps = snapshot.CompletedSlaveSteps,
            UnitElapsedLogicalMilli = snapshot.UnitElapsedLogicalMilli,
            UnitDurationLogicalMilli = snapshot.UnitDurationLogicalMilli,
            SlaveTransferCommitted = snapshot.SlaveTransferCommitted,
            Reason = snapshot.Reason,
            Manifest = WorldOrderResourceManifestWireDto.FromManifest(snapshot.Manifest),
            Plan = WorldOrderSolvePlanWireDto.FromPlan(snapshot.Plan)
        };
    }

    public WorldOrderExecutionSessionProgressSnapshot ToSnapshot()
    {
        Validate();
        var manifest = Manifest.ToManifest();
        var plan = Plan.ToPlan();
        return new WorldOrderExecutionSessionProgressSnapshot(
            DemandId,
            Enum.Parse<WorldOrderExecutionStage>(Stage, false),
            CompletedMasterSteps,
            CompletedSlaveSteps,
            UnitElapsedLogicalMilli,
            UnitDurationLogicalMilli,
            SlaveTransferCommitted,
            Reason,
            manifest,
            plan);
    }

    public void Validate()
    {
        if (DemandId <= 0) throw new InvalidOperationException("会话 Wire DTO 的 DemandId 无效。");
        if (!Enum.TryParse<WorldOrderExecutionStage>(Stage, false, out var stage) ||
            !Enum.IsDefined(stage) || stage is WorldOrderExecutionStage.Completed or
                WorldOrderExecutionStage.Failed or WorldOrderExecutionStage.Interrupted or
                WorldOrderExecutionStage.Cancelled)
            throw new InvalidOperationException("会话 Wire DTO 必须描述有效的活动阶段。");
        if (UnitDurationLogicalMilli <= 0 || UnitElapsedLogicalMilli < 0 ||
            UnitElapsedLogicalMilli >= UnitDurationLogicalMilli)
            throw new InvalidOperationException("会话 Wire DTO 的逻辑时间游标无效。");
        if (Manifest == null || Plan == null)
            throw new InvalidOperationException("会话 Wire DTO 缺少 Manifest 或 Plan。");
        Manifest.Validate();
        Plan.Validate();
        var transferJarIdsMatch = Manifest.TransferJarIds.OrderBy(x => x)
            .SequenceEqual(Plan.TransferJarIds.OrderBy(x => x));
        // Manifest 以路径前缀峰值计算最低供给，可能保留值为 0 的液种键；
        // Plan 的转移账本可以省略这些键。缺失键与显式 0 在资源语义上等价。
        var transferLiquidsMatch = LiquidBalancesEqual(Manifest.TransferLiquids, Plan.TransferLiquids);
        if (Manifest.DemandId != DemandId || Manifest.IsPartitioned != Plan.IsPartitioned ||
            !transferJarIdsMatch || !transferLiquidsMatch)
            throw new InvalidOperationException(
                "会话 Wire DTO 的 Manifest 与 Plan 不一致：" +
                $"DemandId={DemandId}/{Manifest.DemandId}，Partitioned={Manifest.IsPartitioned}/{Plan.IsPartitioned}，" +
                $"TransferJarIds={string.Join(",", Manifest.TransferJarIds)}/{string.Join(",", Plan.TransferJarIds)}，" +
                $"TransferLiquids={string.Join(",", Manifest.TransferLiquids.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value))}/" +
                string.Join(",", Plan.TransferLiquids.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value)) + "。");
        ExecutionProgressInvariants.Validate(
            stage,
            CompletedMasterSteps,
            Plan.MasterSteps.Length,
            CompletedSlaveSteps,
            Plan.SlaveSteps.Length,
            Plan.IsPartitioned,
            SlaveTransferCommitted,
            Reason);
    }

    private static bool LiquidBalancesEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        var keys = left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal);
        return keys.All(key => left.GetValueOrDefault(key) == right.GetValueOrDefault(key));
    }
}

public sealed class WorldOrderResourceManifestWireDto
{
    public int DemandId { get; init; }
    public string WorldVersion { get; init; } = string.Empty;
    public bool IsPartitioned { get; init; }
    public int[] MasterJarIds { get; init; } = Array.Empty<int>();
    public int[] TransferJarIds { get; init; } = Array.Empty<int>();
    public int[] LockedJarIds { get; init; } = Array.Empty<int>();
    public Dictionary<string, int> MasterInitialLiquidAmounts { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> TransferLiquids { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> MasterFinalLiquidBalances { get; init; } = new(StringComparer.Ordinal);
    public int MasterStagingPeakCapacity { get; init; }
    public int SlaveStagingPeakCapacity { get; init; }

    internal static WorldOrderResourceManifestWireDto FromManifest(WorldOrderResourceManifest value) => new()
    {
        DemandId = value.DemandId,
        WorldVersion = value.WorldVersion,
        IsPartitioned = value.IsPartitioned,
        MasterJarIds = value.MasterJarIds.ToArray(),
        TransferJarIds = value.TransferJarIds.ToArray(),
        LockedJarIds = value.LockedJarIds.ToArray(),
        MasterInitialLiquidAmounts = Copy(value.MasterInitialLiquidAmounts),
        TransferLiquids = Copy(value.TransferLiquids),
        MasterFinalLiquidBalances = Copy(value.MasterFinalLiquidBalances),
        MasterStagingPeakCapacity = value.MasterStagingPeakCapacity,
        SlaveStagingPeakCapacity = value.SlaveStagingPeakCapacity
    };

    internal void Validate()
    {
        if (DemandId <= 0 || string.IsNullOrWhiteSpace(WorldVersion))
            throw new InvalidOperationException("Manifest Wire DTO 缺少订单身份或世界版本。");
        if (MasterJarIds == null || TransferJarIds == null || LockedJarIds == null ||
            MasterInitialLiquidAmounts == null || TransferLiquids == null || MasterFinalLiquidBalances == null)
            throw new InvalidOperationException("Manifest Wire DTO 包含 null 集合。");
        // These sets may overlap when a jar is used in the master stage and then handed off.
        if (MasterJarIds.Any(x => x <= 0) || TransferJarIds.Any(x => x <= 0) ||
            MasterJarIds.Distinct().Count() != MasterJarIds.Length ||
            TransferJarIds.Distinct().Count() != TransferJarIds.Length ||
            LockedJarIds.Distinct().Count() != LockedJarIds.Length ||
            !LockedJarIds.ToHashSet().SetEquals(MasterJarIds.Concat(TransferJarIds)))
            throw new InvalidOperationException("Manifest Wire DTO 的罐子身份集合无效。");
        ValidateLiquidMap(MasterInitialLiquidAmounts);
        ValidateLiquidMap(TransferLiquids);
        ValidateLiquidMap(MasterFinalLiquidBalances);
        if (MasterStagingPeakCapacity < 0 || SlaveStagingPeakCapacity < 0 ||
            (!IsPartitioned && (TransferJarIds.Length != 0 || TransferLiquids.Count != 0 || SlaveStagingPeakCapacity != 0)))
            throw new InvalidOperationException("Manifest Wire DTO 的主从资源边界无效。");
    }

    internal WorldOrderResourceManifest ToManifest()
    {
        Validate();
        return WorldOrderResourceManifest.RestoreFromWire(
            DemandId, WorldVersion, IsPartitioned,
            MasterJarIds, TransferJarIds,
            MasterInitialLiquidAmounts, TransferLiquids,
            MasterFinalLiquidBalances,
            MasterStagingPeakCapacity, SlaveStagingPeakCapacity);
    }

    private static Dictionary<string, int> Copy(IReadOnlyDictionary<string, int> source) =>
        source.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    private static void ValidateLiquidMap(IReadOnlyDictionary<string, int> map)
    {
        if (map.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value < 0))
            throw new InvalidOperationException("Manifest Wire DTO 的液体余额无效。");
    }
}

public sealed class WorldOrderSolvePlanWireDto
{
    public bool IsPartitioned { get; init; }
    public int[] TransferJarIds { get; init; } = Array.Empty<int>();
    public Dictionary<string, int> TransferLiquids { get; init; } = new(StringComparer.Ordinal);
    public WorldPlanStepWireDto[] MasterSteps { get; init; } = Array.Empty<WorldPlanStepWireDto>();
    public WorldPlanStepWireDto[] SlaveSteps { get; init; } = Array.Empty<WorldPlanStepWireDto>();

    internal static WorldOrderSolvePlanWireDto FromPlan(WorldOrderSolvePlan value) => new()
    {
        IsPartitioned = value.IsPartitioned,
        TransferJarIds = value.TransferJarIds.ToArray(),
        TransferLiquids = value.TransferLiquids.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        MasterSteps = value.MasterSteps.Select(WorldPlanStepWireDto.FromStep).ToArray(),
        SlaveSteps = value.SlaveSteps.Select(WorldPlanStepWireDto.FromStep).ToArray()
    };

    internal WorldOrderSolvePlan ToPlan()
    {
        Validate();
        return WorldOrderSolvePlan.RestoreFromWire(
            IsPartitioned, TransferJarIds, TransferLiquids,
            MasterSteps.Select(x => x.ToStep()).ToArray(),
            SlaveSteps.Select(x => x.ToStep()).ToArray());
    }

    internal void Validate()
    {
        if (TransferJarIds == null || TransferLiquids == null || MasterSteps == null || SlaveSteps == null)
            throw new InvalidOperationException("Plan Wire DTO 包含 null 集合。");
        if (TransferJarIds.Any(x => x <= 0) || TransferJarIds.Distinct().Count() != TransferJarIds.Length ||
            TransferLiquids.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value < 0))
            throw new InvalidOperationException("Plan Wire DTO 的转移资源无效。");
        if (!IsPartitioned && (TransferJarIds.Length != 0 || TransferLiquids.Count != 0 || SlaveSteps.Length != 0))
            throw new InvalidOperationException("A 类 Plan Wire DTO 不得包含从台转移数据。");
        foreach (var step in MasterSteps.Concat(SlaveSteps)) step.Validate();
    }
}

public sealed class WorldPlanStepWireDto
{
    public int Step { get; init; }
    public string? RequirementShape { get; init; }
    public string? RequirementLiquid { get; init; }
    public WorldPlanOperationWireDto? Operation { get; init; }
    public WorldPlanJarStateWireDto[] Before { get; init; } = Array.Empty<WorldPlanJarStateWireDto>();
    public WorldPlanJarStateWireDto[] After { get; init; } = Array.Empty<WorldPlanJarStateWireDto>();
    public Dictionary<string, int> AfterLiquids { get; init; } = new(StringComparer.Ordinal);

    internal static WorldPlanStepWireDto FromStep(WorldPlanStep value) => new()
    {
        Step = value.Step,
        RequirementShape = value.RequirementShape,
        RequirementLiquid = value.RequirementLiquid,
        Operation = value.Operation == null ? null : WorldPlanOperationWireDto.FromOperation(value.Operation),
        Before = value.Before.Select(WorldPlanJarStateWireDto.FromState).ToArray(),
        After = value.After.Select(WorldPlanJarStateWireDto.FromState).ToArray(),
        AfterLiquids = value.AfterLiquids.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
    };

    internal WorldPlanStep ToStep()
    {
        Validate();
        return new WorldPlanStep(
            Step, RequirementShape, RequirementLiquid,
            Operation!.ToOperation(),
            Array.AsReadOnly(Before.Select(x => x.ToState()).ToArray()),
            Array.AsReadOnly(After.Select(x => x.ToState()).ToArray()),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
                new Dictionary<string, int>(AfterLiquids, StringComparer.Ordinal)));
    }

    internal void Validate()
    {
        if (Operation == null || Before == null || After == null || AfterLiquids == null ||
            Before.Length == 0 || Before.Length != After.Length ||
            Operation.JarIndex is not int index || index < 0 || index >= Before.Length)
            throw new InvalidOperationException("Plan step Wire DTO 缺少操作或 Before/After 不匹配。");
        if (Before.Any(x => x == null) || After.Any(x => x == null) ||
            Before.Select(x => x.Id).Distinct().Count() != Before.Length ||
            Before.Where((x, i) => x.Id != After[i].Id || x.MasterOrigin != After[i].MasterOrigin).Any())
            throw new InvalidOperationException("Plan step Wire DTO 的罐子身份/来源在前后状态间不一致。");
        if (AfterLiquids.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value < 0))
            throw new InvalidOperationException("Plan step Wire DTO 的液体余额无效。");
        foreach (var state in Before.Concat(After)) state.Validate();
        Operation.Validate();
    }
}

public sealed class WorldPlanOperationWireDto
{
    public string? Type { get; init; }
    public string? Description { get; init; }
    public int? JarIndex { get; init; }
    public string? TargetShape { get; init; }
    public string? TargetLiquid { get; init; }
    public string? CostLiquid { get; init; }
    public Dictionary<string, int> Consumed { get; init; } = new(StringComparer.Ordinal);
    public bool FullReshape { get; init; }
    public string? RuleId { get; init; }
    public int? RuleVersion { get; init; }

    internal static WorldPlanOperationWireDto FromOperation(WorldPlanOperation value) => new()
    {
        Type = value.Type, Description = value.Description, JarIndex = value.JarIndex,
        TargetShape = value.TargetShape, TargetLiquid = value.TargetLiquid,
        CostLiquid = value.CostLiquid,
        Consumed = value.Consumed.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
        FullReshape = value.FullReshape,
        RuleId = value.RuleId,
        RuleVersion = value.RuleVersion
    };

    internal WorldPlanOperation ToOperation()
    {
        Validate();
        return new WorldPlanOperation(
            Type, Description, JarIndex, TargetShape, TargetLiquid, CostLiquid,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
                new Dictionary<string, int>(Consumed, StringComparer.Ordinal)),
            FullReshape)
        {
            RuleId = RuleId,
            RuleVersion = RuleVersion
        };
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Type) || JarIndex is < 0 || Consumed == null ||
            Consumed.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value < 0) ||
            (RuleId == null) != (RuleVersion == null) ||
            (RuleId != null && (string.IsNullOrWhiteSpace(RuleId) || RuleVersion is not > 0)))
            throw new InvalidOperationException("Plan operation Wire DTO 无效。");
    }
}

public sealed class WorldPlanJarStateWireDto
{
    public int? Id { get; init; }
    public string Shape { get; init; } = string.Empty;
    public string? Liquid { get; init; }
    public bool IsMarked { get; init; }
    public bool MasterOrigin { get; init; }

    internal static WorldPlanJarStateWireDto FromState(WorldPlanJarState value) => new()
    {
        Id = value.Id, Shape = value.Shape, Liquid = value.Liquid,
        IsMarked = value.IsMarked, MasterOrigin = value.MasterOrigin
    };

    internal WorldPlanJarState ToState()
    {
        Validate();
        return new WorldPlanJarState(Id, Shape, Liquid, IsMarked, MasterOrigin);
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Shape) || (Id.HasValue && Id.Value <= 0))
            throw new InvalidOperationException("Plan jar state Wire DTO 无效。");
    }
}

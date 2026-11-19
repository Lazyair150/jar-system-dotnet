using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Orders;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 一份不可变的求解计划投影。它只包含操作描述与实体 ID，不持有真实 Jar/Workshop 引用。
/// 计划必须与外层 WorldOrderSolveResult 的 WorldVersion 一起使用。
/// </summary>
public sealed class WorldOrderSolvePlan
{
    public IReadOnlyList<WorldPlanStep> MasterSteps { get; }
    public IReadOnlyList<WorldPlanStep> SlaveSteps { get; }
    public IReadOnlyList<int> TransferJarIds { get; }
    public IReadOnlyDictionary<string, int> TransferLiquids { get; }
    public bool IsPartitioned { get; }

    private WorldOrderSolvePlan(
        IEnumerable<WorldPlanStep> masterSteps,
        IEnumerable<WorldPlanStep>? slaveSteps = null,
        IEnumerable<int>? transferJarIds = null,
        IReadOnlyDictionary<string, int>? transferLiquids = null,
        bool isPartitioned = false)
    {
        MasterSteps = Array.AsReadOnly(masterSteps.ToArray());
        SlaveSteps = Array.AsReadOnly((slaveSteps ?? Array.Empty<WorldPlanStep>()).ToArray());
        TransferJarIds = Array.AsReadOnly((transferJarIds ?? Array.Empty<int>()).ToArray());
        IsPartitioned = isPartitioned;
        TransferLiquids = new ReadOnlyDictionary<string, int>(
            transferLiquids == null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : transferLiquids.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
    }

    public static WorldOrderSolvePlan SinglePhase(IEnumerable<SolverStep> path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        return new WorldOrderSolvePlan(path.Select(WorldPlanStep.CopyFrom));
    }

    public static WorldOrderSolvePlan Partitioned(PartitionSearchOutcome outcome)
    {
        if (outcome == null) throw new ArgumentNullException(nameof(outcome));
        return new WorldOrderSolvePlan(
            outcome.MasterPath.Select(WorldPlanStep.CopyFrom),
            outcome.SlavePath.Select(WorldPlanStep.CopyFrom),
            outcome.TransferJarIds,
            outcome.TransferLiquids,
            isPartitioned: true);
    }
}

public sealed record WorldPlanStep(
    int Step,
    string? RequirementShape,
    string? RequirementLiquid,
    WorldPlanOperation? Operation,
    IReadOnlyList<WorldPlanJarState> Before,
    IReadOnlyList<WorldPlanJarState> After,
    IReadOnlyDictionary<string, int> AfterLiquids)
{
    internal static WorldPlanStep CopyFrom(SolverStep source) => new(
        source.Step,
        source.Requirement?.Shape,
        source.Requirement?.Liquid,
        source.Operation == null ? null : WorldPlanOperation.CopyFrom(source.Operation),
        Array.AsReadOnly((source.Before ?? new List<JarState>()).Select(WorldPlanJarState.CopyFrom).ToArray()),
        Array.AsReadOnly((source.After ?? new List<JarState>()).Select(WorldPlanJarState.CopyFrom).ToArray()),
        new ReadOnlyDictionary<string, int>(
            source.AfterLiquids == null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(source.AfterLiquids, StringComparer.Ordinal)));
}

public sealed record WorldPlanOperation(
    string? Type,
    string? Description,
    int? JarIndex,
    string? TargetShape,
    string? TargetLiquid,
    string? CostLiquid,
    IReadOnlyDictionary<string, int> Consumed,
    bool FullReshape)
{
    internal static WorldPlanOperation CopyFrom(SolverOperation source) => new(
        source.Type,
        source.Description,
        source.JarIndex,
        source.TargetShape,
        source.TargetLiquid,
        source.CostLiquid,
        new ReadOnlyDictionary<string, int>(
            source.Consumed == null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(source.Consumed, StringComparer.Ordinal)),
        source.FullReshape);
}

public sealed record WorldPlanJarState(
    int? Id,
    string Shape,
    string? Liquid,
    bool IsMarked,
    bool MasterOrigin)
{
    internal static WorldPlanJarState CopyFrom(JarState source) =>
        new(source.Id, source.Shape, source.Liquid, source.IsMarked, source.MasterOrigin);
}

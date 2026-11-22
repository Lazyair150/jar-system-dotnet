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
        ValidateSingleOperationPerJar(MasterSteps.Concat(SlaveSteps));
    }

    private static void ValidateSingleOperationPerJar(IEnumerable<WorldPlanStep> steps)
    {
        var operatedJarIds = new HashSet<int>();
        foreach (var step in steps)
        {
            if (step.Operation is not { } operation) continue;
            if ((operation.RuleId == null) != (operation.RuleVersion == null) ||
                (operation.RuleId != null &&
                 (string.IsNullOrWhiteSpace(operation.RuleId) || operation.RuleVersion is not > 0)))
                throw new ArgumentException("规则身份必须同时包含非空 RuleId 和正数 RuleVersion");
            if (operation.JarIndex is not int jarIndex) continue;
            if (jarIndex < 0 || jarIndex >= step.Before.Count ||
                step.Before.Count != step.After.Count)
                throw new ArgumentException("订单步骤的 JarIndex 或 Before/After 快照无效");

            var before = step.Before[jarIndex];
            var after = step.After[jarIndex];
            if (before.Id is not > 0 || after.Id != before.Id)
                throw new ArgumentException("订单操作必须保持目标 Jar 的稳定身份");
            if (before.IsMarked)
                throw new ArgumentException("已标记 Jar 不能再次参与本订单操作：" + before.Id);
            if (!after.IsMarked)
                throw new ArgumentException("订单操作必须在输出状态中标记 Jar：" + before.Id);
            if (!operatedJarIds.Add(before.Id.Value))
                throw new ArgumentException("同一 Jar 在一次订单实现中只能操作一次：" + before.Id);
        }
    }

    internal static WorldOrderSolvePlan RestoreFromWire(
        bool isPartitioned,
        IEnumerable<int> transferJarIds,
        IReadOnlyDictionary<string, int> transferLiquids,
        IEnumerable<WorldPlanStep> masterSteps,
        IEnumerable<WorldPlanStep> slaveSteps)
    {
        if (transferJarIds == null || transferLiquids == null ||
            masterSteps == null || slaveSteps == null)
            throw new ArgumentNullException("Wire plan collections cannot be null.");
        var jars = transferJarIds.ToArray();
        var master = masterSteps.ToArray();
        var slave = slaveSteps.ToArray();
        if (jars.Any(x => x <= 0) || jars.Distinct().Count() != jars.Length ||
            transferLiquids.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value < 0))
            throw new InvalidOperationException("Wire plan transfer resources are invalid.");
        if (!isPartitioned && (jars.Length != 0 || transferLiquids.Count != 0 || slave.Length != 0))
            throw new InvalidOperationException("A-class wire plan cannot contain slave resources.");
        return new WorldOrderSolvePlan(master, slave, jars, transferLiquids, isPartitioned);
    }

    /// <summary>
    /// Creates a plan from already materialized, ordered solver paths.
    /// This is the narrow bridge for the symbolic-program materializer; it does not
    /// perform rule solving or resource replay. Callers must validate the result with
    /// WorldOrderResourceManifest.Create before reporting the order as executable.
    /// </summary>
    public static WorldOrderSolvePlan FromMaterializedPaths(
        bool isPartitioned,
        IEnumerable<SolverStep> masterPath,
        IEnumerable<SolverStep>? slavePath = null,
        IEnumerable<int>? transferJarIds = null,
        IReadOnlyDictionary<string, int>? transferLiquids = null)
    {
        if (masterPath == null) throw new ArgumentNullException(nameof(masterPath));
        var master = masterPath.ToArray();
        var slave = (slavePath ?? Array.Empty<SolverStep>()).ToArray();
        var jars = (transferJarIds ?? Array.Empty<int>()).ToArray();
        var liquids = transferLiquids ??
            new Dictionary<string, int>(StringComparer.Ordinal);

        if (master.Any(x => x == null) || slave.Any(x => x == null))
            throw new ArgumentException("Materialized solver paths cannot contain null steps.");
        if (jars.Any(x => x <= 0) || jars.Distinct().Count() != jars.Length)
            throw new ArgumentException("Materialized transfer Jar IDs must be positive and unique.", nameof(transferJarIds));
        if (liquids.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value < 0))
            throw new ArgumentException("Materialized transfer liquid amounts are invalid.", nameof(transferLiquids));
        if (!isPartitioned && (slave.Length != 0 || jars.Length != 0 || liquids.Count != 0))
            throw new ArgumentException("A-class materialized paths cannot contain Slave steps or transfer resources.");

        return new WorldOrderSolvePlan(
            master.Select(WorldPlanStep.CopyFrom),
            slave.Select(WorldPlanStep.CopyFrom),
            jars,
            liquids,
            isPartitioned);
    }

    public static WorldOrderSolvePlan SinglePhase(IEnumerable<SolverStep> path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        return FromMaterializedPaths(false, path);
    }

    public static WorldOrderSolvePlan Partitioned(PartitionSearchOutcome outcome)
    {
        if (outcome == null) throw new ArgumentNullException(nameof(outcome));
        return FromMaterializedPaths(
            true, outcome.MasterPath, outcome.SlavePath,
            outcome.TransferJarIds, outcome.TransferLiquids);
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
    public string? RuleId { get; init; }
    public int? RuleVersion { get; init; }

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
        source.FullReshape)
    {
        RuleId = source.RuleId,
        RuleVersion = source.RuleVersion
    };
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

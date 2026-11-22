using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.World;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// Joins physical materialization, ordered resource replay, immutable plan freezing,
/// and Manifest validation. This is the first point at which a concrete symbolic
/// branch may become a Feasible WorldOrderSolveResult.
/// </summary>
public sealed class WorldOrderConcretePlanAssemblyResult
{
    public WorldOrderSolveStatus Status { get; }
    public WorldOrderSolveResult? SolveResult { get; }
    public WorldOrderSolvePlan? CandidatePlan { get; }
    public WorldOrderResourceManifest? Manifest { get; }
    public WorldOrderConcreteMaterializationResult? Materialization { get; }
    public WorldOrderOrderedResourceReplayResult? ResourceReplay { get; }
    public string? Reason { get; }
    public bool IsFeasible => Status == WorldOrderSolveStatus.Feasible;

    internal WorldOrderConcretePlanAssemblyResult(
        WorldOrderSolveStatus status,
        WorldOrderSolveResult? solveResult,
        WorldOrderSolvePlan? candidatePlan,
        WorldOrderResourceManifest? manifest,
        WorldOrderConcreteMaterializationResult? materialization,
        WorldOrderOrderedResourceReplayResult? resourceReplay,
        string? reason)
    {
        Status = status;
        SolveResult = solveResult;
        CandidatePlan = candidatePlan;
        Manifest = manifest;
        Materialization = materialization;
        ResourceReplay = resourceReplay;
        Reason = reason;
    }
}

/// <summary>
/// Fail-closed assembler for one already enumerated symbolic branch. It does not
/// enumerate candidate alternatives or replace the current solver entry point.
/// </summary>
public static class WorldOrderConcretePlanAssembler
{
    public static WorldOrderConcretePlanAssemblyResult Assemble(
        int demandId,
        WorldOrderSymbolicOperationProgram program,
        WorldOrderPhysicalJarBindingResult binding,
        WorldSnapshot world,
        WorldOrderRuleCandidateProfile masterProfile,
        WorldOrderRuleCandidateProfile? slaveProfile = null,
        IEnumerable<string>? slaveRejectedLiquids = null)
    {
        if (demandId <= 0) throw new ArgumentOutOfRangeException(nameof(demandId));
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (binding == null) throw new ArgumentNullException(nameof(binding));
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (masterProfile == null) throw new ArgumentNullException(nameof(masterProfile));

        // Validate the exact candidate/profile membership and physical Jar state before
        // trusting any symbolic liquid effect in the resource replay. Otherwise a forged
        // candidate could manufacture an Infeasible result before profile validation.
        var identityMaterialization = WorldOrderConcreteOperationMaterializer.Materialize(
            program, binding, world, masterProfile, slaveProfile);
        if (!identityMaterialization.IsComplete)
        {
            var status = identityMaterialization.Status == WorldOrderConcreteMaterializationStatus.Infeasible
                ? WorldOrderSolveStatus.Infeasible
                : WorldOrderSolveStatus.Undetermined;
            return Failure(status, identityMaterialization.Reason,
                materialization: identityMaterialization);
        }

        var replay = WorldOrderOrderedResourceReplay.Replay(
            program, world, slaveRejectedLiquids, "concrete-" + demandId);
        if (!replay.IsComplete)
        {
            var status = replay.Status == WorldOrderOrderedResourceReplayStatus.Infeasible
                ? WorldOrderSolveStatus.Infeasible
                : WorldOrderSolveStatus.Undetermined;
            return Failure(status, replay.Reason,
                materialization: identityMaterialization, replay: replay);
        }

        var materialization = WorldOrderConcreteOperationMaterializer.Materialize(
            program, binding, world, masterProfile, slaveProfile, replay);
        if (!materialization.IsComplete)
        {
            var status = materialization.Status == WorldOrderConcreteMaterializationStatus.Infeasible
                ? WorldOrderSolveStatus.Infeasible
                : WorldOrderSolveStatus.Undetermined;
            return Failure(status, materialization.Reason,
                materialization: materialization, replay: replay);
        }

        if (program.IsBClass)
        {
            var expectedSlaveOrder = program.Nodes
                .Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule &&
                            x.WorkshopRole == program.SlaveRole)
                .OrderBy(x => x.Sequence)
                .Select(x => x.NodeId);
            if (replay.SlaveBoundaryGroup!.Requirements.Count(x =>
                    x.Kind == WorldOrderBoundaryResourceKind.JarState) != materialization.TransferJarIds.Count ||
                !replay.SlaveBoundaryGroup.SlaveOperationOrder.SequenceEqual(expectedSlaveOrder))
                return Failure(WorldOrderSolveStatus.Undetermined,
                    "资源边界组的 JarState 或从台顺序 witness 与具体交接/操作路径不一致",
                    materialization: materialization, replay: replay);
        }

        WorldOrderSolvePlan plan;
        try
        {
            plan = WorldOrderSolvePlan.FromMaterializedPaths(
                program.IsBClass,
                materialization.MasterPath,
                materialization.SlavePath,
                materialization.TransferJarIds,
                replay.TransferLiquids);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(WorldOrderSolveStatus.Undetermined,
                "具体操作路径无法冻结为合法订单计划：" + exception.Message,
                materialization: materialization, replay: replay);
        }
        var allSteps = materialization.MasterPath.Concat(materialization.SlavePath).ToArray();
        var directCount = allSteps.Count(x => x.Operation?.Type == "MATCH");
        var operationCount = allSteps.Length - directCount;
        var provisional = new WorldOrderSolveResult(
            demandId,
            world.Version,
            WorldOrderSolveStatus.Feasible,
            "9l_concrete_plan",
            directCount: directCount,
            operationCount: operationCount,
            plan: plan,
            ruleSetVersion: world.RuleSetVersion,
            workshopProfileVersion: world.WorkshopProfileVersion,
            resourceVersion: world.ResourceVersion,
            specificationVersion: masterProfile.SpecificationVersion);

        WorldOrderResourceManifest manifest;
        try
        {
            manifest = WorldOrderResourceManifest.Create(provisional, world);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            return Failure(WorldOrderSolveStatus.Undetermined,
                "Manifest 无法验证有序重放后的具体计划：" + exception.Message,
                plan, materialization, replay);
        }

        // Manifest's generic path simulation is intentionally cross-checked against
        // the policy-aware replay. If rejected/overflowed outputs were treated as
        // accepted by Manifest, the disagreement fails closed instead of returning Feasible.
        if (!DictionaryEqual(manifest.MasterInitialLiquidAmounts, replay.MasterInitialLiquids) ||
            !DictionaryEqual(manifest.MasterFinalLiquidBalances, replay.MasterFinalLiquids) ||
            !DictionaryEqual(manifest.TransferLiquids, replay.TransferLiquids) ||
            manifest.MasterStagingPeakCapacity != replay.MasterStagingPeakCapacity ||
            manifest.SlaveStagingPeakCapacity != replay.SlaveStagingPeakCapacity ||
            !manifest.TransferJarIds.OrderBy(x => x).SequenceEqual(
                materialization.TransferJarIds.OrderBy(x => x)))
        {
            return Failure(WorldOrderSolveStatus.Undetermined,
                "Manifest 与策略感知有序重放的液体/容量/交接证明不一致",
                plan, materialization, replay);
        }

        return new WorldOrderConcretePlanAssemblyResult(
            WorldOrderSolveStatus.Feasible,
            provisional,
            plan,
            manifest,
            materialization,
            replay,
            null);

        WorldOrderConcretePlanAssemblyResult Failure(
            WorldOrderSolveStatus status,
            string? reason,
            WorldOrderSolvePlan? candidatePlan = null,
            WorldOrderConcreteMaterializationResult? materialization = null,
            WorldOrderOrderedResourceReplayResult? replay = null) =>
            new(status, null, candidatePlan, null, materialization, replay, reason);
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        var keys = left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal);
        return keys.All(key => left.GetValueOrDefault(key) == right.GetValueOrDefault(key));
    }
}

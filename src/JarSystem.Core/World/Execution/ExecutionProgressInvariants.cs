using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World.Execution;

/// <summary>
/// 活动订单执行进度的唯一领域契约。
/// Wire DTO 与 Runtime Restore 必须调用此验证器，禁止各自维护阶段/游标规则。
/// </summary>
internal static class ExecutionProgressInvariants
{
    public static void Validate(
        WorldOrderExecutionStage stage,
        int completedMasterSteps,
        int masterStepCount,
        int completedSlaveSteps,
        int slaveStepCount,
        bool isPartitioned,
        bool slaveTransferCommitted,
        string? reason)
    {
        if (masterStepCount < 0 || slaveStepCount < 0 ||
            completedMasterSteps < 0 || completedMasterSteps > masterStepCount ||
            completedSlaveSteps < 0 || completedSlaveSteps > slaveStepCount)
            throw new InvalidOperationException("执行会话快照的步骤游标越界。");

        var validStage = stage switch
        {
            WorldOrderExecutionStage.MasterSteps =>
                completedMasterSteps < masterStepCount &&
                completedSlaveSteps == 0 && !slaveTransferCommitted,
            WorldOrderExecutionStage.Transfer =>
                isPartitioned &&
                completedMasterSteps == masterStepCount &&
                completedSlaveSteps == 0 && !slaveTransferCommitted,
            WorldOrderExecutionStage.SlaveSteps =>
                isPartitioned &&
                completedMasterSteps == masterStepCount &&
                completedSlaveSteps < slaveStepCount &&
                slaveTransferCommitted,
            _ => false
        };

        if (!validStage || reason != null)
            throw new InvalidOperationException("执行会话快照的阶段与步骤游标组合无效。");
    }
    public static void ValidateManifestPlan(
        int demandId,
        WorldOrderResourceManifest manifest,
        WorldOrderSolvePlan plan)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(plan);
        if (manifest.DemandId != demandId || manifest.IsPartitioned != plan.IsPartitioned ||
            !manifest.TransferJarIds.OrderBy(x => x).SequenceEqual(
                plan.TransferJarIds.OrderBy(x => x)) ||
            !LiquidMapsEqual(manifest.TransferLiquids, plan.TransferLiquids))
            throw new InvalidOperationException("执行会话的 Manifest 与 Plan 订单身份或交接资源不一致。");
    }

    private static bool LiquidMapsEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        if (left.Count != right.Count) return false;
        return left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);
    }

}

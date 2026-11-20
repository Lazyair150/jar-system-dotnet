using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World.Execution;

/// <summary>
/// Applies exactly one immutable world-plan step to a live workshop.
/// The solver's JarIndex is only meaningful inside the step's Before list; the live jar
/// is always resolved by the stable ID stored at that index.
/// </summary>
public sealed class WorldPlanStepApplier
{
    public WorldPlanStepApplyResult Apply(Workshop workshop, WorldPlanStep step)
    {
        if (workshop == null) throw new ArgumentNullException(nameof(workshop));
        if (step == null) throw new ArgumentNullException(nameof(step));
        if (step.Operation == null || !step.Operation.JarIndex.HasValue)
            return Reject("计划步骤缺少操作或 JarIndex");

        var operation = step.Operation;
        var index = operation.JarIndex.Value;
        if (index < 0 || index >= step.Before.Count || step.Before.Count != step.After.Count)
            return Reject("计划步骤的 JarIndex 或 Before/After 长度无效");

        var expectedBefore = step.Before[index];
        var expectedAfter = step.After[index];
        for (var i = 0; i < step.Before.Count; i++)
        {
            var beforeState = step.Before[i];
            var afterState = step.After[i];
            if (beforeState.Id != afterState.Id || beforeState.MasterOrigin != afterState.MasterOrigin)
                return Reject("计划步骤改变了罐子身份或来源标记");
            if (i != index && (beforeState.Shape != afterState.Shape ||
                beforeState.Liquid != afterState.Liquid || beforeState.IsMarked != afterState.IsMarked))
                return Reject("计划步骤意外修改了非目标罐子");
        }
        if (expectedBefore.Id is not > 0 || expectedAfter.Id != expectedBefore.Id ||
            step.Before.Count(x => x.Id == expectedBefore.Id) != 1)
            return Reject("计划目标罐子的 ID 无效或在步骤快照中重复");
        var transitionError = ValidateTransition(operation, expectedBefore, expectedAfter);
        if (transitionError != null) return Reject(transitionError);

        var matches = workshop.Jars.Where(j => j.Id == expectedBefore.Id).ToArray();
        if (matches.Length != 1)
            return Reject(matches.Length == 0
                ? $"工作台中找不到计划罐子 #{expectedBefore.Id}"
                : $"工作台中存在重复罐子 ID #{expectedBefore.Id}");

        var jar = matches[0];
        if (jar.Shape != expectedBefore.Shape ||
            jar.Liquid != expectedBefore.Liquid ||
            jar.IsMarked != expectedBefore.IsMarked)
            return Reject($"罐子 #{expectedBefore.Id} 的执行前状态与计划不符");
        if (jar.IsMarked)
            return Reject($"罐子 #{expectedBefore.Id} 已被本周期其他操作标记");

        if (operation.Consumed == null)
            return Reject("计划 Consumed 账目不能为空");
        var expectedConsumed = ExpectedConsumed(operation, expectedBefore);
        if (expectedConsumed == null || !DictionaryEqual(expectedConsumed, operation.Consumed))
            return Reject("计划 Consumed 与操作类型的液体账目语义不一致");
        if (operation.Type == "RESHAPE" &&
            (string.IsNullOrWhiteSpace(operation.TargetShape) ||
             !jar.IsValidShapeIn(operation.TargetShape) ||
             !workshop.CanTransform(jar.Shape, operation.TargetShape)))
            return Reject("工作台或罐子词表不允许计划中的目标形状");

        var liquidBefore = workshop.LiquidStorage.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var result = ApplyOperation(workshop, jar, operation, expectedBefore);
        if (result != null) return Reject(result);

        if (jar.Shape != expectedAfter.Shape ||
            jar.Liquid != expectedAfter.Liquid ||
            jar.IsMarked != expectedAfter.IsMarked)
            throw new InvalidOperationException(
                $"计划步骤执行后状态违约：罐子 #{expectedBefore.Id} 的真实状态与 After 不一致");

        var expectedDelta = ExpectedFreeLiquidDelta(operation, expectedBefore);
        foreach (var liquid in liquidBefore.Keys.Concat(workshop.LiquidStorage.Keys).Distinct(StringComparer.Ordinal))
        {
            var before = liquidBefore.GetValueOrDefault(liquid);
            var after = workshop.LiquidStorage.GetValueOrDefault(liquid);
            var delta = after - before;
            if (delta != expectedDelta.GetValueOrDefault(liquid))
                throw new InvalidOperationException(
                    $"计划步骤执行后液体账目违约：{liquid} 预期变化 {expectedDelta.GetValueOrDefault(liquid)}，实际变化 {delta}");
        }

        return new WorldPlanStepApplyResult(true, expectedBefore.Id, operation.Type, null);
    }

    private static string? ValidateTransition(
        WorldPlanOperation operation, WorldPlanJarState before, WorldPlanJarState after)
    {
        if (!after.IsMarked) return "计划步骤的 After 状态没有标记目标罐子";
        switch (operation.Type)
        {
            case "MATCH":
                return after.Shape == before.Shape && after.Liquid == before.Liquid
                    ? null : "MATCH 的 After 状态改变了罐子内容";
            case "POUR_IN":
                return before.Liquid == null && after.Shape == before.Shape &&
                    !string.IsNullOrWhiteSpace(operation.TargetLiquid) &&
                    after.Liquid == operation.TargetLiquid
                    ? null : "POUR_IN 的 After 状态不符合操作语义";
            case "POUR_OUT":
                return before.Liquid != null && after.Shape == before.Shape && after.Liquid == null
                    ? null : "POUR_OUT 的 After 状态不符合操作语义";
            case "RESHAPE":
                return !string.IsNullOrWhiteSpace(operation.TargetShape) &&
                    after.Shape == operation.TargetShape && after.Liquid == null &&
                    (operation.FullReshape ? before.Liquid != null : before.Liquid == null)
                    ? null : "RESHAPE 的 After 状态不符合操作语义";
            default:
                return "未知计划操作类型：" + (operation.Type ?? "(null)");
        }
    }

    private static string? ApplyOperation(
        Workshop workshop, Jar jar, WorldPlanOperation operation, WorldPlanJarState before)
    {
        switch (operation.Type)
        {
            case "MATCH":
                if (before.Shape != operation.TargetShape && operation.TargetShape != null)
                    return "MATCH 不应改变罐子形状";
                if (before.Liquid != operation.TargetLiquid && operation.TargetLiquid != null)
                    return "MATCH 不应改变罐内液体";
                var mark = workshop.MarkJar(jar);
                return mark.Success ? null : mark.Reason ?? "MATCH 标记失败";

            case "POUR_IN":
                if (string.IsNullOrWhiteSpace(operation.TargetLiquid))
                    return "POUR_IN 缺少 TargetLiquid";
                var pourIn = workshop.PourIn(jar, operation.TargetLiquid);
                return pourIn.Success ? null : pourIn.Reason ?? "POUR_IN 执行失败";

            case "POUR_OUT":
                var pourOut = workshop.PourOut(jar);
                if (!pourOut.Success) return pourOut.Reason ?? "POUR_OUT 执行失败";
                if (!string.Equals(pourOut.Liquid, before.Liquid, StringComparison.Ordinal))
                    throw new InvalidOperationException("POUR_OUT 成功返回的液体与计划 Before 不一致");
                return null;

            case "RESHAPE":
                if (string.IsNullOrWhiteSpace(operation.TargetShape))
                    return "RESHAPE 缺少 TargetShape";
                var reshape = workshop.TryReshape(
                    jar,
                    operation.TargetShape,
                    operation.FullReshape ? ReshapeMode.Full : ReshapeMode.Empty,
                    operation.FullReshape ? null : operation.CostLiquid);
                return reshape.Success ? null : reshape.Reason ?? "RESHAPE 执行失败";

            default:
                return "未知计划操作类型：" + (operation.Type ?? "(null)");
        }
    }

    private static Dictionary<string, int>? ExpectedConsumed(
        WorldPlanOperation operation, WorldPlanJarState before)
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        switch (operation.Type)
        {
            case "MATCH":
            case "POUR_OUT":
                break;
            case "POUR_IN":
                if (string.IsNullOrWhiteSpace(operation.TargetLiquid)) return null;
                expected[operation.TargetLiquid] = 1;
                break;
            case "RESHAPE":
                if (operation.FullReshape)
                {
                    if (before.Liquid == null || operation.CostLiquid != before.Liquid) return null;
                }
                else
                {
                    if (before.Liquid != null || string.IsNullOrWhiteSpace(operation.CostLiquid)) return null;
                    expected[operation.CostLiquid] = 1;
                }
                break;
            default:
                return null;
        }
        return expected;
    }

    private static Dictionary<string, int> ExpectedFreeLiquidDelta(
        WorldPlanOperation operation, WorldPlanJarState before)
    {
        var delta = new Dictionary<string, int>(StringComparer.Ordinal);
        if (operation.Type == "POUR_IN")
            delta[operation.TargetLiquid!] = -1;
        else if (operation.Type == "POUR_OUT")
            delta[before.Liquid!] = 1;
        else if (operation.Type == "RESHAPE" && !operation.FullReshape)
            delta[operation.CostLiquid!] = -1;
        return delta;
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right)
    {
        var keys = left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal);
        return keys.All(key => left.GetValueOrDefault(key) == right.GetValueOrDefault(key));
    }

    private static WorldPlanStepApplyResult Reject(string reason) =>
        new(false, null, null, reason);
}

/// <summary>Result of applying one plan step. A rejected result guarantees no step mutation occurred.</summary>
public sealed record WorldPlanStepApplyResult(
    bool Success, int? JarId, string? Operation, string? Reason);

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Solver;

/// <summary>
/// 计算 B 类订单从台阶段实际需要从主台转移的游离液体量。
/// 沿路径累计每种液体的净消耗（消耗 − POUR_OUT 回补），取前缀峰值。
/// </summary>
public static class LiquidRequirementCalculator
{
    public static Dictionary<string, int> ComputeRequired(IReadOnlyList<SolverStep> path)
    {
        var required = new Dictionary<string, int>(StringComparer.Ordinal);
        var net = new Dictionary<string, int>(StringComparer.Ordinal);
        // 保留词表中的零值键：旧求解契约与确定性摘要都依赖完整液体键集合。
        foreach (var liquid in Constants.LIQUIDS)
        {
            required[liquid] = 0;
            net[liquid] = 0;
        }

        foreach (var step in path)
        {
            var op = step.Operation;
            if (op == null) continue;

            if (op.Type == "POUR_IN" && op.TargetLiquid != null)
                Add(net, op.TargetLiquid, 1);
            else if (op.Type == "RESHAPE" && !op.FullReshape && op.CostLiquid != null)
                Add(net, op.CostLiquid, 1);
            else if (op.Type == "POUR_OUT")
            {
                var liquid = PourOutLiquid(step);
                if (liquid != null) Add(net, liquid, -1);
            }

            foreach (var item in net)
                required[item.Key] = Math.Max(required.GetValueOrDefault(item.Key), item.Value);
        }

        foreach (var key in required.Keys.ToArray())
            required[key] = Math.Max(0, required[key]);
        return required;
    }

    /// <summary>
    /// 使用最小初始供给重放路径，返回该路径真实需要的合计暂存容量峰值。
    /// 该值不包含主台没有被从台路径需要的剩余液体。
    /// </summary>
    public static int ComputePeakCapacity(IReadOnlyList<SolverStep> path)
    {
        var balances = ComputeRequired(path);
        var peak = ShipLiquidInventory.TotalAmount(balances);
        foreach (var step in path)
        {
            var op = step.Operation;
            if (op == null) continue;

            if (op.Type == "POUR_IN" && op.TargetLiquid != null)
                Add(balances, op.TargetLiquid, -1);
            else if (op.Type == "RESHAPE" && !op.FullReshape && op.CostLiquid != null)
                Add(balances, op.CostLiquid, -1);
            else if (op.Type == "POUR_OUT")
            {
                var liquid = PourOutLiquid(step);
                if (liquid != null) Add(balances, liquid, 1);
            }

            peak = Math.Max(peak, ShipLiquidInventory.TotalAmount(balances));
        }

        return peak;
    }

    private static string? PourOutLiquid(SolverStep step)
    {
        if (!string.IsNullOrWhiteSpace(step.Operation?.TargetLiquid))
            return step.Operation.TargetLiquid;
        var index = step.Operation?.JarIndex;
        return step.Before != null && index.HasValue &&
            index.Value >= 0 && index.Value < step.Before.Count
                ? step.Before[index.Value].Liquid
                : null;
    }

    private static void Add(Dictionary<string, int> values, string liquid, int amount) =>
        values[liquid] = checked(values.GetValueOrDefault(liquid) + amount);
}

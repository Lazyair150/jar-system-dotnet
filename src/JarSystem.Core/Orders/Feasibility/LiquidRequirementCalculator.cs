using System;
using System.Collections.Generic;
using JarSystem.Core.Config;

namespace JarSystem.Core.Orders;

/// <summary>
/// 计算 B 类订单从台阶段**实际需要**从主台转移的游离液体量（每种液体一份）。
///
/// 语义：沿从台路径累计每种液体的净消耗（消耗 − 倒出回补），取累计峰值，
/// 超出从台自身存储的部分才需要转移；满罐改形自耗不占用外部液体。
/// </summary>
public static class LiquidRequirementCalculator
{
    public static Dictionary<string, int> ComputeRequired(List<SolverStep> path, Dictionary<string, int> slaveLiquids)
    {
        var required = new Dictionary<string, int>();
        var consumed = new Dictionary<string, int>();
        var produced = new Dictionary<string, int>();
        foreach (var l in Constants.LIQUIDS) { required[l] = 0; consumed[l] = 0; produced[l] = 0; }

        foreach (var step in path)
        {
            var op = step.Operation;
            if (op == null) continue;
            if (op.Type == "POUR_IN" && op.TargetLiquid != null) consumed[op.TargetLiquid]++;
            else if (op.Type == "RESHAPE" && op.CostLiquid != null)
            {
                var isFull = step.Before != null && op.JarIndex.HasValue && op.JarIndex.Value >= 0 &&
                    op.JarIndex.Value < step.Before.Count && step.Before[op.JarIndex.Value].Liquid != null;
                if (!isFull) consumed[op.CostLiquid]++;
            }
            else if (op.Type == "POUR_OUT" && step.Before != null && op.JarIndex.HasValue &&
                op.JarIndex.Value >= 0 && op.JarIndex.Value < step.Before.Count && step.Before[op.JarIndex.Value].Liquid != null)
            {
                produced[step.Before[op.JarIndex.Value].Liquid!]++;
            }

            foreach (var l in Constants.LIQUIDS)
            {
                int net = consumed[l] - produced[l];
                if (net - slaveLiquids.GetValueOrDefault(l, 0) > required[l])
                    required[l] = net - slaveLiquids.GetValueOrDefault(l, 0);
            }
        }
        foreach (var l in Constants.LIQUIDS)
            required[l] = Math.Max(0, required[l]);
        return required;
    }
}

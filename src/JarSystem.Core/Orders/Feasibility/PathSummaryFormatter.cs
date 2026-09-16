using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.Orders;

/// <summary>
/// 求解器路径 → 对外「路径步骤」（<see cref="SolverStepDto"/>）与统计的格式化器
/// （原 <c>FeasibilityChecker</c> 的私有方法）。
///
/// **路径步骤字段名即对外契约**：14 个键名与类型全部集中在 <see cref="SolverStepDto"/> 的
/// <c>[JsonPropertyName]</c> 上（阶段 3-D 之前是本文件里手写的字典键，键名写错只在运行期暴露）。
/// </summary>
internal static class PathSummaryFormatter
{
    /// <summary>路径步骤 → 强类型步骤列表（path 为 null 或空时返回空列表）。</summary>
    public static List<SolverStepDto> Summary(List<SolverStep>? path)
    {
        if (path == null || path.Count == 0) return [];
        return path.Select(s =>
        {
            var type = s.Operation?.Type ?? "";
            var ji = s.Operation?.JarIndex;
            var ts = s.Operation?.TargetShape;
            var tl = s.Operation?.TargetLiquid;
            var cst = s.Operation?.CostLiquid;
            var desc = s.Operation?.Description ?? "";

            // 「操作前罐子」定位：与原先逐字等价（下标必须落在 Before 快照范围内），
            // 只是把重复了三遍的越界判断收敛成一次；越界时三个字段同时为 null。
            var before = s.Before;
            JarState? src = null;
            if (ji.HasValue && before != null && ji.Value >= 0 && ji.Value < before.Count)
                src = before[ji.Value];

            string opDesc = type switch
            {
                "MATCH" => "直接使用 #" + ji + " (" + desc + ")",
                "POUR_IN" => "向 #" + ji + " 倒入 " + tl,
                "RESHAPE" => "将 #" + ji + " 改形为 " + ts + (cst != null ? " (消耗" + cst + ")" : ""),
                "POUR_OUT" => "倒出 #" + ji + " 中的液体",
                _ => desc
            };

            return new SolverStepDto
            {
                Step = s.Step,
                Requirement = s.Requirement?.Liquid != null
                    ? ("满罐(" + s.Requirement.Shape + "," + s.Requirement.Liquid + ")")
                    : ("空罐(" + s.Requirement?.Shape + ")"),
                Operation = opDesc,
                Type = type,
                JarIndex = ji,
                JarId = src?.Id,
                JarShape = src?.Shape,
                JarLiquid = src?.Liquid,
                TargetShape = ts,
                TargetLiquid = tl,
                CostLiquid = cst,
                ReshapeMode = s.Operation?.Type == "RESHAPE" ? (s.Operation.FullReshape ? "full" : "empty") : null,
                Consumed = s.Operation?.Consumed != null ? new Dictionary<string, int>(s.Operation.Consumed) : null,
                JarsAfter = s.After?.Select(j => j.Shape + (j.Liquid != null ? "/" + j.Liquid : "空")).ToList() ?? new()
            };
        }).ToList();
    }

    // 2026-09 契约统一时删除 DirectMatches / Operations：它们只喂给已删除的
    // directSatisfactions / operationsNeeded 两个键，而这两个键的信息由 solverPath 完整覆盖。
}

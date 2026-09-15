using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Services;

namespace JarSystem.Core.Orders;

public class Order
{
    public int Id { get; }
    public List<Requirement> Requirements { get; }
    public string Type { get; }

    /// <summary>订单状态。只能经 <see cref="MarkSucceeded"/> / <see cref="MarkFailed"/> 迁移。</summary>
    public string Status { get; private set; }

    /// <summary>最近一次可行性分析结果。只能经 <see cref="RecordFeasibility"/> 写入。</summary>
    public FeasibilityResultData? FeasibilityResult { get; private set; }

    public Order(List<Requirement> requirements, string type = Constants.OrderType.A, IIdGenerator? ids = null)
    {
        Id = (ids ?? DefaultIdGenerators.Orders).Next();
        Requirements = requirements;
        Type = type;
        Status = Config.Constants.OrderStatus.PENDING;
        ValidateOrder();
    }

    private void ValidateOrder()
    {
        if (Requirements.Count > Config.Constants.MAX_ORDER_REQUIREMENTS)
            throw new InvalidOperationException($"订单需求数量不能超过 {Config.Constants.MAX_ORDER_REQUIREMENTS} 个");
        bool seenFull = false;
        foreach (var req in Requirements)
        {
            // 安全/健壮性：需求里的形状与液体必须是已知值，否则订单永远不可能被满足
            if (!Array.Exists(Constants.SHAPES, s => s == req.Shape))
                throw new InvalidOperationException($"无效形状: {req.Shape}");
            if (req.Liquid != null && !Array.Exists(Constants.LIQUIDS, l => l == req.Liquid))
                throw new InvalidOperationException($"无效液体: {req.Liquid}");

            if (req.Liquid != null) seenFull = true;
            else if (seenFull) throw new InvalidOperationException("订单需求列表约定: 空罐需求在前、满罐需求在后（执行顺序为满罐优先）");
        }
    }

    // ---- 状态与结果（唯一入口） ----

    /// <summary>标记订单执行成功。</summary>
    public void MarkSucceeded() { Status = Constants.OrderStatus.SUCCESS; }

    /// <summary>标记订单执行失败。</summary>
    public void MarkFailed() { Status = Constants.OrderStatus.FAILED; }

    /// <summary>记录本次可行性分析结果。</summary>
    public void RecordFeasibility(FeasibilityResultData result) { FeasibilityResult = result; }

    public bool IsSatisfiedBy(IReadOnlyList<Jar> jars)
    {
        var matched = JarMatcher.MatchAll(jars, Requirements, JarMatcher.Matches);
        foreach (var idx in matched)
            if (idx < 0) return false;
        return true;
    }

    public override string ToString()
    {
        var reqs = string.Join(", ", Requirements.Select(r =>
            r.Liquid != null ? "满罐(" + r.Shape + "," + r.Liquid + ")" : "空罐(" + r.Shape + ")"));
        return "订单#" + Id + "[" + Type + "类]{" + reqs + "}";
    }
}

public class Requirement
{
    public string Shape { get; set; } = "";
    public string? Liquid { get; set; }
}

public class FeasibilityResultData
{
    public bool Feasible { get; set; }
    public string? Blockage { get; set; }
    public string? Reason { get; set; }
    public string? Timestamp { get; set; }
    public int DirectCount { get; set; }
    public int OperationCount { get; set; }
    public List<SolverStepDto>? SolverPath { get; set; }
    public List<SolverStepDto>? MasterPath { get; set; }
    public List<object>? Path { get; set; }
    public List<DirectMatch>? DirectSatisfactions { get; set; }
    public List<OperationNeed>? OperationsNeeded { get; set; }
    public Dictionary<string, object>? Stats { get; set; }
    public string? CheckMethod { get; set; }
    // 选择性转移记录（B 类）：本次可行性路径需要从主台转移到从台的罐子 Id 清单
    public List<int>? TransferJarIds { get; set; }
    // 需要从主台转移到从台的游离液体量（每种），其余留在主台
    public Dictionary<string, int>? TransferLiquids { get; set; }
}

public class DirectMatch
{
    public Requirement? Requirement { get; set; }
    public string? Location { get; set; }
    public int? JarIndex { get; set; }
    public string? Type { get; set; }
}

public class OperationNeed
{
    public Requirement? Requirement { get; set; }
    public OperationPlan? Plan { get; set; }
}

public class OperationPlan
{
    public string? Type { get; set; }
    public string? Shape { get; set; }
    public string? Liquid { get; set; }
    public string? Method { get; set; }
    public string? SourceShape { get; set; }
    public int Steps { get; set; }
    public Dictionary<string, int>? ConsumesLiquid { get; set; }
}

public class SolverStep
{
    public int Step { get; set; }
    public Requirement? Requirement { get; set; }
    public SolverOperation? Operation { get; set; }
    public List<JarState>? Before { get; set; }
    public List<JarState>? After { get; set; }
    public Dictionary<string, int>? AfterLiquids { get; set; }
}

/// <summary>
/// 求解器操作步骤。属性名与对外 JSON 的路径步骤字段对齐
/// （jarIndex / targetShape / targetLiquid / costLiquid / consumed），避免缩写造成的对照成本。
/// </summary>
public class SolverOperation
{
    public string? Type { get; set; }
    public string? Description { get; set; }
    public int? JarIndex { get; set; }
    public string? TargetShape { get; set; }
    public string? TargetLiquid { get; set; }
    public string? CostLiquid { get; set; }
    public Dictionary<string, int>? Consumed { get; set; }
    public bool FullReshape { get; set; }
}

public class JarState
{
    public int? Id { get; set; }
    public string Shape { get; set; } = "";
    public string? Liquid { get; set; }
    public bool IsMarked { get; set; }
    // 选择性转移标记：从台求解池中的主台来源罐子（受转移预算约束，未使用则留在主台）
    public bool MasterOrigin { get; set; }

    /// <summary>由真实罐子生成求解器状态（投影唯一实现；快照语义为未标记）。</summary>
    public static JarState From(Jar jar) => new()
    {
        Id = jar.Id,
        Shape = jar.Shape,
        Liquid = jar.Liquid,
        IsMarked = false
    };

    /// <summary>复制求解器状态（投影唯一实现）。<paramref name="masterOrigin"/> 为 null 时保留来源标记。</summary>
    public static JarState Copy(JarState source, bool? masterOrigin = null) => new()
    {
        Id = source.Id,
        Shape = source.Shape,
        Liquid = source.Liquid,
        IsMarked = source.IsMarked,
        MasterOrigin = masterOrigin ?? source.MasterOrigin
    };
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
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

    public Order(List<Requirement> requirements, string type = Constants.OrderType.A, IIdGenerator? ids = null,
        Vocabulary? vocabulary = null)
    {
        Id = (ids ?? DefaultIdGenerators.Orders).Next();
        Requirements = requirements;
        Type = type;
        Status = Config.Constants.OrderStatus.PENDING;
        _vocabulary = vocabulary ?? Vocabulary.Default;
        ValidateOrder();
    }

    // S2：订单需求按**词表**校验（原先写死读 Constants.SHAPES / Constants.LIQUIDS）
    private readonly Vocabulary _vocabulary;

    private void ValidateOrder()
    {
        // 订单类型是契约取值（A/B）：非法值会被后续判定当成"非 A"（即 B 类）静默处理，
        // 因此这条规则必须住在**领域**里，而不是住在 Web 的命令分派器里（那里只有 Web 宿主遵守）。
        if (Type != Config.Constants.OrderType.A && Type != Config.Constants.OrderType.B)
            throw new DomainValidationException($"订单类型只能是 A 或 B，收到: {Type}");

        if (Requirements.Count > Config.Constants.MAX_ORDER_REQUIREMENTS)
            throw new DomainValidationException($"订单需求数量不能超过 {Config.Constants.MAX_ORDER_REQUIREMENTS} 个");
        bool seenFull = false;
        foreach (var req in Requirements)
        {
            // 安全/健壮性：需求里的形状与液体必须是**词表内**的值，否则订单永远不可能被满足
            if (!_vocabulary.IsValidShape(req.Shape))
                throw new DomainValidationException($"无效形状: {req.Shape}");
            if (req.Liquid != null && !_vocabulary.IsValidLiquid(req.Liquid))
                throw new DomainValidationException($"无效液体: {req.Liquid}");

            if (req.Liquid != null) seenFull = true;
            else if (seenFull) throw new DomainValidationException("订单需求列表约定: 空罐需求在前、满罐需求在后（执行顺序为满罐优先）");
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

/// <summary>
/// 可行性分析结果。
///
/// ⚠️ **本类的属性名本身就是对外契约**：`autoCheckResults` / `allFeasibilityResults` 消息直接把本对象
/// （嵌在 <see cref="AutoCheckResultDto"/> 里）序列化出去，走 Web 层的 camelCase 策略。
/// 因此这里**逐个属性显式标注 <c>[JsonPropertyName]</c>**：改名不再静默改契约，且能导出 JSON Schema。
/// （`GetState()` 里的 `feasibilityResult` 是另一份 10 键投影，见 <c>FeasibilityStateDto</c>，与本类不同。）
/// </summary>
public class FeasibilityResultData
{
    [JsonPropertyName("feasible")] public bool Feasible { get; set; }

    /// <summary>
    /// **未判定**（2026-09 新增键）：搜索因超出工作预算而提前中止 ⇒ 既不能说"可行"、也不能说"不可行"。
    /// 与 <see cref="Feasible"/>=false 合起来读：`feasible=false && undetermined=false` 才是"确实不可行"，
    /// 也只有那种情况受"不可行 ⇒ 执行必失败"这条不变量的约束。
    /// 前端据此显示"⚠ 未判定"，并且**仍然允许执行**（执行由贪心兜底，不受预测承诺约束）。
    /// </summary>
    [JsonPropertyName("undetermined")] public bool Undetermined { get; set; }

    [JsonPropertyName("blockage")] public string? Blockage { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
    [JsonPropertyName("directCount")] public int DirectCount { get; set; }
    [JsonPropertyName("operationCount")] public int OperationCount { get; set; }
    [JsonPropertyName("solverPath")] public List<SolverStepDto>? SolverPath { get; set; }
    [JsonPropertyName("masterPath")] public List<SolverStepDto>? MasterPath { get; set; }

    /// <summary>恒为 null（历史遗留）。**不能删**：它是本消息键集合的一部分，删掉即改契约。</summary>
    [JsonPropertyName("path")] public List<object>? Path { get; set; }

    [JsonPropertyName("directSatisfactions")] public List<DirectMatch>? DirectSatisfactions { get; set; }
    [JsonPropertyName("operationsNeeded")] public List<OperationNeed>? OperationsNeeded { get; set; }

    /// <summary>恒为 null（历史遗留）。**不能删**：同上。</summary>
    [JsonPropertyName("stats")] public Dictionary<string, object>? Stats { get; set; }

    [JsonPropertyName("checkMethod")] public string? CheckMethod { get; set; }

    // 选择性转移记录（B 类）：本次可行性路径需要从主台转移到从台的罐子 Id 清单
    [JsonPropertyName("transferJarIds")] public List<int>? TransferJarIds { get; set; }

    // 需要从主台转移到从台的游离液体量（每种），其余留在主台
    [JsonPropertyName("transferLiquids")] public Dictionary<string, int>? TransferLiquids { get; set; }
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

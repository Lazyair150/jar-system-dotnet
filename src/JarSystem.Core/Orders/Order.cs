using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

public class Order
{
    public int Id { get; }
    /// <summary>需求列表（对外只读；构造时已做**私有副本**）。</summary>
    public IReadOnlyList<Requirement> Requirements { get; }
    public string Type { get; }

    /// <summary>订单状态。只能经 <see cref="MarkSucceeded"/> / <see cref="MarkFailed"/> 迁移。</summary>
    public string Status { get; private set; }

    /// <summary>
    /// **最近一次**可行性分析结果。只能经 <see cref="RecordFeasibility"/> 写入。
    ///
    /// ⚠️ 语义边界（2026-09-16 复核后写明）：它是"最近一次分析"，**不是对执行的承诺** ——
    /// 宿主开着自动检测时，每 5 秒的 <c>CheckAllFeasibility</c> 会把它**整体覆盖**，
    /// 而执行器读的正是它（`OrderService.ExecuteOrder` → `OrderExecutor`）。
    ///
    /// 为什么这个"覆盖"不是缺陷（审查提过，逐条查过）：
    /// <list type="number">
    /// <item>执行器**不盲信**它：主台路径任何一步失败就明确报"状态与可行性分析不一致"并终止，
    /// 不会带着错误继续（见 <c>OrderExecutor</c> 的 masterPathErrored 分支）；</item>
    /// <item>预算**按次重置**（<c>FeasibilityChecker.Check</c> → <c>BeginCheck</c>），
    /// 所以自动检测不会把预算耗尽"传染"给后续分析（否则跑一会儿就会全是"未判定"）；</item>
    /// <item>覆盖发生在**闸门内**，与命令执行、Tick 互斥 ⇒ 不会在执行的半途中被换掉。</item>
    /// </list>
    /// 残留的真实缺口是**新鲜度不可见**：UI 看到的那份结果可能来自更早的世界状态，
    /// 而 `timestamp` 装的是"分析耗时毫秒"、不是时间点，无法据此判断。要补需要**新增契约键**
    /// （例如"分析时的 tick 序号"）⇒ 属需批准的契约变更，未擅自加。
    /// </summary>
    public FeasibilityResultData? FeasibilityResult { get; private set; }

    public Order(List<Requirement> requirements, string type = Constants.OrderType.A, IIdGenerator? ids = null,
        Vocabulary? vocabulary = null)
    {
        Id = (ids ?? DefaultIdGenerators.Orders).Next();
        // 私有副本：若直接保存调用方传进来的 List，调用方在构造之后仍能
        // order 之外的引用 Add/Remove，从而绕过 ValidateOrder() 的全部约定
        // （A/B 类型、需求数上限、"空罐在前、满罐在后"）。
        Requirements = new List<Requirement>(requirements);
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

    /// <summary>
    /// 清空缓存的可行性结论（世界已变，旧结论不再对应现在的工作台）。
    ///
    /// <para>为什么必须有这个方法：<c>OrderService.ExecuteOrder</c> 会照着
    /// <see cref="FeasibilityResult"/> 里的 <c>SolverPath</c> 执行，而那份路径**没有记下
    /// "算在哪一版工作台上"**（它由 <c>OrderService.WorldSignature</c> 旁挂记录）。
    /// 世界在两次检测之间变了之后，执行器会拿着对不上现状的步骤照做 ——
    /// 清空之后它退回贪心兜底，宁可慢也不能拿着过期计划动手。</para>
    /// </summary>
    public void ClearFeasibility() { FeasibilityResult = null; }

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

/// <summary>
/// 可行性分析结果。
///
/// ⚠️ **本类的属性名本身就是对外契约**：`state` / `feasibilityResult` / `autoCheckResults` /
/// `allFeasibilityResults` **全都序列化这一个类型**（2026-09 契约统一，此前有两套形状：state 走
/// 一份 10 键投影、消息走本类，前端因此被迫写兼容层）。因此这里**逐个属性显式标注
/// <c>[JsonPropertyName]</c>**：改名不再静默改契约，且能导出 JSON Schema。
/// </summary>
public class FeasibilityResultData
{
    // ⚠️ 除 timestamp 外**全部 init**：本类型会经 Order.FeasibilityResult（private set）
    // 与 OrderStateDto 交出去，若属性可写，`order.FeasibilityResult!.Feasible = true`
    // 就能编译并通过 —— "private set" 会被可变对象击穿（分析结论可被任意改写）。
    // timestamp 单独走 internal SetTimestamp（它的值是"分析耗时"，必须在检查结束后才知道）。
    // init 不影响 JSON：schema / 快照 / contract.d.ts 都只看属性类型，不看 setter。
    [JsonPropertyName("feasible")] public bool Feasible { get; init; }

    /// <summary>
    /// **未判定**（2026-09 新增键）：搜索因超出工作预算而提前中止 ⇒ 既不能说"可行"、也不能说"不可行"。
    /// 与 <see cref="Feasible"/>=false 合起来读：`feasible=false && undetermined=false` 才是"确实不可行"，
    /// 也只有那种情况受"不可行 ⇒ 执行必失败"这条不变量的约束。
    /// 前端据此显示"⚠ 未判定"，并且**仍然允许执行**（执行由贪心兜底，不受预测承诺约束）。
    /// </summary>
    [JsonPropertyName("undetermined")] public bool Undetermined { get; init; }

    [JsonPropertyName("blockage")] public string? Blockage { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; private set; }
    [JsonPropertyName("directCount")] public int DirectCount { get; init; }
    [JsonPropertyName("operationCount")] public int OperationCount { get; init; }
    // 四个集合一律 **IReadOnly***（2026-09-16 修）：它们会随 FeasibilityResult 一起交出去，
    // 而执行器**照着 solverPath/masterPath/transferJarIds 干活** —— 交出可变的 List/Dictionary
    // 等于让外部能改写"可行性分析对执行的承诺"（`fr.SolverPath!.Add(...)` 就能注入步骤）。
    // 只读视图不影响 JSON：schema / 快照 / contract.d.ts 只看元素类型，不看集合是 List 还是 IReadOnlyList。
    // （构造端无需改动：List/Dictionary → 只读接口是隐式转换。）
    [JsonPropertyName("solverPath")] public IReadOnlyList<SolverStepDto>? SolverPath { get; init; }
    [JsonPropertyName("masterPath")] public IReadOnlyList<SolverStepDto>? MasterPath { get; init; }
    [JsonPropertyName("checkMethod")] public string? CheckMethod { get; init; }

    // 选择性转移记录（B 类）：本次可行性路径需要从主台转移到从台的罐子 Id 清单
    [JsonPropertyName("transferJarIds")] public IReadOnlyList<int>? TransferJarIds { get; init; }

    // 需要从主台转移到从台的游离液体量（每种），其余留在主台
    //
    // ⚠️ **这一个故意保留 `Dictionary`**（与上面三个集合不同）：本仓库里 `IReadOnlyList` 有
    // "已进 schema 导出且 degraded=0" 的先例（`WorkshopStateDto.BaseMarks`），而
    // `IReadOnlyDictionary` **没有** —— 而 `schema total loose=1` 是被文档钉住的期望值
    // （JsonSchemaExporter 若把它当成无法描述的节点，会多出一个 loose）。
    // 且它在**用法上是惰性的**：执行器只按内置液体名逐个查询（`GetValueOrDefault`），
    // 外部塞进来的额外键读不到，与"注入可执行步骤"的 solverPath 不同风险等级。
    // 要一并收紧的前提是：先确认导出器对 `IReadOnlyDictionary<string,int>` 仍产出
    // `additionalProperties: {"type":"integer"}`（那时再改，并同步文档里的 loose 口径）。
    [JsonPropertyName("transferLiquids")] public Dictionary<string, int>? TransferLiquids { get; init; }

    /// <summary>记录本次分析的耗时（毫秒，字符串形态）。只允许可行性检查链调用。</summary>
    internal void SetTimestamp(string? value) => Timestamp = value;
}

// 2026-09 契约统一时删除：DirectMatch / OperationNeed / OperationPlan。
// 它们只服务于已删除的 directSatisfactions / operationsNeeded 两个键，而后者的唯一消费者
// （前端）只用到了数组长度 —— 而长度就等于 directCount / operationCount；
// 逐条明细由 solverPath（14 字段/步）承载，信息量严格更大。

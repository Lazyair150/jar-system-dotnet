using System.Text.Json.Serialization;

namespace JarSystem.Core.Orders;

/// <summary>
/// `autoCheckResults` 消息的元素（3 个键：index / orderId / result）。
///
/// 原先是 <c>OrderService.CheckAllFeasibility</c> 里手工拼的匿名对象（方法签名是 <c>List&lt;object&gt;</c>）——
/// 键名只靠"匿名对象属性名 + camelCase 策略"约定，schema 无法描述、改名不会报错。
/// 现在键名由 <c>[JsonPropertyName]</c> 钉死，且可直接导出 JSON Schema。
///
/// 注意 <see cref="Result"/> 仍是 <see cref="FeasibilityResultData"/> 本体
/// （该类的属性名本身就是这条消息的契约，已逐个用 <c>[JsonPropertyName]</c> 固定）。
/// </summary>
public sealed class AutoCheckResultDto
{
    /// <summary>订单在工作台列表中的下标。</summary>
    [JsonPropertyName("index")] public int Index { get; init; }

    /// <summary>订单 ID（稳定标识，前端用它选订单）。</summary>
    [JsonPropertyName("orderId")] public int OrderId { get; init; }

    /// <summary>
    /// 本次可行性分析结果。**实际恒非 null**（2026-09-16 更正）：本消息由
    /// <c>OrderService.CheckAllFeasibility</c> 产出，它只对**合法下标**调用 <c>CheckFeasibility</c>，
    /// 而那个方法连非法下标都会返回一个带 <c>reason="无效订单索引"</c> 的结果对象，从不返回 null。
    ///
    /// 类型**保留可空**是刻意的：它已经落进契约产物（`state.schema.json` 的 `"type":["object","null"]`
    /// 与 `contract.d.ts` 的 `| null`），去掉问号会改契约产物 —— 这属于"需要单独批准的契约变更"。
    /// </summary>
    [JsonPropertyName("result")] public FeasibilityResultData? Result { get; init; }
}

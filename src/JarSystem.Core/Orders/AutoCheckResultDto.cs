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

    /// <summary>本次可行性分析结果；订单下标非法等情况下为 null。</summary>
    [JsonPropertyName("result")] public FeasibilityResultData? Result { get; init; }
}

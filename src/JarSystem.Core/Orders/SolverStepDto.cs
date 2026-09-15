using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JarSystem.Core.Orders;

/// <summary>
/// 可行性路径的「单个步骤」对外模型。
///
/// **背景（阶段 3-D）**：此前路径步骤是没有编译期约束的 <c>Dictionary&lt;string, object?&gt;</c>，
/// 生产端拼键、消费端 <c>TryGetValue</c>，键名写错只会在运行时静默退化为 null。
/// 现在由本类取代，键名与类型双向固化：生产端拼错字段**编译不过**，消费端读错字段**编译不过**。
///
/// **14 个 <c>[JsonPropertyName]</c> 就是对外契约**（前端路径弹窗 <c>wwwroot/js/app.js</c>、
/// <c>Godot迁移指南.txt</c> 都依赖）：
/// step / requirement / operation / type / jarIndex / jarId / jarShape / jarLiquid /
/// targetShape / targetLiquid / costLiquid / reshapeMode / consumed / jarsAfter。
///
/// 注意：属性**声明顺序**与原字典的插入顺序逐项一致 ⇒ 序列化出的 JSON 键顺序也逐字不变。
/// 改键名、改顺序、改可空性都等于改契约。
/// </summary>
public sealed class SolverStepDto
{
    /// <summary>步骤序号（求解器路径内从 1 起）。</summary>
    [JsonPropertyName("step")] public int Step { get; init; }

    /// <summary>本步骤服务的需求文本：`满罐(形状,液体)` / `空罐(形状)`。</summary>
    [JsonPropertyName("requirement")] public string? Requirement { get; init; }

    /// <summary>人读的操作描述（含罐子下标与消耗液体）。</summary>
    [JsonPropertyName("operation")] public string? Operation { get; init; }

    /// <summary>操作类型：<c>MATCH</c> / <c>POUR_IN</c> / <c>POUR_OUT</c> / <c>RESHAPE</c>。</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>操作前罐子在快照中的下标（转移后会变化，仅供显示与回退定位）。</summary>
    [JsonPropertyName("jarIndex")] public int? JarIndex { get; init; }

    /// <summary>操作前罐子的稳定 ID（执行时首选按它定位罐子）。</summary>
    [JsonPropertyName("jarId")] public int? JarId { get; init; }

    /// <summary>操作前罐子形状（执行前的状态校验用）。</summary>
    [JsonPropertyName("jarShape")] public string? JarShape { get; init; }

    /// <summary>操作前罐内液体（null = 空罐；执行前的状态校验用）。</summary>
    [JsonPropertyName("jarLiquid")] public string? JarLiquid { get; init; }

    /// <summary>目标形状（改形步骤用）。</summary>
    [JsonPropertyName("targetShape")] public string? TargetShape { get; init; }

    /// <summary>目标液体（倒液步骤用）。</summary>
    [JsonPropertyName("targetLiquid")] public string? TargetLiquid { get; init; }

    /// <summary>改形消耗的液体（空罐改形用）。</summary>
    [JsonPropertyName("costLiquid")] public string? CostLiquid { get; init; }

    /// <summary>改形方式：<c>full</c> / <c>empty</c>；非改形步骤为 null。</summary>
    [JsonPropertyName("reshapeMode")] public string? ReshapeMode { get; init; }

    /// <summary>本步骤消耗的液体量（按液体名聚合）；无消耗为 null。</summary>
    [JsonPropertyName("consumed")] public Dictionary<string, int>? Consumed { get; init; }

    /// <summary>本步骤之后的罐子摘要（`形状/液体`，空罐记为 `形状空`）。</summary>
    [JsonPropertyName("jarsAfter")] public List<string> JarsAfter { get; init; } = new();
}

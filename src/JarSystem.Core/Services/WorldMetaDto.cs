using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JarSystem.Core.Services;

/// <summary>
/// `world` 消息的载荷：**世界元数据**（形状/液体目录）。
///
/// **为什么单独一条消息，而不是塞进 `GetState()`**：
/// 词表是"连接时需要知道一次"的**静态信息**，而 `state` 每 2 秒推一次 ——
/// 放进 state 会让**每条**消息都背上这份数组，也会让契约快照（一行一个叶子）随之变长。
/// 因此由宿主在连接建立时**一次性**下发（见 `Web/WebSocketHub.RunSession`），
/// 前端据此渲染形状下拉框与标记选择器，从而**删掉客户端那份形状清单副本**。
///
/// 语义边界：这里下发的是**词表**（有哪些形状/液体），不是规则 ——
/// 形状能怎么变仍由工作台的标记（`WorkshopCapability`）决定。
/// </summary>
public sealed class WorldMetaDto
{
    /// <summary>形状目录（顺序 = 工作台/契约里的顺序）。</summary>
    [JsonPropertyName("shapes")] public List<string> Shapes { get; init; } = new();

    /// <summary>液体目录（恰好两个，玩法语义）。</summary>
    [JsonPropertyName("liquids")] public List<string> Liquids { get; init; } = new();
}

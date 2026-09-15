using System.Text.Json;

namespace JarSystem.Core.Config;

/// <summary>
/// <see cref="WorldOptions"/> 的 JSON 读写。
///
/// **只处理字符串，不碰文件** —— 读写文件、决定"配置在哪、坏了要不要拒绝启动"都是**宿主的职责**。
/// 这样核心既保持零依赖（只用 .NET 内置的 System.Text.Json），也不把 IO 策略写进领域层。
///
/// 解析保持**严格 JSON**（不容忍注释与尾随逗号），并**容忍字段缺失**（缺失 = 用 POCO 默认值）：
/// 于是"只写想改的那几行"也能工作，而写错的键名不会静默生效 ——
/// 键名是否正确由 <see cref="WorldOptions.Validate"/> 的引用完整性检查兜底（引用不存在 ⇒ Errors）。
/// </summary>
public static class WorldOptionsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    /// <summary>从 JSON 文本解析世界配置（文本非法时抛 <see cref="JsonException"/>，由宿主决定如何处理）。</summary>
    public static WorldOptions FromJson(string json) =>
        JsonSerializer.Deserialize<WorldOptions>(json, Options) ?? new WorldOptions();

    /// <summary>导出为 JSON 文本（供宿主写样本文件 / 导出当前世界）。</summary>
    public static string ToJson(WorldOptions world) => JsonSerializer.Serialize(world, Options);
}

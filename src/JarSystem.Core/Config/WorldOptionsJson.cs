using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarSystem.Core.Config;

/// <summary>
/// <see cref="WorldOptions"/> 的 JSON 读写。
///
/// **只处理字符串，不碰文件** —— 读写文件、决定"配置在哪、坏了要不要拒绝启动"都是**宿主的职责**。
/// 这样核心既保持零依赖（只用 .NET 内置的 System.Text.Json），也不把 IO 策略写进领域层。
///
/// 解析保持**严格 JSON**（不容忍注释与尾随逗号），并**容忍字段缺失**（缺失 = 用 POCO 默认值）：
/// 于是"只写想改的那几行"也能工作。
///
/// **未知键名必须报错（2026-09-16 修）**：原注释声称"写错的键名不会静默生效 —— 键名是否正确由
/// <see cref="WorldOptions.Validate"/> 的引用完整性检查兜底"，这句话是**错的**：
/// <c>"workshops"</c> 拼成 <c>"workshop"</c> 时该键只会被静默丢弃，于是装配出一个**没有工作台**的世界，
/// 而 <c>Validate</c> 一条错误都报不出来（它检查的是"已读到字段"的引用完整性，看不见"根本没读到的字段"）。
/// 现在用 <see cref="JsonUnmappedMemberHandling.Disallow"/> 把未知键变成 <see cref="JsonException"/>，
/// 宿主据此拒绝启动（与"启动即失败优于静默缺产出"同一条原则）。
/// </summary>
public static class WorldOptionsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        // 拼错的键名 / 过期字段不再静默丢弃 ⇒ 抛 JsonException。
        // 只作用于**反序列化**：ToJson 的输出逐字节不变（保存格式与契约快照都不受影响）。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>从 JSON 文本解析世界配置（文本非法时抛 <see cref="JsonException"/>，由宿主决定如何处理）。</summary>
    public static WorldOptions FromJson(string json) =>
        JsonSerializer.Deserialize<WorldOptions>(json, Options) ?? new WorldOptions();

    /// <summary>导出为 JSON 文本（供宿主写样本文件 / 导出当前世界）。</summary>
    public static string ToJson(WorldOptions world) => JsonSerializer.Serialize(world, Options);
}

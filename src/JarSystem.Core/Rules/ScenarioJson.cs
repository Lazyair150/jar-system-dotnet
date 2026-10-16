using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarSystem.Core.Rules;

/// <summary>
/// 场景数据的 JSON 读写（只处理字符串、不碰文件 —— 与 WorldOptionsJson 同一分工）。
///
/// 与既有策略一致：未知键名直接报错（拼错的键不能静默丢弃，否则会加载出一个"少了一半规则"的场景）。
/// </summary>
public static class ScenarioJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ScenarioOptions FromJson(string json) =>
        JsonSerializer.Deserialize<ScenarioOptions>(json, Options) ?? new ScenarioOptions();

    public static string ToJson(ScenarioOptions scenario) => JsonSerializer.Serialize(scenario, Options);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace JarSystem.Core.Process;

/// <summary>过程日志存档（格式 v1，见设计规格 §3.5）。</summary>
public sealed class JournalDocument
{
    /// <summary>存档格式版本（**改名或改结构必须升版本**）。</summary>
    [JsonPropertyName("journalVersion")] public int JournalVersion { get; init; } = 1;

    /// <summary>条目序列。</summary>
    [JsonPropertyName("entries")] public List<JournalEntry> Entries { get; init; } = new();
}

/// <summary>
/// 过程日志的 JSON 读写（存档格式 v1）。
///
/// 与 <c>WorldCommandJson</c> / <c>WorldOptionsJson</c> 同一策略：**未知键名直接报错**。
/// 理由相同：存档是**外部输入**（手改过的档 / 旧版本字段），静默丢字段会读出一个"看起来正常、
/// 实际少了记录"的过程日志 —— 那比读档失败危险得多。
/// </summary>
public static class JournalCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>导出为 JSON 文本。</summary>
    public static string ToJson(IEnumerable<JournalEntry> entries) =>
        JsonSerializer.Serialize(new JournalDocument { Entries = entries.ToList() }, Options);

    /// <summary>从 JSON 文本读回（文本非法或含未知键时抛 <see cref="JsonException"/>，由宿主决定如何处置）。</summary>
    public static JournalDocument FromJson(string json) =>
        JsonSerializer.Deserialize<JournalDocument>(json, Options) ?? new JournalDocument();
}

using System.Text.Json;
using JarSystem.Core.Orders;

/// <summary>
/// WebSocket 命令载荷的 JSON 读取/序列化辅助（原为 Program.cs 的顶层局部函数）。
/// 容错语义保持不变：类型不符时取默认值（字符串 → null、数字 → 0/缺省、数组 → 过滤非字符串项）。
/// </summary>
public static class JsonArgs
{
    /// <summary>全局序列化选项：camelCase 属性命名（对外 JSON 契约的一部分）。</summary>
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string J(object o) => JsonSerializer.Serialize(o, Options);

    public static string? JS(JsonElement d, string k) =>
        d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int JI(JsonElement d, string k) =>
        d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static double JD(JsonElement d, string k) =>
        d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    public static bool JB(JsonElement d, string k) =>
        d.TryGetProperty(k, out var v) && v.GetBoolean();

    public static int? JIOrNull(JsonElement d, string k) =>
        d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;

    public static List<string> JL(JsonElement d, string k)
    {
        var r = new List<string>();
        if (d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var i in v.EnumerateArray())
                if (i.ValueKind == JsonValueKind.String) r.Add(i.GetString() ?? "");
        return r;
    }

    public static List<Requirement> ParseReqs(JsonElement d)
    {
        var r = new List<Requirement>();
        if (d.TryGetProperty("requirements", out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var i in v.EnumerateArray())
                r.Add(new Requirement
                {
                    Shape = i.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "",
                    Liquid = i.TryGetProperty("liquid", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null
                });
        return r;
    }
}

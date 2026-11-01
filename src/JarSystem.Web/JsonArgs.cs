using System.Text.Json;
using JarSystem.Core.Orders;
using JarSystem.Core.Abstractions;

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
        d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    public static double JD(JsonElement d, string k) =>
        d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? x : 0;

    public static bool JB(JsonElement d, string k) =>
        d.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    public static int? JIOrNull(JsonElement d, string k) =>
        d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : (int?)null;

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
            {
                // 元素必须是对象：非对象元素（字符串/数字/数组）这里直接跳过，
                // 而不是让 JsonElement.TryGetProperty 抛 InvalidOperationException 把整条连接带走。
                if (i.ValueKind != JsonValueKind.Object) continue;
                r.Add(new Requirement
                {
                    Shape = i.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "",
                    Liquid = i.TryGetProperty("liquid", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null
                });
            }
        return r;
    }
}

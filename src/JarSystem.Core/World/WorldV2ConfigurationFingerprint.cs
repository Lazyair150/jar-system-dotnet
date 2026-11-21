using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 配置指纹。对象属性按名称排序、数组保留原始顺序：
/// 配置字典的插入顺序不影响指纹，但生产者/工作台/形状列表顺序仍按配置语义保留。
/// </summary>
public static class WorldV2ConfigurationFingerprint
{
    public static string Compute(WorldV2Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var validation = options.Validate();
        if (!validation.IsValid)
            throw new ArgumentException(
                "不能为无效的 World v2 配置生成指纹：" + string.Join("；", validation.Errors),
                nameof(options));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(options));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(document.RootElement, writer);

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

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

    // Runtime owner IDs are persisted independently in the World owner identity map.
    // They must not alter the immutable definition/configuration fingerprint.
    private static bool IsRuntimeOwnerIdentityProperty(string propertyName) =>
        string.Equals(propertyName, "OwnedInstanceId", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(propertyName, "ProducerInstanceId", StringComparison.OrdinalIgnoreCase);

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                    .Where(item => !IsRuntimeOwnerIdentityProperty(item.Name))
                    .OrderBy(item => item.Name, StringComparer.Ordinal))
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

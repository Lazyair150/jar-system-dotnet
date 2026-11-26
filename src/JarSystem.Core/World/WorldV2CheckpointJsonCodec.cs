using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;

namespace JarSystem.Core.World;

/// <summary>
/// Versioned JSON envelope for World v2 execution-checkpoint wire payloads.
/// This class validates the outer persistence contract; callers must supply a
/// dedicated wire DTO when the runtime checkpoint contains domain types that
/// are not directly JSON-constructible.
/// </summary>
public static class WorldV2CheckpointJsonCodec
{
    public const int CurrentSchemaVersion = 11;
    public const string CurrentKind = "world-v2-execution-checkpoint";

    private static readonly Regex FingerprintPattern =
        new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 128
    };

    /// <summary>将完整 World V2 活动执行检查点编码为版本化 JSON。</summary>
    public static string EncodeExecutionResourceCheckpoint(
        WorldV2ExecutionResourceCheckpoint checkpoint,
        bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return Encode(
            WorldV2ExecutionCheckpointWireDto.FromCheckpoint(checkpoint),
            checkpoint.ConfigurationFingerprint,
            indented);
    }

    /// <summary>解码根级 World V2 检查点，并恢复为经过 wire 校验的领域快照对象图。</summary>
    public static WorldV2ExecutionResourceCheckpoint DecodeExecutionResourceCheckpoint(
        string json,
        global::JarSystem.Core.Domain.WorldInstanceId? legacyMigrationWorldInstanceId = null)
    {
        var envelope = Decode<WorldV2ExecutionCheckpointWireDto>(json);
        if (envelope.SchemaVersion >= 10)
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
            if (!document.RootElement.TryGetProperty("payload", out var payloadElement) ||
                !payloadElement.TryGetProperty("ResourceLedger", out var ledgerElement) ||
                ledgerElement.ValueKind != JsonValueKind.Object ||
                !ledgerElement.TryGetProperty("TransferBatchReceipts", out var batchReceiptsElement) ||
                batchReceiptsElement.ValueKind != JsonValueKind.Array)
                throw new JsonException("Schema 10+ resource ledger must carry the TransferBatchReceipts array.");

            foreach (var batchReceipt in batchReceiptsElement.EnumerateArray())
            {
                if (batchReceipt.ValueKind != JsonValueKind.Object ||
                    !batchReceipt.TryGetProperty("SchemaVersion", out var batchSchemaElement) ||
                    batchSchemaElement.ValueKind != JsonValueKind.Number ||
                    !batchSchemaElement.TryGetInt32(out var batchSchema) ||
                    batchSchema != ResourceLedgerTransferBatchReceipt.CurrentSchemaVersion)
                    throw new JsonException("Schema 10+ composite transfer receipts must declare a supported SchemaVersion.");
            }

            if (envelope.SchemaVersion >= 11 &&
                (!ledgerElement.TryGetProperty("ProducedJarImportReceipts", out var producedReceiptsElement) ||
                 producedReceiptsElement.ValueKind != JsonValueKind.Array))
                throw new JsonException("Schema 11+ resource ledger must carry the ProducedJarImportReceipts array.");

            if (envelope.SchemaVersion >= 11)
            {
                foreach (var receipt in ledgerElement.GetProperty("ProducedJarImportReceipts").EnumerateArray())
                {
                    if (receipt.ValueKind != JsonValueKind.Object ||
                        !receipt.TryGetProperty("SchemaVersion", out var receiptSchemaElement) ||
                        receiptSchemaElement.ValueKind != JsonValueKind.Number ||
                        !receiptSchemaElement.TryGetInt32(out var receiptSchema) ||
                        receiptSchema != ResourceLedgerProducedJarImportReceipt.CurrentSchemaVersion)
                        throw new JsonException("Schema 11+ produced-Jar import receipts must declare a supported SchemaVersion.");
                }
            }
        }
        var ledgerEvents = envelope.Payload.ResourceLedger?.Events;
        if (envelope.SchemaVersion >= 8 &&
            (ledgerEvents == null || ledgerEvents.Any(item =>
                item == null || item.FromEndpoint == null || item.ToEndpoint == null)))
            throw new JsonException("Schema 8+ resource ledger events must carry explicit tagged endpoints.");
        if (envelope.SchemaVersion >= 9 &&
            (ledgerEvents == null || ledgerEvents.Any(item =>
                item == null || string.IsNullOrWhiteSpace(item.DisplayName))))
            throw new JsonException("Schema 9+ resource ledger events must carry an explicit DisplayName.");
        return envelope.Payload.ToCheckpoint(
            envelope.ConfigurationFingerprint, legacyMigrationWorldInstanceId);
    }

    /// <summary>Encode a wire payload into a versioned checkpoint envelope.</summary>
    public static string Encode<TPayload>(
        TPayload payload,
        string? configurationFingerprint,
        bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateFingerprint(configurationFingerprint);

        var payloadJson = JsonSerializer.Serialize(payload, SerializerOptions);
        using var payloadDocument = JsonDocument.Parse(payloadJson);
        if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("检查点 payload 必须是 JSON object。", nameof(payload));

        var envelope = new Dictionary<string, object?>
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["kind"] = CurrentKind,
            ["configurationFingerprint"] = configurationFingerprint,
            ["payload"] = payloadDocument.RootElement.Clone()
        };

        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions(SerializerOptions)
        {
            WriteIndented = indented
        });
    }

    /// <summary>
    /// Decode and validate the envelope before materializing its payload as a wire DTO.
    /// Unsupported versions, duplicate/unknown envelope fields and malformed payloads are rejected.
    /// </summary>
    public static WorldV2CheckpointEnvelope<TPayload> Decode<TPayload>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("检查点 JSON 不能为空。", nameof(json));

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("检查点根节点必须是 JSON object。");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion", "kind", "configurationFingerprint", "payload"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new JsonException("检查点 envelope 包含未知字段: " + property.Name);
            if (!seen.Add(property.Name))
                throw new JsonException("检查点 envelope 包含重复字段: " + property.Name);
        }

        if (!root.TryGetProperty("schemaVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt32(out var version))
            throw new JsonException("检查点缺少有效 schemaVersion。");
        // Schemas 3-5 predate WorldInstanceId; schema 6 predates the owner map;
        // schema 7 predates tagged endpoints; schema 8 predates DisplayName.
        // Missing identities/provenance remain explicitly unknown; never infer them from names or fingerprints.
        if (version is not (3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or CurrentSchemaVersion))
            throw new NotSupportedException("不支持的检查点 schemaVersion: " + version);

        if (!root.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String ||
            !string.Equals(kindElement.GetString(), CurrentKind, StringComparison.Ordinal))
            throw new JsonException("检查点 kind 缺失或不匹配。");

        if (!root.TryGetProperty("configurationFingerprint", out var fingerprintElement))
            throw new JsonException("检查点缺少 configurationFingerprint 字段。");
        var fingerprint = fingerprintElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => fingerprintElement.GetString(),
            _ => throw new JsonException("configurationFingerprint 必须是字符串或 null。")
        };
        try
        {
            ValidateFingerprint(fingerprint);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("检查点配置指纹格式无效。", exception);
        }

        if (!root.TryGetProperty("payload", out var payloadElement) ||
            payloadElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("检查点 payload 缺失或不是 JSON object。");

        TPayload? payload;
        try
        {
            payload = payloadElement.Deserialize<TPayload>(SerializerOptions);
        }
        catch (JsonException)
        {
            throw;
        }
        if (payload is null)
            throw new JsonException("检查点 payload 不能反序列化为 null。");

        return new WorldV2CheckpointEnvelope<TPayload>(
            version, fingerprint, payload);
    }

    private static void ValidateFingerprint(string? fingerprint)
    {
        if (fingerprint != null && !FingerprintPattern.IsMatch(fingerprint))
            throw new ArgumentException(
                "配置指纹必须为 64 位小写 SHA-256 十六进制字符串或 null。",
                nameof(fingerprint));
    }
}

/// <summary>已经通过 envelope 校验的版本化检查点 wire payload。</summary>
public sealed record WorldV2CheckpointEnvelope<TPayload>(
    int SchemaVersion,
    string? ConfigurationFingerprint,
    TPayload Payload);

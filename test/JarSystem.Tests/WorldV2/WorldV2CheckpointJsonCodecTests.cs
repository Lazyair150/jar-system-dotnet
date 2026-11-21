using System;
using System.Text.Json;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldV2CheckpointJsonCodecTests
{
    private sealed record Payload(string Name, int Count);

    private const string Fingerprint =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void EncodeAndDecodePreservesVersionFingerprintAndPayload()
    {
        var json = WorldV2CheckpointJsonCodec.Encode(
            new Payload("B 类订单", 3), Fingerprint);

        var decoded = WorldV2CheckpointJsonCodec.Decode<Payload>(json);

        Assert.Equal(WorldV2CheckpointJsonCodec.CurrentSchemaVersion, decoded.SchemaVersion);
        Assert.Equal(Fingerprint, decoded.ConfigurationFingerprint);
        Assert.Equal(new Payload("B 类订单", 3), decoded.Payload);
    }

    [Fact]
    public void DecodeAcceptsNullFingerprintForLegacyDirectlyCreatedContexts()
    {
        var json = WorldV2CheckpointJsonCodec.Encode(new Payload("legacy", 1), null);

        var decoded = WorldV2CheckpointJsonCodec.Decode<Payload>(json);

        Assert.Null(decoded.ConfigurationFingerprint);
        Assert.Equal(new Payload("legacy", 1), decoded.Payload);
    }

    [Fact]
    public void DecodeRejectsUnsupportedSchemaVersion()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "kind": "world-v2-execution-checkpoint",
              "configurationFingerprint": null,
              "payload": {}
            }
            """;

        Assert.Throws<NotSupportedException>(
            () => WorldV2CheckpointJsonCodec.Decode<Payload>(json));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"kind":"wrong","configurationFingerprint":null,"payload":{}}""")]
    [InlineData("""{"schemaVersion":1,"kind":"world-v2-execution-checkpoint","payload":{}}""")]
    [InlineData("""{"schemaVersion":1,"kind":"world-v2-execution-checkpoint","configurationFingerprint":"bad","payload":{}}""")]
    [InlineData("""{"schemaVersion":1,"kind":"world-v2-execution-checkpoint","configurationFingerprint":null,"payload":[]}""")]
    [InlineData("""{"schemaVersion":1,"kind":"world-v2-execution-checkpoint","configurationFingerprint":null,"payload":{},"extra":true}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1,"kind":"world-v2-execution-checkpoint","configurationFingerprint":null,"payload":{}}""")]
    public void DecodeRejectsInvalidEnvelope(string json)
    {
        Assert.Throws<JsonException>(
            () => WorldV2CheckpointJsonCodec.Decode<Payload>(json));
    }

    [Fact]
    public void EncodeRejectsInvalidFingerprint()
    {
        Assert.Throws<ArgumentException>(
            () => WorldV2CheckpointJsonCodec.Encode(new Payload("x", 1), "not-a-sha256"));
    }

    [Fact]
    public void EncodeRejectsNonObjectPayload()
    {
        Assert.Throws<ArgumentException>(
            () => WorldV2CheckpointJsonCodec.Encode(new[] { 1, 2, 3 }, null));
    }
}

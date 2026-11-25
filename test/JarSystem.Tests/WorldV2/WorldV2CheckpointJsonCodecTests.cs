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
    public void DecodeRejectsV2CheckpointWithoutExplicitLiquidPolarityMapping()
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
    [InlineData(3)]
    [InlineData(4)]
    public void DecodeAcceptsLegacyCheckpointEnvelopes(int version)
    {
        var json = $"""
            {
              "schemaVersion": {{version}},
              "kind": "world-v2-execution-checkpoint",
              "configurationFingerprint": null,
              "payload": {}
            }
            """;

        var decoded = WorldV2CheckpointJsonCodec.Decode<Payload>(json);

        Assert.Equal(version, decoded.SchemaVersion);
        Assert.Equal(new Payload("", 0), decoded.Payload);
    }

    [Fact]
    public void DecodeRejectsUnsupportedSchemaVersion()
    {
        const string json = """
            {
              "schemaVersion": 6,
              "kind": "world-v2-execution-checkpoint",
              "configurationFingerprint": null,
              "payload": {}
            }
            """;

        Assert.Throws<NotSupportedException>(
            () => WorldV2CheckpointJsonCodec.Decode<Payload>(json));
    }

    [Theory]
    [InlineData("""{"schemaVersion":3,"kind":"wrong","configurationFingerprint":null,"payload":{}}""")]
    [InlineData("""{"schemaVersion":3,"kind":"world-v2-execution-checkpoint","payload":{}}""")]
    [InlineData("""{"schemaVersion":3,"kind":"world-v2-execution-checkpoint","configurationFingerprint":"bad","payload":{}}""")]
    [InlineData("""{"schemaVersion":3,"kind":"world-v2-execution-checkpoint","configurationFingerprint":null,"payload":[]}""")]
    [InlineData("""{"schemaVersion":3,"kind":"world-v2-execution-checkpoint","configurationFingerprint":null,"payload":{},"extra":true}""")]
    [InlineData("""{"schemaVersion":3,"schemaVersion":3,"kind":"world-v2-execution-checkpoint","configurationFingerprint":null,"payload":{}}""")]
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

    [Fact]
    public void SessionWireDtoRoundTripsBClassHandoffAndPlanWithoutRuntimeObjects()
    {
        var dto = new JarSystem.Core.World.Execution.WorldOrderExecutionSessionWireDto
        {
            DemandId = 17,
            Stage = "SlaveSteps",
            CompletedMasterSteps = 1,
            CompletedSlaveSteps = 0,
            UnitElapsedLogicalMilli = 25,
            UnitDurationLogicalMilli = 100,
            SlaveTransferCommitted = true,
            Manifest = new JarSystem.Core.World.Execution.WorldOrderResourceManifestWireDto
            {
                DemandId = 17,
                WorldVersion = "world-v2:42",
                IsPartitioned = true,
                MasterJarIds = new[] { 1, 2 },
                TransferJarIds = new[] { 2 },
                LockedJarIds = new[] { 1, 2 },
                MasterInitialLiquidAmounts = new System.Collections.Generic.Dictionary<string, int> { ["water"] = 3 },
                TransferLiquids = new System.Collections.Generic.Dictionary<string, int> { ["water"] = 1 },
                MasterFinalLiquidBalances = new System.Collections.Generic.Dictionary<string, int> { ["water"] = 2 },
                MasterStagingPeakCapacity = 3,
                SlaveStagingPeakCapacity = 1
            },
            Plan = new JarSystem.Core.World.Execution.WorldOrderSolvePlanWireDto
            {
                IsPartitioned = true,
                TransferJarIds = new[] { 2 },
                TransferLiquids = new System.Collections.Generic.Dictionary<string, int> { ["water"] = 1 },
                MasterSteps = new[]
                {
                    new JarSystem.Core.World.Execution.WorldPlanStepWireDto
                    {
                        Step = 0,
                        RequirementShape = "round",
                        RequirementLiquid = "water",
                        Operation = new JarSystem.Core.World.Execution.WorldPlanOperationWireDto
                        {
                            Type = "POUR_IN", JarIndex = 0, TargetLiquid = "water",
                            Consumed = new System.Collections.Generic.Dictionary<string, int> { ["water"] = 1 }
                        },
                        Before = new[] { new JarSystem.Core.World.Execution.WorldPlanJarStateWireDto { Id = 1, Shape = "round" } },
                        After = new[] { new JarSystem.Core.World.Execution.WorldPlanJarStateWireDto { Id = 1, Shape = "round", Liquid = "water", IsMarked = true } },
                        AfterLiquids = new System.Collections.Generic.Dictionary<string, int> { ["water"] = 2 }
                    }
                },
                SlaveSteps = new[]
                {
                    new JarSystem.Core.World.Execution.WorldPlanStepWireDto
                    {
                        Step = 0,
                        RequirementShape = "square",
                        RequirementLiquid = "water",
                        Operation = new JarSystem.Core.World.Execution.WorldPlanOperationWireDto
                        {
                            Type = "MATCH", JarIndex = 0, TargetShape = "square", TargetLiquid = "water",
                            Consumed = new System.Collections.Generic.Dictionary<string, int>()
                        },
                        Before = new[] { new JarSystem.Core.World.Execution.WorldPlanJarStateWireDto { Id = 2, Shape = "square", Liquid = "water", MasterOrigin = true } },
                        After = new[] { new JarSystem.Core.World.Execution.WorldPlanJarStateWireDto { Id = 2, Shape = "square", Liquid = "water", IsMarked = true, MasterOrigin = true } },
                        AfterLiquids = new System.Collections.Generic.Dictionary<string, int>()
                    }
                }
            }
        };

        dto.Validate();
        var json = WorldV2CheckpointJsonCodec.Encode(dto, Fingerprint);
        var decoded = WorldV2CheckpointJsonCodec.Decode<JarSystem.Core.World.Execution.WorldOrderExecutionSessionWireDto>(json);

        var planTransferMarker = "\"TransferLiquids\":{\"water\":1}";
        var planTransferIndex = json.LastIndexOf(planTransferMarker, StringComparison.Ordinal);
        Assert.True(planTransferIndex >= 0);
        var mismatchedJson = json[..planTransferIndex] +
            json[planTransferIndex..].Replace(planTransferMarker, "\"TransferLiquids\":{\"water\":2}", StringComparison.Ordinal);
        var mismatched = WorldV2CheckpointJsonCodec.Decode<JarSystem.Core.World.Execution.WorldOrderExecutionSessionWireDto>(mismatchedJson);
        Assert.Throws<InvalidOperationException>(() => mismatched.Payload.Validate());

        decoded.Payload.Validate();
        Assert.Equal(17, decoded.Payload.DemandId);
        Assert.Equal("SlaveSteps", decoded.Payload.Stage);
        Assert.True(decoded.Payload.SlaveTransferCommitted);
        Assert.Equal(1, decoded.Payload.Manifest.TransferLiquids["water"]);
        Assert.Equal(2, decoded.Payload.Manifest.MasterFinalLiquidBalances["water"]);
        Assert.Equal(0, decoded.Payload.Plan.SlaveSteps[0].Operation!.JarIndex);
        Assert.Equal("MATCH", decoded.Payload.Plan.SlaveSteps[0].Operation!.Type);

        var restored = decoded.Payload.ToSnapshot();
        Assert.Equal(17, restored.DemandId);
        Assert.Equal(JarSystem.Core.World.Execution.WorldOrderExecutionStage.SlaveSteps, restored.Stage);
        Assert.Equal(1, restored.CompletedMasterSteps);
        Assert.Equal(0, restored.CompletedSlaveSteps);
        Assert.True(restored.SlaveTransferCommitted);
        Assert.Equal(new[] { 1, 2 }, restored.Manifest.LockedJarIds);
        Assert.Equal(new[] { 2 }, restored.Manifest.TransferJarIds);
        Assert.Equal(1, restored.Plan.TransferLiquids["water"]);
        Assert.Equal("MATCH", restored.Plan.SlaveSteps[0].Operation!.Type);

        var recaptured = JarSystem.Core.World.Execution.WorldOrderExecutionSessionWireDto.FromSnapshot(restored);
        recaptured.Validate();
        Assert.Equal(decoded.Payload.Plan.SlaveSteps[0].After[0].IsMarked,
            recaptured.Plan.SlaveSteps[0].After[0].IsMarked);
    }

    [Fact]
    public void SessionWireDtoRejectsSlaveStageBeforeTransferCommit()
    {
        var dto = new JarSystem.Core.World.Execution.WorldOrderExecutionSessionWireDto
        {
            DemandId = 1,
            Stage = "SlaveSteps",
            UnitDurationLogicalMilli = 10,
            UnitElapsedLogicalMilli = 1,
            SlaveTransferCommitted = false,
            Manifest = new JarSystem.Core.World.Execution.WorldOrderResourceManifestWireDto
            {
                DemandId = 1, WorldVersion = "v1", IsPartitioned = true,
                MasterJarIds = new[] { 1 }, TransferJarIds = new[] { 1 }, LockedJarIds = new[] { 1 }
            },
            Plan = new JarSystem.Core.World.Execution.WorldOrderSolvePlanWireDto
            {
                IsPartitioned = true, TransferJarIds = new[] { 1 }
            }
        };

        Assert.Throws<InvalidOperationException>(() => dto.Validate());
    }

}

using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using JarSystem.Core.World;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests;

public sealed class ResourceLedgerSnapshotTests
{
    [Fact]
    public void RestorePreservesSequenceDroppedCountAndIncrementalCursor()
    {
        var ledger = new ResourceLedger(capacity: 4);
        for (var i = 1; i <= 6; i++)
            ledger.Record(i, ResourceCauses.Production, ResourceKinds.Liquid, "酒", 1);

        var snapshot = ledger.Capture();
        Assert.Equal(6, snapshot.LastSeq);
        Assert.Equal(2, snapshot.Dropped);
        Assert.Equal(4, snapshot.Retained);

        var restored = ResourceLedger.Restore(snapshot);
        Assert.Equal(6, restored.LastSeq);
        Assert.Equal(2, restored.Dropped);
        Assert.Equal(new long[] { 3, 4, 5, 6 }, restored.Entries.Select(item => item.Seq));
        Assert.Equal(new long[] { 5, 6 }, restored.Since(4).Select(item => item.Seq));

        var appended = restored.Record(7, ResourceCauses.Discard,
            ResourceKinds.Liquid, "蜜", 2);
        Assert.Equal(7, appended!.Seq);
    }

    [Fact]
    public void RestorePreservesSequenceAcrossAnExplicitClear()
    {
        var ledger = new ResourceLedger();
        ledger.Record(1, ResourceCauses.Stock, ResourceKinds.Liquid, "酒", 1);
        ledger.Clear();

        var restored = ResourceLedger.Restore(ledger.Capture());
        Assert.Equal(1, restored.LastSeq);
        Assert.Empty(restored.Entries);
        Assert.Equal(2, restored.Record(2, ResourceCauses.Production,
            ResourceKinds.Liquid, "酒", 1)!.Seq);
    }

    [Fact]
    public void RecordBatchOnceDeduplicatesWholeSettlementAndPersistsOperationIdAfterTrimming()
    {
        var ledger = new ResourceLedger(capacity: 1);
        var batch = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Return, ResourceKinds.Liquid,
                "酒", 2, From: "订单暂存", To: "自机液库"),
            new ResourceLedgerMutation(ResourceCauses.Discard, ResourceKinds.Liquid,
                "蜜", 1, From: "订单暂存", Reason: "自机液库总容量不足")
        };

        Assert.True(ledger.RecordBatchOnce("world:order:7:settlement", 12, batch));
        Assert.False(ledger.RecordBatchOnce("world:order:7:settlement", 12, batch));
        Assert.True(ledger.HasRecordedOperation("world:order:7:settlement"));
        Assert.Equal(2, ledger.LastSeq);

        var restored = ResourceLedger.Restore(ledger.Capture());
        Assert.True(restored.HasRecordedOperation("world:order:7:settlement"));
        Assert.False(restored.RecordBatchOnce("world:order:7:settlement", 13,
            new[] { new ResourceLedgerMutation(
                ResourceCauses.Return, ResourceKinds.Liquid, "酒", 99) }));
        Assert.Equal(2, restored.LastSeq);
    }

    [Fact]
    public void InvalidBatchPublishesNeitherPartialEventsNorOperationReceipt()
    {
        var ledger = new ResourceLedger();
        var operationId = "world:order:8:transfer";
        var batch = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Jar,
                "圆罐", 1, From: "主台", To: "从台"),
            new ResourceLedgerMutation(ResourceCauses.Transfer, "unsupported-kind",
                "bad", 1, From: "主台", To: "从台")
        };

        Assert.Throws<DomainValidationException>(() =>
            ledger.RecordBatchOnce(operationId, 5, batch));

        Assert.Equal(0, ledger.LastSeq);
        Assert.Empty(ledger.Entries);
        Assert.False(ledger.HasRecordedOperation(operationId));
    }

    [Fact]
    public void EntriesExposeReadOnlyViewInsteadOfMutableBackingList()
    {
        var ledger = new ResourceLedger();
        ledger.Record(1, ResourceCauses.Stock, ResourceKinds.Liquid, "酒", 1);

        var view = Assert.IsAssignableFrom<System.Collections.Generic.IList<ResourceEvent>>(ledger.Entries);
        Assert.True(view.IsReadOnly);
        Assert.Throws<System.NotSupportedException>(() => view.Clear());
        Assert.Single(ledger.Entries);
    }

    [Fact]
    public void SnapshotDoesNotSilentlyDeduplicateOperationIdsBeforeRestoreValidation()
    {
        var snapshot = new ResourceLedgerSnapshot(
            4, 0, 0, Array.Empty<ResourceEvent>(),
            new[] { "operation.duplicate", "operation.duplicate" });

        Assert.Throws<DomainValidationException>(() => ResourceLedger.Restore(snapshot));
    }

    [Fact]
    public void EmptyOperationBatchDoesNotReserveAnIdWithoutAnyResourceFact()
    {
        var ledger = new ResourceLedger();

        Assert.Throws<DomainValidationException>(() =>
            ledger.RecordBatchOnce("operation.empty", 1, Array.Empty<ResourceLedgerMutation>()));

        Assert.Equal(0, ledger.LastSeq);
        Assert.Empty(ledger.Entries);
        Assert.False(ledger.HasRecordedOperation("operation.empty"));
    }

    [Fact]
    public void TransferReceiptAndOperationIdSurviveSnapshotAndCheckpointWireRoundTrip()
    {
        var ledger = new ResourceLedger(capacity: 4);
        var receipt = new ResourceTransferReceipt(
            "operation.transfer.1", "connection.1", "source.out", "target.in",
            RuleResourceKind.Liquid, "酒",
            new RuleResourceAccount("workshop.free-liquid-staging", "workshop.source"),
            new RuleResourceAccount("workshop.free-liquid-staging", "workshop.target"),
            2, 2, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");

        Assert.True(ledger.RecordTransferOnce(
            receipt.OperationId, 21,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                    "酒", 2, From: "workshop.source", To: "workshop.target")
            },
            receipt));

        var restored = ResourceLedger.Restore(ledger.Capture());
        Assert.True(restored.TryGetTransferReceipt(receipt.OperationId, out var restoredReceipt));
        Assert.Equal(receipt, restoredReceipt);

        var wire = ResourceLedgerWireDto.FromSnapshot(restored.Capture());
        wire.Validate();
        var json = JsonSerializer.Serialize(wire);
        var decodedWire = JsonSerializer.Deserialize<ResourceLedgerWireDto>(json)
            ?? throw new System.InvalidOperationException("Ledger wire DTO deserialized as null.");
        decodedWire.Validate();
        var roundTripped = ResourceLedger.Restore(decodedWire.ToSnapshot());
        Assert.True(roundTripped.TryGetTransferReceipt(receipt.OperationId, out var wireReceipt));
        Assert.Equal(receipt, wireReceipt);
        Assert.False(roundTripped.RecordTransferOnce(
            receipt.OperationId, 22,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                    "酒", 2, From: "workshop.source", To: "workshop.target")
            },
            receipt));
        Assert.Equal(1, roundTripped.Entries.Count);
    }

    [Fact]
    public void TransferReceiptJournalRejectsOperationIdPayloadCollision()
    {
        var ledger = new ResourceLedger();
        var receipt = new ResourceTransferReceipt(
            "operation.transfer.collision", "connection.1", "source.out", "target.in",
            RuleResourceKind.Liquid, "酒",
            new RuleResourceAccount("workshop.free-liquid-staging", "workshop.source"),
            new RuleResourceAccount("workshop.free-liquid-staging", "workshop.target"),
            1, 1, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");
        var different = receipt with { RequestedAmount = 2, TransferredAmount = 2 };

        Assert.True(ledger.RecordTransferOnce(
            receipt.OperationId, 1,
            new[] { new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid, "酒", 1) },
            receipt));
        Assert.Throws<System.InvalidOperationException>(() => ledger.RecordTransferOnce(
            receipt.OperationId, 2,
            new[] { new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid, "酒", 2) },
            different));
        Assert.Equal(1, ledger.LastSeq);
        Assert.True(ledger.TryGetTransferReceipt(receipt.OperationId, out var stored));
        Assert.Equal(receipt, stored);
    }

    [Fact]
    public void TransferEventsOperationIdAndReceiptCommitAsOneRestorableUnit()
    {
        var ledger = new ResourceLedger(capacity: 2);
        var source = new RuleResourceAccount("workshop-liquid-staging", "workshop.master");
        var target = new RuleResourceAccount("workshop-liquid-staging", "workshop.slave");
        var receipt = new ResourceTransferReceipt(
            "operation.receipt.1", "connection.receipt.1",
            "master.liquid.out", "slave.liquid.in",
            RuleResourceKind.Liquid, "酒", source, target,
            3, 3, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");
        var batch = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                "酒", 3, From: source.OwnerId, To: target.OwnerId)
        };

        Assert.True(ledger.RecordTransferOnce(receipt.OperationId, 41, batch, receipt));
        Assert.False(ledger.RecordTransferOnce(receipt.OperationId, 41, batch, receipt));
        Assert.True(ledger.HasRecordedOperation(receipt.OperationId));
        Assert.True(ledger.TryGetTransferReceipt(receipt.OperationId, out var stored));
        Assert.Equal(receipt, stored);
        Assert.Single(ledger.Entries);
        Assert.Equal(1, ledger.LastSeq);

        Assert.Throws<System.InvalidOperationException>(() =>
            ledger.RecordTransferOnce(receipt.OperationId, 41, batch,
                receipt with { ReasonCode = "different-receipt" }));

        var restored = ResourceLedger.Restore(ledger.Capture());
        Assert.True(restored.TryGetTransferReceipt(receipt.OperationId, out var restoredReceipt));
        Assert.Equal(receipt, restoredReceipt);
        Assert.False(restored.RecordTransferOnce(receipt.OperationId, 42, batch, receipt));
    }

    [Fact]
    public void TransferReceiptJournalSurvivesWorldCheckpointJsonWireRoundTrip()
    {
        var ledger = new ResourceLedger();
        var source = new RuleResourceAccount("ship-liquid-inventory", "ship.01");
        var target = new RuleResourceAccount("workshop-prefill-liquid", "workshop.01");
        var receipt = new ResourceTransferReceipt(
            "operation.receipt.2", "connection.receipt.2",
            "ship.liquid.out", "workshop.prefill.in",
            RuleResourceKind.Liquid, "蜜", source, target,
            2, 2, 0, 0, ResourceTransferOutcome.Transferred,
            "ship-before", "ship-after", "prefill-before", "prefill-after", "allowed");
        ledger.RecordTransferOnce(receipt.OperationId, 52,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Flow, ResourceKinds.Liquid,
                    "蜜", 2, From: source.OwnerId, To: target.OwnerId)
            }, receipt);

        var wire = ResourceLedgerWireDto.FromSnapshot(ledger.Capture());
        var json = WorldV2CheckpointJsonCodec.Encode(wire, null);
        var decoded = WorldV2CheckpointJsonCodec.Decode<ResourceLedgerWireDto>(json).Payload;
        var restored = ResourceLedger.Restore(decoded.ToSnapshot());

        Assert.True(restored.TryGetTransferReceipt(receipt.OperationId, out var restoredReceipt));
        Assert.Equal(receipt, restoredReceipt);
        Assert.True(restored.HasRecordedOperation(receipt.OperationId));
        Assert.Equal(1, restored.LastSeq);
    }

    [Fact]
    public void TransferJournalRejectsEventsThatDoNotMatchReceiptEndpoints()
    {
        var ledger = new ResourceLedger();
        var receipt = new ResourceTransferReceipt(
            "operation.transfer.endpoint", "connection.1", "source.out", "target.in",
            RuleResourceKind.Liquid, "酒",
            new RuleResourceAccount("workshop.free-liquid-staging", "workshop.source"),
            new RuleResourceAccount("workshop.free-liquid-staging", "workshop.target"),
            1, 1, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");

        Assert.Throws<DomainValidationException>(() => ledger.RecordTransferOnce(
            receipt.OperationId, 1,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                    "酒", 1, From: "wrong-source", To: "workshop.target")
            },
            receipt));
        Assert.Equal(0, ledger.LastSeq);
        Assert.False(ledger.HasRecordedOperation(receipt.OperationId));
    }

    [Fact]
    public void RestoreRejectsLastSequenceThatDoesNotMatchRetainedTail()
    {
        var invalid = new ResourceLedgerSnapshot(
            4, 9, 0,
            new[]
            {
                new ResourceEvent
                {
                    Seq = 7, Tick = 1, Cause = ResourceCauses.Stock,
                    Kind = ResourceKinds.Liquid, Name = "酒", Amount = 1
                }
            });

        Assert.Throws<DomainValidationException>(() => ResourceLedger.Restore(invalid));
    }
}

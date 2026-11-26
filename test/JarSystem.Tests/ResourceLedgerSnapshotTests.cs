using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
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
        Assert.False(restored.RecordBatchOnce("world:order:7:settlement", 13, batch));
        Assert.Throws<System.InvalidOperationException>(() => restored.RecordBatchOnce(
            "world:order:7:settlement", 13,
            new[] { new ResourceLedgerMutation(
                ResourceCauses.Return, ResourceKinds.Liquid, "酒", 99) }));
        Assert.Equal(2, restored.LastSeq);
    }

    [Fact]
    public void HasRecordedBatchVerifiesExactPayloadWithoutMutatingLedger()
    {
        const string operationId = "world:order:payload-check:settlement";
        var batch = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Return, ResourceKinds.Liquid,
                "酒", 2, From: "订单暂存", To: "自机液库"),
            new ResourceLedgerMutation(ResourceCauses.Discard, ResourceKinds.Liquid,
                "蜜", 1, From: "订单暂存", Reason: "自机液库总容量不足")
        };
        var ledger = new ResourceLedger();
        Assert.True(ledger.RecordBatchOnce(operationId, 31, batch));
        var lastSeq = ledger.LastSeq;
        var entries = ledger.Entries.Select(item =>
            (item.Seq, item.OperationId, item.Cause, item.Kind, item.ResourceIdentity, item.Amount))
            .ToArray();

        Assert.True(ledger.HasRecordedBatch(operationId, batch));
        Assert.False(ledger.HasRecordedBatch(operationId, new[]
        {
            batch[0],
            batch[1] with { Amount = 2 }
        }));
        Assert.False(ledger.HasRecordedBatch(operationId, new[]
        {
            batch[0] with { ResourceIdentity = "蜜" },
            batch[1]
        }));
        Assert.False(ledger.HasRecordedBatch("missing:settlement", batch));
        Assert.Equal(lastSeq, ledger.LastSeq);
        Assert.Equal(entries, ledger.Entries.Select(item =>
            (item.Seq, item.OperationId, item.Cause, item.Kind, item.ResourceIdentity, item.Amount)));

        var restored = ResourceLedger.Restore(ledger.Capture());
        Assert.True(restored.HasRecordedBatch(operationId, batch));
        Assert.False(restored.HasRecordedBatch(operationId, new[]
        {
            batch[0] with { Amount = 99 }, batch[1]
        }));
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
    public void LedgerRejectsCrossWorldAndMixedVersionAccountToAccountFactsAtomically()
    {
        var worldA = new WorldInstanceId("ledger-world-a");
        var worldB = new WorldInstanceId("ledger-world-b");
        var source = ResourceEndpoint.ForAccount(new RuleResourceAccount(
            new OwnerIdentity(worldA, ResourceOwnerKind.Workshop, "source"), "workshop.free-liquid-staging").AccountId);
        var foreignTarget = ResourceEndpoint.ForAccount(new RuleResourceAccount(
            new OwnerIdentity(worldB, ResourceOwnerKind.Workshop, "target"), "workshop.free-liquid-staging").AccountId);
        var legacyTarget = ResourceEndpoint.ForAccount(
            new RuleResourceAccount("workshop.free-liquid-staging", "legacy-target").AccountId);

        var ledger = new ResourceLedger();
        Assert.Throws<DomainValidationException>(() => ledger.Record(
            1, ResourceCauses.Transfer, ResourceKinds.Liquid, "酒", 1,
            fromEndpoint: source, toEndpoint: foreignTarget));
        Assert.Throws<DomainValidationException>(() => ledger.RecordBatchOnce(
            "operation.cross-world", 1,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid, "酒", 1,
                    FromAccountId: source.AccountId, ToAccountId: foreignTarget.AccountId,
                    FromEndpoint: source, ToEndpoint: foreignTarget)
            }));
        Assert.Throws<DomainValidationException>(() => ledger.RecordBatchOnce(
            "operation.mixed-version", 1,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid, "酒", 1,
                    FromAccountId: source.AccountId, ToAccountId: legacyTarget.AccountId,
                    FromEndpoint: source, ToEndpoint: legacyTarget)
            }));

        Assert.Empty(ledger.Entries);
        Assert.Equal(0, ledger.LastSeq);
        Assert.False(ledger.HasRecordedOperation("operation.cross-world"));
        Assert.False(ledger.HasRecordedOperation("operation.mixed-version"));

        var forgedSnapshot = new ResourceLedgerSnapshot(
            8, 1, 0,
            new[]
            {
                new ResourceEvent
                {
                    Seq = 1, Tick = 1, Cause = ResourceCauses.Transfer,
                    Kind = ResourceKinds.Liquid, Name = "酒", DisplayName = "酒",
                    ResourceIdentity = "酒", Amount = 1,
                    FromAccountId = source.AccountId, ToAccountId = foreignTarget.AccountId,
                    FromEndpoint = source, ToEndpoint = foreignTarget
                }
            },
            Array.Empty<string>());
        Assert.Throws<DomainValidationException>(() => ResourceLedger.Restore(forgedSnapshot));
    }

    [Fact]
    public void RestoreRejectsOperationEventsWithoutTheirPayloadFingerprint()
    {
        const string operationId = "operation.missing-fingerprint";
        var snapshot = new ResourceLedgerSnapshot(
            4, 1, 0,
            new[]
            {
                new ResourceEvent
                {
                    Seq = 1, Tick = 1, Cause = ResourceCauses.Production,
                    Kind = ResourceKinds.Liquid, Name = "酒", ResourceIdentity = "酒",
                    OperationId = operationId, Amount = 1
                }
            },
            new[] { operationId });

        Assert.Throws<DomainValidationException>(() => ResourceLedger.Restore(snapshot));
    }

    [Fact]
    public void CheckpointWireRejectsOperationEventsWithoutTheirPayloadFingerprint()
    {
        const string operationId = "operation.wire-missing-fingerprint";
        var wire = new ResourceLedgerWireDto
        {
            Capacity = 4,
            LastSeq = 1,
            OperationIds = new List<string> { operationId },
            Events = new List<ResourceEventWireDto>
            {
                new()
                {
                    Seq = 1, Tick = 1, Cause = ResourceCauses.Production,
                    Kind = ResourceKinds.Liquid, Name = "酒", ResourceIdentity = "酒",
                    OperationId = operationId, Amount = 1
                }
            }
        };

        Assert.Throws<System.InvalidOperationException>(() => wire.Validate());
    }

    [Fact]
    public void RestoreRejectsTransferReceiptsWithoutTheirPayloadFingerprint()
    {
        const string operationId = "operation.receipt-missing-fingerprint";
        var source = new RuleResourceAccount("workshop-liquid-staging", "workshop.source");
        var target = new RuleResourceAccount("workshop-liquid-staging", "workshop.target");
        var receipt = new ResourceTransferReceipt(
            operationId, "connection.missing-fingerprint", "source.out", "target.in",
            RuleResourceKind.Liquid, "酒", source, target,
            1, 1, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");
        var snapshot = new ResourceLedgerSnapshot(
            4, 0, 0, Array.Empty<ResourceEvent>(),
            new[] { operationId }, new[] { receipt });

        Assert.Throws<DomainValidationException>(() => ResourceLedger.Restore(snapshot));
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
    public void EmptyOperationBatchDoesNotCreatePhantomLedgerOperation()
    {
        var ledger = new ResourceLedger();

        Assert.False(ledger.RecordBatchOnce(
            "operation.empty-settlement", 1, Array.Empty<ResourceLedgerMutation>()));
        Assert.False(ledger.RecordBatchOnce(
            "operation.empty-settlement", 1, Array.Empty<ResourceLedgerMutation>()));

        Assert.Equal(0, ledger.LastSeq);
        Assert.Empty(ledger.Entries);
        Assert.False(ledger.HasRecordedOperation("operation.empty-settlement"));
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
        var wireEvent = Assert.Single(roundTripped.Entries);
        Assert.Equal(receipt.SourceAccount.AccountId, wireEvent.FromAccountId);
        Assert.Equal(receipt.TargetAccount.AccountId, wireEvent.ToAccountId);
        Assert.False(roundTripped.RecordTransferOnce(
            receipt.OperationId, 22,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                    "酒", 2, From: "workshop.source", To: "workshop.target")
            },
            receipt));
        Assert.Single(roundTripped.Entries);
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
            new[] { new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid, "酒", 1,
                From: receipt.SourceAccount.OwnerId, To: receipt.TargetAccount.OwnerId) },
            receipt));
        Assert.Throws<System.InvalidOperationException>(() => ledger.RecordTransferOnce(
            receipt.OperationId, 2,
            new[] { new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid, "酒", 2,
                From: receipt.SourceAccount.OwnerId, To: receipt.TargetAccount.OwnerId) },
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
        var transferEvent = Assert.Single(ledger.Entries);
        Assert.Equal(source.AccountId, transferEvent.FromAccountId);
        Assert.Equal(target.AccountId, transferEvent.ToAccountId);
        Assert.Equal(1, ledger.LastSeq);

        Assert.Throws<System.InvalidOperationException>(() =>
            ledger.RecordTransferOnce(receipt.OperationId, 41, batch,
                receipt with { ReasonCode = "different-receipt" }));

        var restored = ResourceLedger.Restore(ledger.Capture());
        Assert.True(restored.TryGetTransferReceipt(receipt.OperationId, out var restoredReceipt));
        Assert.Equal(receipt, restoredReceipt);
        var restoredEvent = Assert.Single(restored.Entries);
        Assert.Equal(source.AccountId, restoredEvent.FromAccountId);
        Assert.Equal(target.AccountId, restoredEvent.ToAccountId);
        Assert.False(restored.RecordTransferOnce(receipt.OperationId, 42, batch, receipt));
    }

    [Fact]
    public void RestoreFromReplacesEventsOperationIdsAndReceiptJournalTogether()
    {
        var current = new ResourceLedger();
        var replacement = new ResourceLedger();
        var currentSource = new RuleResourceAccount("workshop-liquid-staging", "workshop.current.source");
        var currentTarget = new RuleResourceAccount("workshop-liquid-staging", "workshop.current.target");
        var replacementSource = new RuleResourceAccount("workshop-liquid-staging", "workshop.replacement.source");
        var replacementTarget = new RuleResourceAccount("workshop-liquid-staging", "workshop.replacement.target");
        var currentReceipt = new ResourceTransferReceipt(
            "operation.current", "connection.current", "current.out", "current.in",
            RuleResourceKind.Liquid, "酒", currentSource, currentTarget,
            1, 1, 0, 0, ResourceTransferOutcome.Transferred,
            "current-source-before", "current-source-after",
            "current-target-before", "current-target-after", "allowed");
        var replacementReceipt = new ResourceTransferReceipt(
            "operation.replacement", "connection.replacement", "replacement.out", "replacement.in",
            RuleResourceKind.Liquid, "蜜", replacementSource, replacementTarget,
            2, 2, 0, 0, ResourceTransferOutcome.Transferred,
            "replacement-source-before", "replacement-source-after",
            "replacement-target-before", "replacement-target-after", "allowed");

        current.RecordTransferOnce(currentReceipt.OperationId, 1,
            new[] { new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                "酒", 1, From: currentSource.OwnerId, To: currentTarget.OwnerId) }, currentReceipt);
        replacement.RecordTransferOnce(replacementReceipt.OperationId, 2,
            new[] { new ResourceLedgerMutation(ResourceCauses.Flow, ResourceKinds.Liquid,
                "蜜", 2, From: replacementSource.OwnerId, To: replacementTarget.OwnerId) }, replacementReceipt);

        current.RestoreFrom(replacement.Capture());

        Assert.False(current.HasRecordedOperation(currentReceipt.OperationId));
        Assert.True(current.HasRecordedOperation(replacementReceipt.OperationId));
        Assert.False(current.TryGetTransferReceipt(currentReceipt.OperationId, out _));
        Assert.True(current.TryGetTransferReceipt(replacementReceipt.OperationId, out var restored));
        Assert.Equal(replacementReceipt, restored);
        Assert.Single(current.Entries);
        Assert.Equal(1, current.LastSeq);
    }

    [Fact]
    public void ConcurrentSameOperationPublishesExactlyOneReceiptAndEventBatch()
    {
        var ledger = new ResourceLedger();
        var source = new RuleResourceAccount("workshop-liquid-staging", "workshop.concurrent.source");
        var target = new RuleResourceAccount("workshop-liquid-staging", "workshop.concurrent.target");
        var receipt = new ResourceTransferReceipt(
            "operation.concurrent.1", "connection.concurrent.1",
            "source.out", "target.in", RuleResourceKind.Liquid, "酒",
            source, target, 4, 4, 0, 0, ResourceTransferOutcome.Transferred,
            "source-v1", "source-v2", "target-v1", "target-v2", "allowed");
        var batch = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                "酒", 4, From: source.OwnerId, To: target.OwnerId)
        };
        var committed = 0;

        System.Threading.Tasks.Parallel.For(0, 32, _ =>
        {
            if (ledger.RecordTransferOnce(receipt.OperationId, 77, batch, receipt))
                System.Threading.Interlocked.Increment(ref committed);
        });

        Assert.Equal(1, committed);
        Assert.Single(ledger.Entries);
        Assert.Equal(1, ledger.LastSeq);
        Assert.True(ledger.TryGetTransferReceipt(receipt.OperationId, out var stored));
        Assert.Equal(receipt, stored);
    }

    [Fact]
    public void TransferReceiptAndOperationIdSurviveEventWindowTrimming()
    {
        var ledger = new ResourceLedger(capacity: 1);
        var source = new RuleResourceAccount("workshop-liquid-staging", "workshop.trim.source");
        var target = new RuleResourceAccount("workshop-liquid-staging", "workshop.trim.target");
        var receipt = new ResourceTransferReceipt(
            "operation.trim.receipt", "connection.trim.receipt",
            "source.out", "target.in", RuleResourceKind.Liquid, "酒",
            source, target, 1, 1, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");
        var batch = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                "酒", 1, From: source.OwnerId, To: target.OwnerId)
        };

        Assert.True(ledger.RecordTransferOnce(receipt.OperationId, 1, batch, receipt));
        ledger.Record(2, ResourceCauses.Production, ResourceKinds.Liquid, "蜜", 1);

        Assert.Equal(1, ledger.Dropped);
        Assert.Single(ledger.Entries);
        Assert.True(ledger.HasRecordedOperation(receipt.OperationId));
        Assert.True(ledger.TryGetTransferReceipt(receipt.OperationId, out var retainedReceipt));
        Assert.Equal(receipt, retainedReceipt);

        var restored = ResourceLedger.Restore(ledger.Capture());
        Assert.True(restored.TryGetTransferReceipt(receipt.OperationId, out var restoredReceipt));
        Assert.Equal(receipt, restoredReceipt);
        Assert.False(restored.RecordTransferOnce(receipt.OperationId, 3, batch, receipt));
        Assert.Equal(2, restored.LastSeq);
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
    public void TransferJournalRequiresJarReceiptsToRepresentOneCanonicalJarId()
    {
        var source = new RuleResourceAccount("workshop-jar-storage", "workshop.jar.source");
        var target = new RuleResourceAccount("workshop-jar-storage", "workshop.jar.target");
        var invalidReceipts = new[]
        {
            new ResourceTransferReceipt(
                "operation.jar.noncanonical", "connection.jar.1", "source.jar.out", "target.jar.in",
                RuleResourceKind.Jar, "jar-401", source, target,
                1, 1, 0, 0, ResourceTransferOutcome.Transferred,
                "source-before", "source-after", "target-before", "target-after", "allowed"),
            new ResourceTransferReceipt(
                "operation.jar.multiple", "connection.jar.2", "source.jar.out", "target.jar.in",
                RuleResourceKind.Jar, "401", source, target,
                2, 2, 0, 0, ResourceTransferOutcome.Transferred,
                "source-before", "source-after", "target-before", "target-after", "allowed")
        };

        foreach (var receipt in invalidReceipts)
        {
            var ledger = new ResourceLedger();
            Assert.Throws<DomainValidationException>(() => ledger.RecordTransferOnce(
                receipt.OperationId, 1,
                new[]
                {
                    new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Jar,
                        "圆罐", receipt.TransferredAmount,
                        From: source.OwnerId, To: target.OwnerId)
                },
                receipt));
            Assert.False(ledger.HasRecordedOperation(receipt.OperationId));
            Assert.Empty(ledger.Entries);
        }
    }

    [Fact]
    public void RestoreRejectsNoncanonicalJarReceiptInCheckpoint()
    {
        var source = new RuleResourceAccount("workshop-jar-storage", "workshop.restore.source");
        var target = new RuleResourceAccount("workshop-jar-storage", "workshop.restore.target");
        var receipt = new ResourceTransferReceipt(
            "operation.jar.restore.invalid", "connection.jar.restore",
            "source.jar.out", "target.jar.in", RuleResourceKind.Jar, "jar-17",
            source, target, 1, 1, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");
        var snapshot = new ResourceLedgerSnapshot(
            4, 1, 0,
            new[]
            {
                new ResourceEvent
                {
                    Seq = 1, Tick = 1, Cause = ResourceCauses.Transfer,
                    Kind = ResourceKinds.Jar, Name = "圆罐", Amount = 1,
                    From = source.OwnerId, To = target.OwnerId
                }
            },
            new[] { receipt.OperationId },
            new[] { receipt });

        Assert.Throws<DomainValidationException>(() => ResourceLedger.Restore(snapshot));
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

        Assert.Throws<DomainValidationException>(() => ledger.RecordTransferOnce(
            receipt.OperationId, 1,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Stock, ResourceKinds.Liquid,
                    "酒", 1, From: "workshop.source", To: "workshop.target")
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

    [Fact]
    public void BatchEventsCarryOperationIdAndCanonicalResourceIdentity()
    {
        var ledger = new ResourceLedger();
        var operationId = "operation.batch.identity";
        var batch = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                "酒", 2, From: "source", To: "target",
                FromAccountId: new RuleResourceAccount("source", "source1").AccountId,
                ToAccountId: new RuleResourceAccount("target", "target1").AccountId),
            new ResourceLedgerMutation(ResourceCauses.Discard, ResourceKinds.Liquid,
                "蜜", 1, From: "source", To: null,
                FromAccountId: new RuleResourceAccount("source", "source1").AccountId)
        };

        Assert.True(ledger.RecordBatchOnce(operationId, 9, batch));

        Assert.Equal(new[] { operationId, operationId }, ledger.Entries.Select(item => item.OperationId));
        Assert.Equal(new[] { "酒", "蜜" }, ledger.Entries.Select(item => item.ResourceIdentity));
        var wire = ResourceLedgerWireDto.FromSnapshot(ledger.Capture());
        var encoded = WorldV2CheckpointJsonCodec.Encode(wire, null);
        var decoded = WorldV2CheckpointJsonCodec.Decode<ResourceLedgerWireDto>(encoded).Payload;
        var restored = ResourceLedger.Restore(decoded.ToSnapshot());
        Assert.Equal(new[] { operationId, operationId }, restored.Entries.Select(item => item.OperationId));
        Assert.Equal(new RuleResourceAccount("source", "source1").AccountId, restored.Entries[0].FromAccountId);
        Assert.Equal(new RuleResourceAccount("target", "target1").AccountId, restored.Entries[0].ToAccountId);
        Assert.False(restored.RecordBatchOnce(operationId, 10, batch));
        Assert.Throws<System.InvalidOperationException>(() => restored.RecordBatchOnce(
            operationId, 10, new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                    "酒", 2, From: "source", To: "target",
                    FromAccountId: new RuleResourceAccount("other-source-kind", "source1").AccountId,
                    ToAccountId: new RuleResourceAccount("target", "target1").AccountId)
            }));
    }

    [Fact]
    public void JarTransferEventUsesJarIdRatherThanShapeAsResourceIdentity()
    {
        var ledger = new ResourceLedger();
        var source = new RuleResourceAccount("workshop-jar-storage", "workshop.jar.source");
        var target = new RuleResourceAccount("workshop-jar-storage", "workshop.jar.target");
        var receipt = new ResourceTransferReceipt(
            "operation.jar.identity", "connection.jar.identity",
            "source.jar.out", "target.jar.in", RuleResourceKind.Jar, "401",
            source, target, 1, 1, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");

        Assert.True(ledger.RecordTransferOnce(receipt.OperationId, 4,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Jar,
                    "圆罐", 1, From: source.OwnerId, To: target.OwnerId, ResourceIdentity: "401")
            }, receipt));

        var fact = Assert.Single(ledger.Entries);
        Assert.Equal("圆罐", fact.Name);
        Assert.Equal("401", fact.ResourceIdentity);
        Assert.Equal(receipt.OperationId, fact.OperationId);

        var mismatchedReceipt = receipt with { OperationId = "operation.jar.identity.mismatch", ResourceIdentity = "402" };
        Assert.Throws<DomainValidationException>(() => ledger.RecordTransferOnce(
            mismatchedReceipt.OperationId, 5,
            new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Jar,
                    "圆罐", 1, From: source.OwnerId, To: target.OwnerId, ResourceIdentity: "401")
            }, mismatchedReceipt));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void LegacyLedgerWireKeepsResourceAndAccountIdentitiesUnknown(int schemaVersion)
    {
        var json = $$"""
        {
          "schemaVersion": {{schemaVersion}},
          "kind": "world-v2-execution-checkpoint",
          "configurationFingerprint": null,
          "payload": {
            "Capacity": 4,
            "LastSeq": 1,
            "Dropped": 0,
            "Events": [
              { "Seq": 1, "Tick": 1, "Cause": "stock", "Kind": "liquid", "Name": "酒", "Amount": 1 }
            ],
            "OperationIds": [],
            "TransferReceipts": []
          }
        }
        """;

        var decoded = WorldV2CheckpointJsonCodec.Decode<ResourceLedgerWireDto>(json);
        var wireEvent = Assert.Single(decoded.Payload.Events);
        Assert.Null(wireEvent.ResourceIdentity);
        Assert.Null(wireEvent.OperationId);
        Assert.Null(wireEvent.FromAccountId);
        Assert.Null(wireEvent.ToAccountId);

        var restored = ResourceLedger.Restore(decoded.Payload.ToSnapshot());
        var fact = Assert.Single(restored.Entries);
        Assert.Null(fact.ResourceIdentity);
        Assert.Null(fact.OperationId);
        Assert.Null(fact.FromAccountId);
        Assert.Null(fact.ToAccountId);
        Assert.Equal("酒", fact.Name);
    }


    [Fact]
    public void TaggedLedgerEndpointsDistinguishAccountsExternalBoundariesAndLegacyUnknown()
    {
        var world = new WorldInstanceId("world-ledger-endpoints");
        var ship = new RuleResourceAccount(
            new OwnerIdentity(world, ResourceOwnerKind.Ship, "ship-1"), "ship.liquid-inventory");
        var warehouse = new RuleResourceAccount(
            new OwnerIdentity(world, ResourceOwnerKind.Warehouse, "warehouse-1"), "warehouse.liquid-storage");
        var ledger = new ResourceLedger();

        Assert.True(ledger.RecordBatchOnce("operation.tagged.endpoints", 2, new[]
        {
            new ResourceLedgerMutation(
                ResourceCauses.Stock, ResourceKinds.Liquid, "wine", 3,
                ResourceIdentity: "liquid.wine", DisplayName: "酒",
                FromEndpoint: ResourceEndpoint.ExternalSource("world.initialization"),
                ToEndpoint: ResourceEndpoint.ForAccount(ship.AccountId)),
            new ResourceLedgerMutation(
                ResourceCauses.Transfer, ResourceKinds.Liquid, "酒", 2,
                ResourceIdentity: "酒",
                FromEndpoint: ResourceEndpoint.ForAccount(ship.AccountId),
                ToEndpoint: ResourceEndpoint.ForAccount(warehouse.AccountId)),
            new ResourceLedgerMutation(
                ResourceCauses.Discard, ResourceKinds.Liquid, "酒", 1,
                ResourceIdentity: "酒",
                FromEndpoint: ResourceEndpoint.ForAccount(warehouse.AccountId),
                ToEndpoint: ResourceEndpoint.ExternalSink("warehouse.capacity-overflow"))
        }));

        var wire = ResourceLedgerWireDto.FromSnapshot(ledger.Capture());
        var encoded = WorldV2CheckpointJsonCodec.Encode(wire, null);
        var decodedWire = WorldV2CheckpointJsonCodec.Decode<ResourceLedgerWireDto>(encoded).Payload;
        var restored = ResourceLedger.Restore(decodedWire.ToSnapshot());
        Assert.Equal(ResourceEndpointKind.ExternalSource, restored.Entries[0].FromEndpoint.Kind);
        Assert.Equal("liquid.wine", restored.Entries[0].ResourceIdentity);
        Assert.Equal("酒", restored.Entries[0].DisplayName);
        Assert.Equal("酒", restored.Entries[0].Name);
        Assert.Equal(ResourceEndpointKind.Account, restored.Entries[0].ToEndpoint.Kind);
        Assert.Equal(ship.AccountId, restored.Entries[0].ToEndpoint.AccountId);
        Assert.Equal(warehouse.AccountId, restored.Entries[1].ToEndpoint.AccountId);
        Assert.Equal(ResourceEndpointKind.ExternalSink, restored.Entries[2].ToEndpoint.Kind);

        var legacyLedger = new ResourceLedger();
        legacyLedger.Record(3, ResourceCauses.Production, ResourceKinds.Liquid, "蜜", 1);
        Assert.Equal(ResourceEndpointKind.LegacyUnknown, Assert.Single(legacyLedger.Entries).FromEndpoint.Kind);
    }

    [Fact]
    public void TaggedLedgerEndpointMustAgreeWithLegacyAccountProjection()
    {
        var ledger = new ResourceLedger();
        var account = new RuleResourceAccount("warehouse.liquid-storage", "warehouse-1");
        Assert.Throws<DomainValidationException>(() => ledger.RecordBatchOnce("operation.endpoint.mismatch", 1,
            new[]
            {
                new ResourceLedgerMutation(
                    ResourceCauses.Transfer, ResourceKinds.Liquid, "酒", 1,
                    FromAccountId: account.AccountId,
                    FromEndpoint: ResourceEndpoint.ExternalSource("wrong-source"))
            }));
        Assert.False(ledger.HasRecordedOperation("operation.endpoint.mismatch"));
        Assert.Empty(ledger.Entries);
    }





    [Fact]
    public void ExplicitDisplayNameRequiresCanonicalResourceIdentity()
    {
        var ledger = new ResourceLedger();
        Assert.Throws<DomainValidationException>(() =>
            ledger.Record(1, ResourceCauses.Production, ResourceKinds.Liquid,
                "酒", 1, displayName: "酒"));

        Assert.Throws<DomainValidationException>(() =>
            ledger.RecordBatchOnce("missing-canonical-resource-identity", 1,
                new[]
                {
                    new ResourceLedgerMutation(
                        ResourceCauses.Production, ResourceKinds.Liquid, "wine", 1,
                        DisplayName: "酒")
                }));
        Assert.Empty(ledger.Entries);
    }

    [Fact]
    public void DisplayNameAndLegacyNameChangesDoNotChangeOperationFingerprint()
    {
        var ledger = new ResourceLedger();
        const string operationId = "display-name-only-change";
        var original = new ResourceLedgerMutation(
            ResourceCauses.Stock, ResourceKinds.Liquid, "wine", 3,
            From: "生产区", To: "仓库",
            ResourceIdentity: "liquid.wine",
            FromEndpoint: ResourceEndpoint.ExternalSource("production.seed"),
            ToEndpoint: ResourceEndpoint.LegacyUnknown,
            DisplayName: "酒");

        Assert.True(ledger.RecordBatchOnce(operationId, 1, new[] { original }));
        var renamed = original with
        {
            Name = "Wine", DisplayName = "Wine",
            From = "Producer", To = "Warehouse"
        };

        Assert.False(ledger.RecordBatchOnce(operationId, 2, new[] { renamed }));
        var recorded = Assert.Single(ledger.Entries);
        Assert.Equal("liquid.wine", recorded.ResourceIdentity);
        Assert.Equal("酒", recorded.DisplayName);
        Assert.Equal("酒", recorded.Name);

        var changedIdentity = renamed with { ResourceIdentity = "liquid.honey" };
        Assert.Throws<InvalidOperationException>(() =>
            ledger.RecordBatchOnce(operationId, 3, new[] { changedIdentity }));
    }


    [Fact]
    public void SchemaEightOperationFingerprintRemainsIdempotentAfterDisplayNameUpgrade()
    {
        const string operationId = "schema-eight-display-name-upgrade";
        var source = new RuleResourceAccount("workshop.liquid", "workshop-a");
        var target = new RuleResourceAccount("ship.liquid", "ship-a");
        var mutation = new ResourceLedgerMutation(
            ResourceCauses.Transfer, ResourceKinds.Liquid, "wine", 2,
            From: "主台", To: "自机",
            ResourceIdentity: "liquid.wine",
            FromAccountId: source.AccountId,
            ToAccountId: target.AccountId,
            FromEndpoint: ResourceEndpoint.ForAccount(source.AccountId),
            ToEndpoint: ResourceEndpoint.ForAccount(target.AccountId),
            DisplayName: "酒");

        var schema8Mutation = new
        {
            mutation.Cause, mutation.Kind, mutation.Name, mutation.Amount,
            mutation.From, mutation.To, mutation.Reason, mutation.ResourceIdentity,
            mutation.FromAccountId, mutation.ToAccountId, mutation.FromEndpoint, mutation.ToEndpoint
        };
        var schema8Payload = JsonSerializer.Serialize(new
        {
            Mutations = new[] { schema8Mutation },
            Receipt = (ResourceTransferReceipt?)null
        });
        var schema8Fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(schema8Payload)));
        var snapshot = new ResourceLedgerSnapshot(
            8, 1, 0,
            new[]
            {
                new ResourceEvent
                {
                    Seq = 1, Tick = 4, Cause = ResourceCauses.Transfer,
                    Kind = ResourceKinds.Liquid, Name = "酒", DisplayName = "酒",
                    ResourceIdentity = "liquid.wine", OperationId = operationId, Amount = 2,
                    FromAccountId = source.AccountId, ToAccountId = target.AccountId,
                    FromEndpoint = ResourceEndpoint.ForAccount(source.AccountId),
                    ToEndpoint = ResourceEndpoint.ForAccount(target.AccountId)
                }
            },
            new[] { operationId },
            operationFingerprints: new Dictionary<string, string>
            {
                [operationId] = schema8Fingerprint
            });

        var restored = ResourceLedger.Restore(snapshot);
        Assert.False(restored.RecordBatchOnce(operationId, 5, new[] { mutation }));
        Assert.Equal(1, restored.LastSeq);
    }

    [Fact]
    public void LegacyOperationFingerprintRemainsIdempotentAfterTaggedEndpointUpgrade()
    {
        const string operationId = "legacy.operation.endpoint-upgrade";
        var mutation = new ResourceLedgerMutation(
            ResourceCauses.Return, ResourceKinds.Liquid, "酒", 2,
            From: "订单暂存", To: "自机液库", ResourceIdentity: "酒");

        var legacyMutation = new
        {
            mutation.Cause, mutation.Kind, mutation.Name, mutation.Amount,
            mutation.From, mutation.To, mutation.Reason, mutation.ResourceIdentity,
            mutation.FromAccountId, mutation.ToAccountId
        };
        var legacyPayload = JsonSerializer.Serialize(new
        {
            Mutations = new[] { legacyMutation },
            Receipt = (ResourceTransferReceipt?)null
        });
        var legacyFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(legacyPayload)));
        var snapshot = new ResourceLedgerSnapshot(
            8, 1, 0,
            new[]
            {
                new ResourceEvent
                {
                    Seq = 1, Tick = 4, Cause = ResourceCauses.Return,
                    Kind = ResourceKinds.Liquid, Name = "酒", ResourceIdentity = "酒",
                    OperationId = operationId, Amount = 2
                }
            },
            new[] { operationId },
            operationFingerprints: new Dictionary<string, string>
            {
                [operationId] = legacyFingerprint
            });

        var restored = ResourceLedger.Restore(snapshot);
        Assert.False(restored.RecordBatchOnce(operationId, 5, new[] { mutation }));
        Assert.Equal(1, restored.LastSeq);
    }



    [Fact]
    public void CompositeTransferBatchPublishesParentLegReceiptsAndEventsAtomically()
    {
        var ledger = new ResourceLedger(capacity: 8);
        var sourceA = new RuleResourceAccount("workshop.free-liquid-staging", "source-a");
        var targetA = new RuleResourceAccount("workshop.free-liquid-staging", "target-a");
        var sourceB = new RuleResourceAccount("warehouse.liquid-storage", "source-b");
        var targetB = new RuleResourceAccount("workshop.free-liquid-staging", "target-b");

        var first = new ResourceTransferReceipt(
            "batch.operation.leg-a", "connection.a", "port.a.out", "port.a.in",
            RuleResourceKind.Liquid, "liquid.alpha", sourceA, targetA,
            2, 2, 0, 0, ResourceTransferOutcome.Transferred,
            "a-source-before", "a-source-after", "a-target-before", "a-target-after", "allowed");
        var second = new ResourceTransferReceipt(
            "batch.operation.leg-b", "connection.b", "port.b.out", "port.b.in",
            RuleResourceKind.Liquid, "liquid.beta", sourceB, targetB,
            3, 3, 0, 0, ResourceTransferOutcome.Transferred,
            "b-source-before", "b-source-after", "b-target-before", "b-target-after", "allowed");
        var firstMutations = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                "Alpha", 2, From: sourceA.OwnerId, To: targetA.OwnerId,
                ResourceIdentity: "liquid.alpha", DisplayName: "Alpha")
        };
        var secondMutations = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                "Beta", 3, From: sourceB.OwnerId, To: targetB.OwnerId,
                ResourceIdentity: "liquid.beta", DisplayName: "Beta")
        };
        var receipt = new ResourceLedgerTransferBatchReceipt(
            "batch.operation.root", new[] { first, second });
        var legs = new[]
        {
            new ResourceLedgerTransferBatchLeg(first, firstMutations),
            new ResourceLedgerTransferBatchLeg(second, secondMutations)
        };

        Assert.True(ledger.RecordTransferBatchOnce(receipt.OperationId, 50, legs, receipt));
        Assert.False(ledger.RecordTransferBatchOnce(receipt.OperationId, 51, legs, receipt));
        Assert.Equal(2, ledger.LastSeq);
        Assert.Equal(2, ledger.Retained);
        Assert.True(ledger.HasRecordedOperation(receipt.OperationId));
        Assert.True(ledger.HasRecordedOperation(first.OperationId));
        Assert.True(ledger.HasRecordedOperation(second.OperationId));
        Assert.True(ledger.TryGetTransferBatchReceipt(receipt.OperationId, out var storedBatch));
        Assert.NotNull(storedBatch);
        Assert.True(receipt.HasSamePayload(storedBatch!));
        Assert.True(ledger.TryGetTransferReceipt(first.OperationId, out var storedFirst));
        Assert.Equal(first, storedFirst);
        Assert.True(ledger.TryGetTransferReceipt(second.OperationId, out var storedSecond));
        Assert.Equal(second, storedSecond);

        var events = ledger.Entries;
        Assert.Equal(new[] { first.OperationId, second.OperationId }, events.Select(item => item.OperationId));
        Assert.All(events, item => Assert.Equal(receipt.OperationId, item.BatchOperationId));
        Assert.Equal(new[] { "liquid.alpha", "liquid.beta" }, events.Select(item => item.ResourceIdentity));
    }

    [Fact]
    public void CompositeTransferBatchReceiptAndIndexesSurviveCheckpointWireRoundTrip()
    {
        var ledger = new ResourceLedger(capacity: 8);
        var source = new RuleResourceAccount("workshop.free-liquid-staging", "source");
        var target = new RuleResourceAccount("workshop.free-liquid-staging", "target");
        var leg = new ResourceTransferReceipt(
            "batch.roundtrip.leg", "connection.roundtrip", "source.out", "target.in",
            RuleResourceKind.Liquid, "liquid.stable",
            source, target, 4, 4, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");
        var mutations = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                "Stable liquid", 4, From: source.OwnerId, To: target.OwnerId,
                ResourceIdentity: "liquid.stable", DisplayName: "Stable liquid")
        };
        var receipt = new ResourceLedgerTransferBatchReceipt(
            "batch.roundtrip.root", new[] { leg });
        var legs = new[] { new ResourceLedgerTransferBatchLeg(leg, mutations) };

        Assert.True(ledger.RecordTransferBatchOnce(receipt.OperationId, 70, legs, receipt));
        var restored = ResourceLedger.Restore(ledger.Capture());
        Assert.True(restored.TryGetTransferBatchReceipt(receipt.OperationId, out var restoredBatch));
        Assert.NotNull(restoredBatch);
        Assert.True(receipt.HasSamePayload(restoredBatch!));

        var wire = ResourceLedgerWireDto.FromSnapshot(restored.Capture());
        wire.Validate();
        var json = JsonSerializer.Serialize(wire);
        var decoded = JsonSerializer.Deserialize<ResourceLedgerWireDto>(json)
            ?? throw new System.InvalidOperationException("Composite resource ledger wire payload deserialized as null.");
        decoded.Validate();
        var roundTripped = ResourceLedger.Restore(decoded.ToSnapshot());

        Assert.True(roundTripped.TryGetTransferBatchReceipt(receipt.OperationId, out var roundTrippedBatch));
        Assert.NotNull(roundTrippedBatch);
        Assert.True(receipt.HasSamePayload(roundTrippedBatch!));
        Assert.True(roundTripped.TryGetTransferReceipt(leg.OperationId, out var roundTrippedLeg));
        Assert.Equal(leg, roundTrippedLeg);
        var fact = Assert.Single(roundTripped.Entries);
        Assert.Equal(leg.OperationId, fact.OperationId);
        Assert.Equal(receipt.OperationId, fact.BatchOperationId);
        Assert.False(roundTripped.RecordTransferBatchOnce(receipt.OperationId, 71, legs, receipt));
        Assert.Equal(1, roundTripped.LastSeq);
    }

    [Fact]
    public void RestoreRejectsCompositeTransferBatchWithMissingLegReceipt()
    {
        var ledger = new ResourceLedger();
        var source = new RuleResourceAccount("workshop.free-liquid-staging", "source");
        var target = new RuleResourceAccount("workshop.free-liquid-staging", "target");
        var leg = new ResourceTransferReceipt(
            "batch.corrupt.leg", "connection.corrupt", "source.out", "target.in",
            RuleResourceKind.Liquid, "liquid.corrupt",
            source, target, 1, 1, 0, 0, ResourceTransferOutcome.Transferred,
            "source-before", "source-after", "target-before", "target-after", "allowed");
        var mutations = new[]
        {
            new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                "Corrupt", 1, From: source.OwnerId, To: target.OwnerId,
                ResourceIdentity: "liquid.corrupt", DisplayName: "Corrupt")
        };
        var receipt = new ResourceLedgerTransferBatchReceipt(
            "batch.corrupt.root", new[] { leg });
        Assert.True(ledger.RecordTransferBatchOnce(receipt.OperationId, 1,
            new[] { new ResourceLedgerTransferBatchLeg(leg, mutations) }, receipt));

        var snapshot = ledger.Capture();
        var incomplete = new ResourceLedgerSnapshot(
            snapshot.Capacity, snapshot.LastSeq, snapshot.Dropped, snapshot.Entries,
            snapshot.OperationIds, Array.Empty<ResourceTransferReceipt>(),
            snapshot.OperationFingerprints, snapshot.TransferBatchReceipts);

        Assert.Throws<DomainValidationException>(() => ResourceLedger.Restore(incomplete));
    }


    [Fact]
    public void InvalidCompositeLegPublishesNoParentOrEarlierLegState()
    {
        var ledger = new ResourceLedger();
        var firstSource = new RuleResourceAccount("workshop.free-liquid-staging", "first-source");
        var firstTarget = new RuleResourceAccount("workshop.free-liquid-staging", "first-target");
        var secondSource = new RuleResourceAccount("workshop.free-liquid-staging", "second-source");
        var secondTarget = new RuleResourceAccount("workshop.free-liquid-staging", "second-target");
        var first = new ResourceTransferReceipt(
            "batch.atomic.leg-one", "connection.one", "one.out", "one.in",
            RuleResourceKind.Liquid, "liquid.one", firstSource, firstTarget,
            1, 1, 0, 0, ResourceTransferOutcome.Transferred,
            "one-source-before", "one-source-after", "one-target-before", "one-target-after", "allowed");
        var second = new ResourceTransferReceipt(
            "batch.atomic.leg-two", "connection.two", "two.out", "two.in",
            RuleResourceKind.Liquid, "liquid.two", secondSource, secondTarget,
            2, 2, 0, 0, ResourceTransferOutcome.Transferred,
            "two-source-before", "two-source-after", "two-target-before", "two-target-after", "allowed");
        var receipt = new ResourceLedgerTransferBatchReceipt(
            "batch.atomic.root", new[] { first, second });
        var legs = new[]
        {
            new ResourceLedgerTransferBatchLeg(first, new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                    "One", 1, From: firstSource.OwnerId, To: firstTarget.OwnerId, ResourceIdentity: "liquid.one")
            }),
            new ResourceLedgerTransferBatchLeg(second, new[]
            {
                new ResourceLedgerMutation(ResourceCauses.Transfer, ResourceKinds.Liquid,
                    "Two", 1, From: secondSource.OwnerId, To: secondTarget.OwnerId, ResourceIdentity: "liquid.two")
            })
        };

        Assert.Throws<DomainValidationException>(() =>
            ledger.RecordTransferBatchOnce(receipt.OperationId, 1, legs, receipt));

        Assert.Equal(0, ledger.LastSeq);
        Assert.Empty(ledger.Entries);
        Assert.False(ledger.HasRecordedOperation(receipt.OperationId));
        Assert.False(ledger.HasRecordedOperation(first.OperationId));
        Assert.False(ledger.HasRecordedOperation(second.OperationId));
        Assert.False(ledger.TryGetTransferBatchReceipt(receipt.OperationId, out _));
    }

}

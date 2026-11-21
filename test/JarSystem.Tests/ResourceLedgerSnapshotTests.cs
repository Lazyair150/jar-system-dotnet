using JarSystem.Core.Domain;
using JarSystem.Core.Production;
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

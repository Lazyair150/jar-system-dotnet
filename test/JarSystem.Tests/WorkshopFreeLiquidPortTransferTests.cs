using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

public sealed class WorkshopFreeLiquidPortTransferTests
{
    private const string Liquid = "酒";

    private static (Workshop Source, Workshop Target,
        WorkshopFreeLiquidPortAdapter SourceAdapter, WorkshopFreeLiquidPortAdapter TargetAdapter,
        CircuitPortDefinition SourcePort, CircuitPortDefinition TargetPort,
        CircuitConnectionDefinition Connection) CreatePath(
            int sourceCapacity = 10,
            int targetCapacity = 10,
            IEnumerable<string>? sourceBan = null,
            IEnumerable<string>? targetBan = null,
            int sourceAmount = 5)
    {
        var source = new Workshop(
            "master", ban: sourceBan is null ? null : new List<string>(sourceBan),
            maxLiquids: sourceCapacity, ownedInstanceId: "workshop.master.01");
        var target = new Workshop(
            "slave", ban: targetBan is null ? null : new List<string>(targetBan),
            maxLiquids: targetCapacity, ownedInstanceId: "workshop.slave.01");
        if (sourceAmount > 0) source.AddLiquid(Liquid, sourceAmount);

        var sourceAdapter = new WorkshopFreeLiquidPortAdapter(source, "area.master.free-liquid");
        var targetAdapter = new WorkshopFreeLiquidPortAdapter(target, "area.slave.free-liquid");
        var sourcePort = new CircuitPortDefinition(
            "master.free-liquid.out", "area.master.free-liquid",
            ResourcePortDirection.Output, RuleResourceKind.Liquid, null,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId, 1);
        var targetPort = new CircuitPortDefinition(
            "slave.free-liquid.in", "area.slave.free-liquid",
            ResourcePortDirection.Input, RuleResourceKind.Liquid, null,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId, 1);
        var connection = new CircuitConnectionDefinition(
            "connection.workshop-free-liquid", sourcePort.PortId, targetPort.PortId);
        return (source, target, sourceAdapter, targetAdapter, sourcePort, targetPort, connection);
    }

    private static ResourceTransferRequest Request(
        WorkshopFreeLiquidPortAdapter sourceAdapter,
        WorkshopFreeLiquidPortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        int amount)
    {
        var source = sourceAdapter.Preflight(sourcePort, Liquid, amount);
        var target = targetAdapter.Preflight(targetPort, Liquid, amount);
        return new ResourceTransferRequest(
            "operation.workshop-free-liquid.1", "connection.workshop-free-liquid",
            sourcePort.PortId, targetPort.PortId,
            RuleResourceKind.Liquid, Liquid, amount,
            sourceAdapter.Account, targetAdapter.Account,
            source.ObservedVersionToken, target.ObservedVersionToken);
    }

    [Fact]
    public void CommitMovesExactAmountBetweenIndependentWorkshopFreeAccounts()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, 2);
        var prepared = WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        var receipt = prepared.Commit();

        Assert.True(prepared.IsCommitted);
        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.Equal(3, path.Source.LiquidStorage[Liquid]);
        Assert.Equal(2, path.Target.LiquidStorage[Liquid]);
        Assert.Same(receipt, prepared.Commit());
    }

    [Fact]
    public void TargetWorkshopBanRejectsWithoutChangingEitherAccount()
    {
        var path = CreatePath(targetBan: new[] { Liquid });
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, 2);
        var prepared = WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("banned", receipt.ReasonCode);
        Assert.Equal(5, path.Source.LiquidStorage[Liquid]);
        Assert.Equal(0, path.Target.LiquidStorage[Liquid]);
    }

    [Fact]
    public void SourceWorkshopBanRejectsExtractionEvenIfLegacyBalanceExists()
    {
        var path = CreatePath(sourceBan: new[] { Liquid }, sourceAmount: 0);
        var legacyBalance = new Dictionary<string, int>(path.Source.LiquidStorage)
        {
            [Liquid] = 3
        };
        path.Source.RestoreLiquidStorageSnapshot(legacyBalance);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, 2);
        var prepared = WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("banned", receipt.ReasonCode);
        Assert.Equal(3, path.Source.LiquidStorage[Liquid]);
        Assert.Equal(0, path.Target.LiquidStorage[Liquid]);
    }

    [Fact]
    public void InsufficientSourceBalanceRejectsWholeTransfer()
    {
        var path = CreatePath(sourceAmount: 1);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, 2);
        var prepared = WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("insufficient", receipt.ReasonCode);
        Assert.Equal(1, path.Source.LiquidStorage[Liquid]);
        Assert.Equal(0, path.Target.LiquidStorage[Liquid]);
    }

    [Fact]
    public void TargetCapacityRejectsWholeTransfer()
    {
        var path = CreatePath(targetCapacity: 1);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, 2);
        var prepared = WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("over-capacity", receipt.ReasonCode);
        Assert.Equal(5, path.Source.LiquidStorage[Liquid]);
        Assert.Equal(0, path.Target.LiquidStorage[Liquid]);
    }

    [Fact]
    public void VersionChangeAfterPrepareStopsTransferWithoutMutation()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, 2);
        var prepared = WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        path.Source.AddLiquid("蜜", 1);
        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Undetermined, receipt.Outcome);
        Assert.Equal("account.version.changed", receipt.ReasonCode);
        Assert.Equal(5, path.Source.LiquidStorage[Liquid]);
        Assert.Equal(0, path.Target.LiquidStorage[Liquid]);
    }

    [Fact]
    public void ResourceLedgerPersistsOperationIdAndPreventsCrossInstanceReplay()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, 2);
        var ledger = new ResourceLedger();
        var prepared = WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target,
            ledger, () => 121);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.True(ledger.HasRecordedOperation(request.OperationId));
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(121, entry.Tick);
        Assert.Equal(ResourceCauses.Transfer, entry.Cause);
        Assert.Equal(ResourceKinds.Liquid, entry.Kind);
        Assert.Equal(Liquid, entry.Name);
        Assert.Equal(2, entry.Amount);

        var restoredLedger = ResourceLedger.Restore(ledger.Capture());
        var replay = WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target,
            restoredLedger, () => 122);

        Assert.Equal(receipt, replay.Commit());
        Assert.True(replay.IsCommitted);
        Assert.Equal(3, path.Source.LiquidStorage[Liquid]);
        Assert.Equal(2, path.Target.LiquidStorage[Liquid]);
    }

    [Fact]
    public void SameOperationIdWithDifferentTransferPayloadIsRejected()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, 2);
        var ledger = new ResourceLedger();
        var first = WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target,
            ledger, () => 123);
        var committed = first.Commit();

        var conflictingRequest = request with { Amount = 1 };
        var conflicting = WorkshopFreeLiquidPortTransfer.Prepare(
            conflictingRequest, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target,
            ResourceLedger.Restore(ledger.Capture()), () => 124);

        Assert.Throws<System.InvalidOperationException>(() => conflicting.Commit());
        Assert.Equal(3, path.Source.LiquidStorage[Liquid]);
        Assert.Equal(2, path.Target.LiquidStorage[Liquid]);
        Assert.Equal(ResourceTransferOutcome.Transferred, committed.Outcome);
    }

    [Fact]
    public void LedgerPublicationFailureRestoresBothWorkshopFreeAccounts()
    {
        var path = CreatePath();

        Assert.Throws<System.InvalidOperationException>(() =>
            LegacyWorkshopLiquidTransfer.Execute(
                path.Source, path.Target,
                new Dictionary<string, int> { [Liquid] = 2 },
                () => throw new System.InvalidOperationException("injected ledger publication failure")));

        Assert.Equal(5, path.Source.LiquidStorage[Liquid]);
        Assert.Equal(0, path.Target.LiquidStorage[Liquid]);
    }

    [Fact]
    public void PrepareRejectsAdaptersBoundToDifferentWorkshopInstances()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, 1);
        var otherSource = new Workshop("other", ownedInstanceId: "workshop.other.01");

        Assert.Throws<System.ArgumentException>(() => WorkshopFreeLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, otherSource, path.Target));
    }
}

using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

public sealed class OrderLiquidPortTransferTests
{
    private const int OrderId = 101;

    private static (WorkshopLiquidStaging Source, WorkshopLiquidStaging Target,
        WorkshopOrderLiquidPortAdapter SourceAdapter, WorkshopOrderLiquidPortAdapter TargetAdapter,
        CircuitPortDefinition SourcePort, CircuitPortDefinition TargetPort,
        CircuitConnectionDefinition Connection) CreatePath(
            IEnumerable<string>? sourceBan = null,
            IEnumerable<string>? targetBan = null)
    {
        var source = new WorkshopLiquidStaging(10, sourceBan);
        var target = new WorkshopLiquidStaging(10, targetBan);
        var sourceAdapter = new WorkshopOrderLiquidPortAdapter(
            source, "workshop.source.01", "area.source", OrderId);
        var targetAdapter = new WorkshopOrderLiquidPortAdapter(
            target, "workshop.target.01", "area.target", OrderId);
        var sourcePort = new CircuitPortDefinition(
            "source.order.liquid.out", "area.source", ResourcePortDirection.Output,
            RuleResourceKind.Liquid, null,
            WorkshopOrderLiquidPortAdapter.SupportedPolicyId, 1);
        var targetPort = new CircuitPortDefinition(
            "target.order.liquid.in", "area.target", ResourcePortDirection.Input,
            RuleResourceKind.Liquid, null,
            WorkshopOrderLiquidPortAdapter.SupportedPolicyId, 1);
        var connection = new CircuitConnectionDefinition(
            "connection.order.liquid", sourcePort.PortId, targetPort.PortId);
        return (source, target, sourceAdapter, targetAdapter, sourcePort, targetPort, connection);
    }

    private static ResourceTransferRequest Request(
        WorkshopOrderLiquidPortAdapter sourceAdapter,
        WorkshopOrderLiquidPortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        string identity,
        int amount)
    {
        var source = sourceAdapter.Preflight(sourcePort, identity, amount);
        var target = targetAdapter.Preflight(targetPort, identity, amount);
        return new ResourceTransferRequest(
            "operation.order.101.1", "connection.order.liquid",
            sourcePort.PortId, targetPort.PortId,
            RuleResourceKind.Liquid, identity, amount,
            sourceAdapter.Account, targetAdapter.Account,
            source.ObservedVersionToken, target.ObservedVersionToken);
    }

    [Fact]
    public void CommitMovesExactOrderLiquidAndReturnsStableReceipt()
    {
        var path = CreatePath();
        path.Source.Receive(OrderId, "酒", 4);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = OrderLiquidPortTransfer.Prepare(
            request, OrderId, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        var receipt = prepared.Commit();
        var replay = prepared.Commit();

        Assert.Same(receipt, replay);
        Assert.True(prepared.IsCommitted);
        Assert.True(prepared.IsCompleted);
        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.Equal(2, receipt.TransferredAmount);
        Assert.Equal(0, receipt.RetainedAtSourceAmount);
        Assert.Equal(0, receipt.DiscardedAmount);
        Assert.Equal(2, path.Source.GetAmount(OrderId, "酒"));
        Assert.Equal(2, path.Target.GetAmount(OrderId, "酒"));
        Assert.NotEqual(receipt.SourceVersionBefore, receipt.SourceVersionAfter);
        Assert.NotEqual(receipt.TargetVersionBefore, receipt.TargetVersionAfter);
    }

    [Fact]
    public void VersionChangeAfterPrepareStopsTransferWithoutMutationByParticipant()
    {
        var path = CreatePath();
        path.Source.Receive(OrderId, "酒", 3);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = OrderLiquidPortTransfer.Prepare(
            request, OrderId, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        // A separate operation changes the source after Prepare.
        path.Source.Receive(OrderId, "酒", 1);
        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Undetermined, receipt.Outcome);
        Assert.Equal("account.version.changed", receipt.ReasonCode);
        Assert.Equal(4, path.Source.GetAmount(OrderId, "酒"));
        Assert.Equal(0, path.Target.GetAmount(OrderId, "酒"));
        Assert.Equal(receipt.SourceVersionBefore, receipt.SourceVersionAfter);
        Assert.Equal(receipt.TargetVersionBefore, receipt.TargetVersionAfter);
    }

    [Fact]
    public void TargetWorkshopBanRejectsBeforeAnyAccountMutation()
    {
        var path = CreatePath(targetBan: new[] { "蜜" });
        path.Source.Receive(OrderId, "蜜", 3);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "蜜", 2);
        var prepared = OrderLiquidPortTransfer.Prepare(
            request, OrderId, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("banned", receipt.ReasonCode);
        Assert.Equal(3, path.Source.GetAmount(OrderId, "蜜"));
        Assert.Equal(0, path.Target.GetAmount(OrderId, "蜜"));
    }

    [Fact]
    public void CancelBeforeCommitIsTerminalAndDoesNotMoveResources()
    {
        var path = CreatePath();
        path.Source.Receive(OrderId, "酒", 2);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 1);
        var prepared = OrderLiquidPortTransfer.Prepare(
            request, OrderId, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        prepared.Cancel();
        prepared.Cancel();
        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("transfer.cancelled", receipt.ReasonCode);
        Assert.Equal(2, path.Source.GetAmount(OrderId, "酒"));
        Assert.Equal(0, path.Target.GetAmount(OrderId, "酒"));
    }

    [Fact]
    public void ResourceLedgerPersistsOperationIdAndPreventsCrossInstanceReplay()
    {
        var path = CreatePath();
        path.Source.Receive(OrderId, "酒", 3);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var ledger = new ResourceLedger();
        var prepared = OrderLiquidPortTransfer.Prepare(
            request, OrderId, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target,
            ledger, () => 77);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.True(ledger.HasRecordedOperation(request.OperationId));
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(77, entry.Tick);
        Assert.Equal(ResourceCauses.Transfer, entry.Cause);
        Assert.Equal(ResourceKinds.Liquid, entry.Kind);
        Assert.Equal("酒", entry.Name);
        Assert.Equal(2, entry.Amount);
        Assert.Equal(path.SourceAdapter.Account.OwnerId, entry.From);
        Assert.Equal(path.TargetAdapter.Account.OwnerId, entry.To);

        var restoredLedger = ResourceLedger.Restore(ledger.Capture());
        var replay = OrderLiquidPortTransfer.Prepare(
            request, OrderId, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target,
            restoredLedger, () => 78);

        Assert.Throws<System.InvalidOperationException>(() => replay.Commit());
        Assert.Equal(1, restoredLedger.Entries.Count);
        Assert.Equal(1, path.Source.GetAmount(OrderId, "酒"));
        Assert.Equal(2, path.Target.GetAmount(OrderId, "酒"));
    }

    [Fact]
    public void PrepareRejectsAdaptersThatAreNotBoundToTheSuppliedStagingInstances()
    {
        var path = CreatePath();
        path.Source.Receive(OrderId, "酒", 2);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 1);
        var differentSource = new WorkshopLiquidStaging(10);

        Assert.Throws<System.ArgumentException>(() => OrderLiquidPortTransfer.Prepare(
            request, OrderId, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, differentSource, path.Target));
        Assert.Equal(2, path.Source.GetAmount(OrderId, "酒"));
        Assert.Equal(0, differentSource.GetAmount(OrderId, "酒"));
    }

    [Fact]
    public void PrepareRejectsRequestThatDoesNotMatchConnectionOrAccounts()
    {
        var path = CreatePath();
        path.Source.Receive(OrderId, "酒", 2);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 1);

        Assert.Throws<System.ArgumentException>(() => OrderLiquidPortTransfer.Prepare(
            request with { ConnectionId = "connection.other" },
            OrderId, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target));
        Assert.Throws<System.ArgumentException>(() => OrderLiquidPortTransfer.Prepare(
            request with { TargetAccount = path.SourceAdapter.Account },
            OrderId, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target));
    }
}

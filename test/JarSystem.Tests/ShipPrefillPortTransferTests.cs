using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

public sealed class ShipPrefillPortTransferTests
{
    private static (ShipLiquidInventory Ship, WorkshopLiquidStaging Staging,
        ShipLiquidInventoryPortAdapter SourceAdapter, WorkshopPrefillLiquidPortAdapter TargetAdapter,
        CircuitPortDefinition SourcePort, CircuitPortDefinition TargetPort,
        CircuitConnectionDefinition Connection) CreatePath(
            int shipCapacity = 10,
            int stagingCapacity = 10,
            IEnumerable<string>? targetBan = null)
    {
        var ship = new ShipLiquidInventory(shipCapacity,
            new Dictionary<string, int> { ["酒"] = 5, ["蜜"] = 3 });
        var staging = new WorkshopLiquidStaging(stagingCapacity, targetBan);
        var sourceAdapter = new ShipLiquidInventoryPortAdapter(
            ship, "ship.01", "area.ship.liquids");
        var targetAdapter = new WorkshopPrefillLiquidPortAdapter(
            staging, "workshop.master.01", "area.workshop.prefill");
        var sourcePort = new CircuitPortDefinition(
            "ship.liquid.out", "area.ship.liquids", ResourcePortDirection.Output,
            RuleResourceKind.Liquid, null,
            ShipLiquidInventoryPortAdapter.SupportedPolicyId, 1);
        var targetPort = new CircuitPortDefinition(
            "workshop.prefill.in", "area.workshop.prefill", ResourcePortDirection.Input,
            RuleResourceKind.Liquid, null,
            WorkshopPrefillLiquidPortAdapter.SupportedPolicyId, 1);
        var connection = new CircuitConnectionDefinition(
            "connection.ship-to-prefill", sourcePort.PortId, targetPort.PortId);
        return (ship, staging, sourceAdapter, targetAdapter, sourcePort, targetPort, connection);
    }

    private static ResourceTransferRequest Request(
        ShipLiquidInventoryPortAdapter sourceAdapter,
        WorkshopPrefillLiquidPortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        string liquid,
        int amount)
    {
        var source = sourceAdapter.Preflight(sourcePort, liquid, amount);
        var target = targetAdapter.Preflight(targetPort, liquid, amount);
        return new ResourceTransferRequest(
            "operation.ship-prefill.1", "connection.ship-to-prefill",
            sourcePort.PortId, targetPort.PortId,
            RuleResourceKind.Liquid, liquid, amount,
            sourceAdapter.Account, targetAdapter.Account,
            source.ObservedVersionToken, target.ObservedVersionToken);
    }

    [Fact]
    public void CommitMovesExactAmountIntoIndependentPrefillAccount()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = ShipPrefillPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Ship, path.Staging);

        var receipt = prepared.Commit();

        Assert.True(prepared.IsCommitted);
        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.Equal(2, receipt.TransferredAmount);
        Assert.Equal(3, path.Ship.GetAmount("酒"));
        Assert.Equal(2, path.Staging.PrefillBalances["酒"]);
        Assert.Equal(0, path.Staging.GetAmount(101, "酒"));
        Assert.Equal(0, path.Ship.ReservedReturnCapacity);
        Assert.Equal(2, path.Staging.OccupiedCapacity);
        Assert.NotEqual(receipt.SourceVersionBefore, receipt.SourceVersionAfter);
        Assert.NotEqual(receipt.TargetVersionBefore, receipt.TargetVersionAfter);
        Assert.Same(receipt, prepared.Commit());
    }

    [Fact]
    public void ResourceLedgerPersistsOperationIdAndPreventsCrossInstanceReplay()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var ledger = new ResourceLedger();
        var prepared = ShipPrefillPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Ship, path.Staging,
            ledger, () => 91);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.True(ledger.HasRecordedOperation(request.OperationId));
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(91, entry.Tick);
        Assert.Equal(ResourceCauses.Flow, entry.Cause);
        Assert.Equal(ResourceKinds.Liquid, entry.Kind);
        Assert.Equal("酒", entry.Name);
        Assert.Equal(2, entry.Amount);
        Assert.Equal(path.SourceAdapter.Account.OwnerId, entry.From);
        Assert.Equal(path.TargetAdapter.Account.OwnerId, entry.To);

        var restoredLedger = ResourceLedger.Restore(ledger.Capture());
        var replay = ShipPrefillPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Ship, path.Staging,
            restoredLedger, () => 92);

        Assert.Throws<System.InvalidOperationException>(() => replay.Commit());
        Assert.Single(restoredLedger.Entries);
        Assert.Equal(3, path.Ship.GetAmount("酒"));
        Assert.Equal(2, path.Staging.PrefillBalances["酒"]);
    }

    [Fact]
    public void ExactPrefillReservationRollbackRestoresBothAuthoritativeAccounts()
    {
        var path = CreatePath();

        var transaction = path.Staging.BeginPrefillTransferFromShip(path.Ship, "酒", 2);

        Assert.Equal(3, path.Ship.GetAmount("酒"));
        Assert.Equal(2, path.Ship.ReservedReturnCapacity);
        Assert.Equal(2, path.Staging.PrefillBalances["酒"]);
        Assert.Throws<System.InvalidOperationException>(() => path.Staging.Receive(101, "盐", 1));

        transaction.Rollback();

        Assert.Equal(5, path.Ship.GetAmount("酒"));
        Assert.Equal(0, path.Ship.ReservedReturnCapacity);
        Assert.Empty(path.Staging.PrefillBalances);
        Assert.Equal(0, path.Staging.OccupiedCapacity);
    }

    [Fact]
    public void TargetWorkshopBanRejectsPrefillWithoutWithdrawingFromShip()
    {
        var path = CreatePath(targetBan: new[] { "蜜" });
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "蜜", 2);
        var prepared = ShipPrefillPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Ship, path.Staging);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("banned", receipt.ReasonCode);
        Assert.Equal(3, path.Ship.GetAmount("蜜"));
        Assert.Empty(path.Staging.PrefillBalances);
        Assert.Equal(0, path.Ship.ReservedReturnCapacity);
    }

    [Fact]
    public void VersionChangeAfterPrepareReturnsUndeterminedWithoutTransfer()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = ShipPrefillPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Ship, path.Staging);

        path.Ship.Produce("盐", 1);
        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Undetermined, receipt.Outcome);
        Assert.Equal("account.version.changed", receipt.ReasonCode);
        Assert.Equal(5, path.Ship.GetAmount("酒"));
        Assert.Empty(path.Staging.PrefillBalances);
        Assert.Equal(0, path.Ship.ReservedReturnCapacity);
    }

    [Fact]
    public void InsufficientPrefillCapacityRejectsWholeTransfer()
    {
        var path = CreatePath(stagingCapacity: 1);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = ShipPrefillPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Ship, path.Staging);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("over-capacity", receipt.ReasonCode);
        Assert.Equal(5, path.Ship.GetAmount("酒"));
        Assert.Empty(path.Staging.PrefillBalances);
    }

    [Fact]
    public void CancelBeforeCommitDoesNotReserveOrMoveEitherAccount()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = ShipPrefillPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Ship, path.Staging);

        prepared.Cancel();
        prepared.Cancel();
        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("transfer.cancelled", receipt.ReasonCode);
        Assert.Equal(5, path.Ship.GetAmount("酒"));
        Assert.Empty(path.Staging.PrefillBalances);
        Assert.Equal(0, path.Ship.ReservedReturnCapacity);
    }

    [Fact]
    public void PrepareRejectsAdaptersBoundToDifferentAccountInstances()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 1);
        var otherShip = new ShipLiquidInventory(10);

        Assert.Throws<System.ArgumentException>(() => ShipPrefillPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, otherShip, path.Staging));
    }
}

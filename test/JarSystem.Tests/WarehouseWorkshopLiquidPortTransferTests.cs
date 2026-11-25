using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

public sealed class WarehouseWorkshopLiquidPortTransferTests
{
    private static (Warehouse Warehouse, Workshop Workshop,
        WarehouseLiquidStoragePortAdapter SourceAdapter, WorkshopFreeLiquidPortAdapter TargetAdapter,
        CircuitPortDefinition SourcePort, CircuitPortDefinition TargetPort,
        CircuitConnectionDefinition Connection) CreatePath(
            int workshopLiquidCapacity = 10,
            IEnumerable<string>? targetBan = null)
    {
        var warehouse = new Warehouse();
        warehouse.ProduceLiquid("酒", 5);
        var workshop = new Workshop(
            "master", ban: targetBan is null ? null : new List<string>(targetBan),
            maxLiquids: workshopLiquidCapacity,
            ownedInstanceId: "workshop.master.01");
        var sourceAdapter = new WarehouseLiquidStoragePortAdapter(
            warehouse, "production.01", "area.production.warehouse");
        var targetAdapter = new WorkshopFreeLiquidPortAdapter(
            workshop, "area.workshop.free-liquid");
        var sourcePort = new CircuitPortDefinition(
            "production.warehouse.liquid.out", "area.production.warehouse",
            ResourcePortDirection.Output, RuleResourceKind.Liquid, null,
            WarehouseLiquidStoragePortAdapter.SupportedPolicyId, 1);
        var targetPort = new CircuitPortDefinition(
            "workshop.free.liquid.in", "area.workshop.free-liquid",
            ResourcePortDirection.Input, RuleResourceKind.Liquid, null,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId, 1);
        var connection = new CircuitConnectionDefinition(
            "connection.warehouse-to-workshop", sourcePort.PortId, targetPort.PortId);
        return (warehouse, workshop, sourceAdapter, targetAdapter,
            sourcePort, targetPort, connection);
    }

    private static ResourceTransferRequest Request(
        WarehouseLiquidStoragePortAdapter sourceAdapter,
        WorkshopFreeLiquidPortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        string liquid,
        int amount)
    {
        var source = sourceAdapter.Preflight(sourcePort, liquid, amount);
        var target = targetAdapter.Preflight(targetPort, liquid, amount);
        return new ResourceTransferRequest(
            "operation.warehouse-workshop.1", "connection.warehouse-to-workshop",
            sourcePort.PortId, targetPort.PortId,
            RuleResourceKind.Liquid, liquid, amount,
            sourceAdapter.Account, targetAdapter.Account,
            source.ObservedVersionToken, target.ObservedVersionToken);
    }

    [Fact]
    public void CommitMovesExactAmountIntoWorkshopFreeLiquidAccount()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = WarehouseWorkshopLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop);

        var receipt = prepared.Commit();

        Assert.True(prepared.IsCommitted);
        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.Equal(3, path.Warehouse.LiquidCount("酒"));
        Assert.Equal(2, path.Workshop.LiquidStorage["酒"]);
        Assert.Equal(2, receipt.TransferredAmount);
        Assert.Same(receipt, prepared.Commit());
    }

    [Fact]
    public void WorkshopBanRejectsWarehouseFlowAndLeavesLiquidInWarehouse()
    {
        var path = CreatePath(targetBan: new[] { "酒" });
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = WarehouseWorkshopLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("banned", receipt.ReasonCode);
        Assert.Equal(5, path.Warehouse.LiquidCount("酒"));
        Assert.Equal(0, path.Workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void WorkshopCapacityRejectsWholeTransferWithoutWarehouseWithdrawal()
    {
        var path = CreatePath(workshopLiquidCapacity: 1);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = WarehouseWorkshopLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal(5, path.Warehouse.LiquidCount("酒"));
        Assert.Equal(0, path.Workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void VersionChangeAfterPrepareStopsTransferWithoutMutation()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var prepared = WarehouseWorkshopLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop);

        path.Warehouse.ProduceLiquid("蜜", 1);
        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Undetermined, receipt.Outcome);
        Assert.Equal("account.version.changed", receipt.ReasonCode);
        Assert.Equal(5, path.Warehouse.LiquidCount("酒"));
        Assert.Equal(0, path.Workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void ResourceLedgerPersistsOperationIdAndPreventsCrossInstanceReplay()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 2);
        var ledger = new ResourceLedger();
        var prepared = WarehouseWorkshopLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop,
            ledger, () => 103);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.True(ledger.HasRecordedOperation(request.OperationId));
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(103, entry.Tick);
        Assert.Equal(ResourceCauses.Flow, entry.Cause);
        Assert.Equal("酒", entry.Name);
        Assert.Equal(2, entry.Amount);

        var restoredLedger = ResourceLedger.Restore(ledger.Capture());
        var replay = WarehouseWorkshopLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop,
            restoredLedger, () => 104);

        Assert.Equal(receipt, replay.Commit());
        Assert.True(replay.IsCommitted);
        Assert.Equal(3, path.Warehouse.LiquidCount("酒"));
        Assert.Equal(2, path.Workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void LedgerPublicationFailureRestoresBothAuthoritativeAccounts()
    {
        var path = CreatePath();

        Assert.Throws<System.InvalidOperationException>(() =>
            WarehouseWorkshopLiquidTransfer.TryExecute(
                path.Warehouse, path.Workshop, "酒", 2, out _,
                () => throw new System.InvalidOperationException("injected ledger publication failure")));

        Assert.Equal(5, path.Warehouse.LiquidCount("酒"));
        Assert.Equal(0, path.Workshop.LiquidStorage["酒"]);
    }

    [Fact]
    public void PrepareRejectsAdaptersBoundToDifferentAccountInstances()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, "酒", 1);
        var otherWarehouse = new Warehouse();

        Assert.Throws<System.ArgumentException>(() => WarehouseWorkshopLiquidPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, otherWarehouse, path.Workshop));
    }
}

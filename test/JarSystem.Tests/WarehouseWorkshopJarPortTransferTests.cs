using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

public sealed class WarehouseWorkshopJarPortTransferTests
{
    private static (Warehouse Warehouse, Workshop Workshop, Jar Jar,
        WarehouseJarStoragePortAdapter SourceAdapter, WorkshopJarStoragePortAdapter TargetAdapter,
        CircuitPortDefinition SourcePort, CircuitPortDefinition TargetPort,
        CircuitConnectionDefinition Connection) CreatePath(
            int workshopJarCapacity = 10,
            IEnumerable<string>? targetBan = null)
    {
        var warehouse = new Warehouse();
        var jar = new Jar("圆罐", "蜜", id: 301);
        warehouse.StoreJars(new[] { jar });
        var workshop = new Workshop(
            "master", ban: targetBan is null ? null : new List<string>(targetBan),
            maxJars: workshopJarCapacity,
            ownedInstanceId: "workshop.master.01");
        var sourceAdapter = new WarehouseJarStoragePortAdapter(
            warehouse, "production.01", "area.production.warehouse");
        var targetAdapter = new WorkshopJarStoragePortAdapter(
            workshop, "area.workshop.jars");
        var sourcePort = new CircuitPortDefinition(
            "production.warehouse.jar.out", "area.production.warehouse",
            ResourcePortDirection.Output, RuleResourceKind.Jar, null,
            WarehouseJarStoragePortAdapter.SupportedPolicyId, 1);
        var targetPort = new CircuitPortDefinition(
            "workshop.jar.in", "area.workshop.jars",
            ResourcePortDirection.Input, RuleResourceKind.Jar, null,
            WorkshopJarStoragePortAdapter.SupportedPolicyId, 1);
        var connection = new CircuitConnectionDefinition(
            "connection.warehouse-jar-to-workshop", sourcePort.PortId, targetPort.PortId);
        return (warehouse, workshop, jar, sourceAdapter, targetAdapter,
            sourcePort, targetPort, connection);
    }

    private static ResourceTransferRequest Request(
        WarehouseJarStoragePortAdapter sourceAdapter,
        WorkshopJarStoragePortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        int jarId)
    {
        var identity = jarId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var source = sourceAdapter.Preflight(sourcePort, identity, 1);
        var target = targetAdapter.Preflight(targetPort, identity, 1);
        return new ResourceTransferRequest(
            "operation.warehouse-workshop-jar.1", "connection.warehouse-jar-to-workshop",
            sourcePort.PortId, targetPort.PortId,
            RuleResourceKind.Jar, identity, 1,
            sourceAdapter.Account, targetAdapter.Account,
            source.ObservedVersionToken, target.ObservedVersionToken);
    }

    [Fact]
    public void CommitMovesTheSameJarEntityAndDoesNotApplyWorkshopBanToJarContents()
    {
        var path = CreatePath(targetBan: new[] { "蜜" });
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);
        var prepared = WarehouseWorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop);

        var receipt = prepared.Commit();

        Assert.True(prepared.IsCommitted);
        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.Empty(path.Warehouse.Jars);
        Assert.Same(path.Jar, Assert.Single(path.Workshop.Jars));
        Assert.Equal("蜜", path.Jar.Liquid);
        Assert.Same(receipt, prepared.Commit());
    }

    [Fact]
    public void WorkshopJarCapacityRejectsWithoutRemovingJarFromWarehouse()
    {
        var path = CreatePath(workshopJarCapacity: 0);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);
        var prepared = WarehouseWorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("jar.storage.capacity", receipt.ReasonCode);
        Assert.Same(path.Jar, Assert.Single(path.Warehouse.Jars));
        Assert.Empty(path.Workshop.Jars);
    }

    [Fact]
    public void VersionChangeAfterPrepareStopsJarOwnershipTransfer()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);
        var prepared = WarehouseWorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop);

        path.Warehouse.StoreJars(new[] { new Jar("圆罐", id: 302) });
        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Undetermined, receipt.Outcome);
        Assert.Equal("account.version.changed", receipt.ReasonCode);
        Assert.Contains(path.Jar, path.Warehouse.Jars);
        Assert.Empty(path.Workshop.Jars);
    }

    [Fact]
    public void ResourceLedgerPersistsJarFlowOperationIdAndPreventsReplay()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);
        var ledger = new ResourceLedger();
        var prepared = WarehouseWorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop,
            ledger, () => 111);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.True(ledger.HasRecordedOperation(request.OperationId));
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(111, entry.Tick);
        Assert.Equal(ResourceCauses.Flow, entry.Cause);
        Assert.Equal(ResourceKinds.Jar, entry.Kind);
        Assert.Equal("圆罐", entry.Name);
        Assert.Equal(1, entry.Amount);

        var restoredLedger = ResourceLedger.Restore(ledger.Capture());
        var replay = WarehouseWorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Warehouse, path.Workshop,
            restoredLedger, () => 112);

        Assert.Throws<System.InvalidOperationException>(() => replay.Commit());
        Assert.Empty(path.Warehouse.Jars);
        Assert.Same(path.Jar, Assert.Single(path.Workshop.Jars));
    }

    [Fact]
    public void LedgerPublicationFailureRestoresExactJarEntityToWarehouse()
    {
        var path = CreatePath();

        Assert.Throws<System.InvalidOperationException>(() =>
            WarehouseWorkshopJarTransfer.TryExecute(
                path.Warehouse, path.Workshop, path.Jar.Id!.Value, out _,
                () => throw new System.InvalidOperationException("injected ledger publication failure")));

        Assert.Same(path.Jar, Assert.Single(path.Warehouse.Jars));
        Assert.Empty(path.Workshop.Jars);
    }

    [Fact]
    public void LedgerFailureRestoresJarToItsExactOriginalWarehousePosition()
    {
        var warehouse = new Warehouse();
        var first = new Jar("圆罐", id: 501);
        var moved = new Jar("方罐", id: 502);
        var last = new Jar("圆罐", id: 503);
        warehouse.StoreJars(new[] { first, moved, last });
        var workshop = new Workshop("master", ownedInstanceId: "workshop.master.01");
        var before = new List<Jar>(warehouse.Jars);

        Assert.Throws<System.InvalidOperationException>(() =>
            WarehouseWorkshopJarTransfer.TryExecute(
                warehouse, workshop, moved.Id!.Value, out _,
                () => throw new System.InvalidOperationException("injected ledger publication failure")));

        Assert.Equal(before.Count, warehouse.Jars.Count);
        for (var i = 0; i < before.Count; i++)
            Assert.Same(before[i], warehouse.Jars[i]);
        Assert.Empty(workshop.Jars);
    }

    [Fact]
    public void PrepareRejectsAdaptersBoundToDifferentStorageInstances()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);

        Assert.Throws<System.ArgumentException>(() => WarehouseWorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, new Warehouse(), path.Workshop));
    }
}

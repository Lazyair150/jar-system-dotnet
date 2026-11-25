using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

public sealed class WorkshopJarPortTransferTests
{
    private static (Workshop Source, Workshop Target, Jar Jar,
        WorkshopJarStoragePortAdapter SourceAdapter, WorkshopJarStoragePortAdapter TargetAdapter,
        CircuitPortDefinition SourcePort, CircuitPortDefinition TargetPort,
        CircuitConnectionDefinition Connection) CreatePath(
            int targetJarCapacity = 10,
            IEnumerable<string>? sourceBan = null,
            IEnumerable<string>? targetBan = null)
    {
        var source = new Workshop(
            "master", ban: sourceBan is null ? null : new List<string>(sourceBan),
            ownedInstanceId: "workshop.master.01");
        var target = new Workshop(
            "slave", ban: targetBan is null ? null : new List<string>(targetBan),
            maxJars: targetJarCapacity, ownedInstanceId: "workshop.slave.01");
        var jar = new Jar("圆罐", "蜜", id: 401);
        source.AddJar(jar);

        var sourceAdapter = new WorkshopJarStoragePortAdapter(source, "area.master.jars");
        var targetAdapter = new WorkshopJarStoragePortAdapter(target, "area.slave.jars");
        var sourcePort = new CircuitPortDefinition(
            "master.jar.out", "area.master.jars",
            ResourcePortDirection.Output, RuleResourceKind.Jar, null,
            WorkshopJarStoragePortAdapter.SupportedPolicyId, 1);
        var targetPort = new CircuitPortDefinition(
            "slave.jar.in", "area.slave.jars",
            ResourcePortDirection.Input, RuleResourceKind.Jar, null,
            WorkshopJarStoragePortAdapter.SupportedPolicyId, 1);
        var connection = new CircuitConnectionDefinition(
            "connection.workshop-jar", sourcePort.PortId, targetPort.PortId);
        return (source, target, jar, sourceAdapter, targetAdapter,
            sourcePort, targetPort, connection);
    }

    private static ResourceTransferRequest Request(
        WorkshopJarStoragePortAdapter sourceAdapter,
        WorkshopJarStoragePortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        int jarId)
    {
        var identity = jarId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var source = sourceAdapter.Preflight(sourcePort, identity, 1);
        var target = targetAdapter.Preflight(targetPort, identity, 1);
        return new ResourceTransferRequest(
            "operation.workshop-jar.1", "connection.workshop-jar",
            sourcePort.PortId, targetPort.PortId,
            RuleResourceKind.Jar, identity, 1,
            sourceAdapter.Account, targetAdapter.Account,
            source.ObservedVersionToken, target.ObservedVersionToken);
    }

    [Fact]
    public void CommitMovesSameJarEntityWithoutApplyingFreeLiquidBanToJarContents()
    {
        var path = CreatePath(targetBan: new[] { "蜜" });
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);
        var prepared = WorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        var receipt = prepared.Commit();

        Assert.True(prepared.IsCommitted);
        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.Empty(path.Source.Jars);
        Assert.Same(path.Jar, Assert.Single(path.Target.Jars));
        Assert.Equal("蜜", path.Jar.Liquid);
    }

    [Fact]
    public void TargetJarCapacityRejectsWithoutChangingOwnership()
    {
        var path = CreatePath(targetJarCapacity: 0);
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);
        var prepared = WorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Rejected, receipt.Outcome);
        Assert.Equal("jar.storage.capacity", receipt.ReasonCode);
        Assert.Same(path.Jar, Assert.Single(path.Source.Jars));
        Assert.Empty(path.Target.Jars);
    }

    [Fact]
    public void VersionChangeAfterPrepareStopsJarTransfer()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);
        var prepared = WorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target);

        path.Source.AddJar(new Jar("圆罐", id: 402));
        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Undetermined, receipt.Outcome);
        Assert.Equal("account.version.changed", receipt.ReasonCode);
        Assert.Contains(path.Jar, path.Source.Jars);
        Assert.Empty(path.Target.Jars);
    }

    [Fact]
    public void ResourceLedgerPersistsWorkshopJarTransferOperationId()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);
        var ledger = new ResourceLedger();
        var prepared = WorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target,
            ledger, () => 131);

        var receipt = prepared.Commit();

        Assert.Equal(ResourceTransferOutcome.Transferred, receipt.Outcome);
        Assert.True(ledger.HasRecordedOperation(request.OperationId));
        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(131, entry.Tick);
        Assert.Equal(ResourceCauses.Transfer, entry.Cause);
        Assert.Equal(ResourceKinds.Jar, entry.Kind);
        Assert.Equal("圆罐", entry.Name);
        Assert.Equal(1, entry.Amount);

        var restoredLedger = ResourceLedger.Restore(ledger.Capture());
        var replay = WorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter, path.Source, path.Target,
            restoredLedger, () => 132);

        Assert.Equal(receipt, replay.Commit());
        Assert.True(replay.IsCommitted);
        Assert.Empty(path.Source.Jars);
        Assert.Same(path.Jar, Assert.Single(path.Target.Jars));
    }

    [Fact]
    public void LedgerPublicationFailureRestoresExactJarEntityToSourceWorkshop()
    {
        var path = CreatePath();

        Assert.Throws<System.InvalidOperationException>(() =>
            WorkshopWorkshopJarTransfer.TryExecute(
                path.Source, path.Target, path.Jar.Id!.Value, out _,
                () => throw new System.InvalidOperationException("injected ledger publication failure")));

        Assert.Same(path.Jar, Assert.Single(path.Source.Jars));
        Assert.Empty(path.Target.Jars);
    }

    [Fact]
    public void PrepareRejectsAdaptersBoundToDifferentWorkshopInstances()
    {
        var path = CreatePath();
        var request = Request(path.SourceAdapter, path.TargetAdapter,
            path.SourcePort, path.TargetPort, path.Jar.Id!.Value);

        Assert.Throws<System.ArgumentException>(() => WorkshopJarPortTransfer.Prepare(
            request, path.Connection, path.SourcePort, path.TargetPort,
            path.SourceAdapter, path.TargetAdapter,
            new Workshop("other", ownedInstanceId: "workshop.other.01"), path.Target));
    }
}

using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// Atomic discrete Jar ownership transfer from the Production Warehouse to Workshop storage.
/// The Jar remains the same entity; its internal liquid is not subjected to Workshop staging Ban.
/// </summary>
internal static class WarehouseWorkshopJarTransfer
{
    public static bool TryExecute(
        Warehouse warehouse,
        Workshop workshop,
        int jarId,
        out string? rejectionReason,
        System.Action? beforeFinalize = null)
    {
        var workshopBefore = workshop.Jars.ToArray();
        if (!warehouse.TryTakeJarExact(jarId, out var jar) || jar is null)
        {
            rejectionReason = "jar.identity.not-owned";
            return false;
        }

        try
        {
            if (!workshop.TransferIn(jar))
                throw new System.InvalidOperationException("Workshop Jar storage changed after port preflight.");
            beforeFinalize?.Invoke();
            rejectionReason = null;
            return true;
        }
        catch (System.Exception transferError)
        {
            try
            {
                if (workshop.Jars.Any(item => ReferenceEquals(item, jar)) &&
                    !ReferenceEquals(workshop.TransferOut(jar), jar))
                    throw new System.InvalidOperationException("Could not remove the transferred Jar from Workshop during compensation.");
                warehouse.RestoreJarExact(jar);
                if (workshop.Jars.Count != workshopBefore.Length ||
                    !workshop.Jars.SequenceEqual(workshopBefore, ReferenceEqualityComparer.Instance))
                    throw new System.InvalidOperationException("Workshop Jar account did not restore its exact pre-transfer ownership.");
            }
            catch (System.Exception rollbackError)
            {
                throw new System.AggregateException(
                    "Warehouse-to-Workshop Jar transfer failed and exact ownership compensation also failed.",
                    transferError, rollbackError);
            }
            throw;
        }
    }
}

/// <summary>One-shot transaction participant for Production Warehouse Jar -> Workshop Jar storage.</summary>
public sealed class PreparedWarehouseWorkshopJarPortTransfer
{
    private readonly ResourceTransferRequest _request;
    private readonly Warehouse _warehouse;
    private readonly Workshop _workshop;
    private readonly WarehouseJarStoragePortAdapter _sourceAdapter;
    private readonly WorkshopJarStoragePortAdapter _targetAdapter;
    private readonly CircuitPortDefinition _sourcePort;
    private readonly CircuitPortDefinition _targetPort;
    private readonly ResourceLedger? _ledger;
    private readonly System.Func<int>? _ledgerTickProvider;
    private ResourceTransferReceipt? _receipt;
    private bool _cancelled;
    private bool _faulted;
    private bool _duplicateOperation;

    internal PreparedWarehouseWorkshopJarPortTransfer(
        ResourceTransferRequest request,
        Warehouse warehouse,
        Workshop workshop,
        WarehouseJarStoragePortAdapter sourceAdapter,
        WorkshopJarStoragePortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        ResourceLedger? ledger,
        System.Func<int>? ledgerTickProvider)
    {
        _request = request;
        _warehouse = warehouse;
        _workshop = workshop;
        _sourceAdapter = sourceAdapter;
        _targetAdapter = targetAdapter;
        _sourcePort = sourcePort;
        _targetPort = targetPort;
        _ledger = ledger;
        _ledgerTickProvider = ledgerTickProvider;
    }

    public string OperationId => _request.OperationId;
    public bool IsCompleted => _receipt is not null || _cancelled || _faulted || _duplicateOperation;
    public bool IsCommitted => _receipt?.Outcome == ResourceTransferOutcome.Transferred;

    public ResourceTransferReceipt Commit()
    {
        if (_receipt is not null) return _receipt;
        if (_faulted)
            throw new System.InvalidOperationException("This transfer participant faulted during commit and cannot be retried.");
        if (_duplicateOperation)
            throw new System.InvalidOperationException(ResourcePortTransferGuards.DuplicateOperationMessage);
        if (_cancelled)
            return _receipt = NoMutation(
                ResourceTransferOutcome.Rejected, "transfer.cancelled",
                _sourceAdapter.Preflight(_sourcePort, _request.ResourceIdentity, _request.Amount),
                _targetAdapter.Preflight(_targetPort, _request.ResourceIdentity, _request.Amount));

        var source = _sourceAdapter.Preflight(_sourcePort, _request.ResourceIdentity, _request.Amount);
        var target = _targetAdapter.Preflight(_targetPort, _request.ResourceIdentity, _request.Amount);
        var preflightReceipt = ResourcePortTransferGuards.EvaluateCommitPreflight(
            _request, source, target, _ledger,
            () => _duplicateOperation = true,
            (outcome, reason) => NoMutation(outcome, reason, source, target));
        if (preflightReceipt is not null)
            return _receipt = preflightReceipt;

        var ledgerTick = _ledger is null ? 0 : _ledgerTickProvider!();
        if (ledgerTick < 0)
            return _receipt = NoMutation(
                ResourceTransferOutcome.Rejected, "ledger.tick.invalid", source, target);

        try
        {
            var jarId = int.Parse(_request.ResourceIdentity, System.Globalization.CultureInfo.InvariantCulture);
            if (!WarehouseWorkshopJarTransfer.TryExecute(
                _warehouse, _workshop, jarId, out _,
                () =>
                {
                    if (_ledger is not null)
                    {
                        var jar = _workshop.Jars.Single(item => item.Id == jarId);
                        if (!_ledger.RecordBatchOnce(_request.OperationId, ledgerTick, new[]
                        {
                            new ResourceLedgerMutation(
                                ResourceCauses.Flow, ResourceKinds.Jar, jar.Shape, 1,
                                From: _request.SourceAccount.OwnerId, To: _request.TargetAccount.OwnerId)
                        }))
                            throw new System.InvalidOperationException(
                                "The transfer OperationId was recorded while its physical commit was in progress.");
                    }
                }))
            {
                var sourceAfterFailure = _sourceAdapter.Preflight(
                    _sourcePort, _request.ResourceIdentity, _request.Amount);
                var targetAfterFailure = _targetAdapter.Preflight(
                    _targetPort, _request.ResourceIdentity, _request.Amount);
                var changed = sourceAfterFailure.ObservedVersionToken != source.ObservedVersionToken ||
                    targetAfterFailure.ObservedVersionToken != target.ObservedVersionToken;
                return _receipt = NoMutation(
                    changed ? ResourceTransferOutcome.Undetermined : ResourceTransferOutcome.Rejected,
                    changed ? "account.version.changed" : "transfer.precondition.rejected",
                    sourceAfterFailure, targetAfterFailure);
            }

            var sourceAfter = _sourceAdapter.Preflight(
                _sourcePort, _request.ResourceIdentity, _request.Amount);
            var targetAfter = _targetAdapter.Preflight(
                _targetPort, _request.ResourceIdentity, _request.Amount);
            _receipt = new ResourceTransferReceipt(
                _request.OperationId, _request.ConnectionId,
                _request.SourcePortId, _request.TargetPortId,
                _request.ResourceKind, _request.ResourceIdentity,
                _request.SourceAccount, _request.TargetAccount,
                _request.Amount, _request.Amount, 0, 0,
                ResourceTransferOutcome.Transferred,
                source.ObservedVersionToken, sourceAfter.ObservedVersionToken,
                target.ObservedVersionToken, targetAfter.ObservedVersionToken,
                "allowed");
            _receipt.Validate();
            return _receipt;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public void Cancel()
    {
        if (_receipt is not null)
            throw new System.InvalidOperationException("A completed transfer participant cannot be cancelled.");
        if (_faulted)
            throw new System.InvalidOperationException("A faulted transfer participant cannot be cancelled.");
        if (_duplicateOperation)
            throw new System.InvalidOperationException("A duplicate OperationId cannot be cancelled or replayed.");
        if (_cancelled) return;
        _cancelled = true;
    }

    private ResourceTransferReceipt NoMutation(
        ResourceTransferOutcome outcome,
        string reasonCode,
        ResourcePortPreflight source,
        ResourcePortPreflight target)
    {
        var receipt = new ResourceTransferReceipt(
            _request.OperationId, _request.ConnectionId,
            _request.SourcePortId, _request.TargetPortId,
            _request.ResourceKind, _request.ResourceIdentity,
            _request.SourceAccount, _request.TargetAccount,
            _request.Amount, 0, _request.Amount, 0, outcome,
            source.ObservedVersionToken, source.ObservedVersionToken,
            target.ObservedVersionToken, target.ObservedVersionToken,
            reasonCode);
        receipt.Validate();
        return receipt;
    }
}

/// <summary>Prepare an exact Warehouse Jar Output -> Workshop Jar Input transfer.</summary>
public static class WarehouseWorkshopJarPortTransfer
{
    public static PreparedWarehouseWorkshopJarPortTransfer Prepare(
        ResourceTransferRequest request,
        CircuitConnectionDefinition connection,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        WarehouseJarStoragePortAdapter sourceAdapter,
        WorkshopJarStoragePortAdapter targetAdapter,
        Warehouse warehouse,
        Workshop workshop,
        ResourceLedger? ledger = null,
        System.Func<int>? ledgerTickProvider = null)
    {
        System.ArgumentNullException.ThrowIfNull(request);
        System.ArgumentNullException.ThrowIfNull(connection);
        System.ArgumentNullException.ThrowIfNull(sourcePort);
        System.ArgumentNullException.ThrowIfNull(targetPort);
        System.ArgumentNullException.ThrowIfNull(sourceAdapter);
        System.ArgumentNullException.ThrowIfNull(targetAdapter);
        System.ArgumentNullException.ThrowIfNull(warehouse);
        System.ArgumentNullException.ThrowIfNull(workshop);
        request.Validate();
        ResourcePortTransferGuards.ValidateLedgerPair(ledger, ledgerTickProvider);

        if (connection.ConnectionId != request.ConnectionId ||
            connection.SourcePortId != request.SourcePortId ||
            connection.TargetPortId != request.TargetPortId)
            throw new System.ArgumentException("Transfer request does not match the supplied Connection.");
        if (sourcePort.PortId != connection.SourcePortId ||
            targetPort.PortId != connection.TargetPortId ||
            sourcePort.Direction != ResourcePortDirection.Output ||
            targetPort.Direction != ResourcePortDirection.Input ||
            sourcePort.ResourceKind != RuleResourceKind.Jar ||
            targetPort.ResourceKind != RuleResourceKind.Jar)
            throw new System.ArgumentException("Warehouse-to-Workshop Jar transfer requires Jar Output -> Input ports.");
        if (request.ResourceKind != RuleResourceKind.Jar ||
            request.SourceAccount != sourceAdapter.Account ||
            request.TargetAccount != targetAdapter.Account)
            throw new System.ArgumentException("Transfer request account identities do not match the supplied adapters.");
        if (!sourceAdapter.IsBoundTo(warehouse) || !targetAdapter.IsBoundTo(workshop))
            throw new System.ArgumentException("Adapters must be bound to the exact supplied Warehouse and Workshop instances.");

        var source = sourceAdapter.Preflight(sourcePort, request.ResourceIdentity, request.Amount);
        var target = targetAdapter.Preflight(targetPort, request.ResourceIdentity, request.Amount);
        source.Validate();
        target.Validate();
        if (source.Account != request.SourceAccount || target.Account != request.TargetAccount)
            throw new System.ArgumentException("Port preflight returned an unexpected authoritative account.");

        return new PreparedWarehouseWorkshopJarPortTransfer(
            request, warehouse, workshop, sourceAdapter, targetAdapter,
            sourcePort, targetPort, ledger, ledgerTickProvider);
    }
}

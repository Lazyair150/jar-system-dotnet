using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// Atomic exact transfer from the Production Warehouse liquid account into a Workshop's
/// independent free-liquid staging account. Workshop Ban and per-liquid capacity remain
/// authoritative; rejected liquid stays in the Warehouse.
/// </summary>
internal static class WarehouseWorkshopLiquidTransfer
{
    public static bool TryExecute(
        Warehouse warehouse,
        Workshop workshop,
        string liquid,
        int amount,
        out string? rejectionReason,
        Action? beforeFinalize = null)
    {
        ArgumentNullException.ThrowIfNull(warehouse);
        ArgumentNullException.ThrowIfNull(workshop);
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: false);

        var warehouseBefore = warehouse.LiquidCount(liquid);
        var workshopBefore = new Dictionary<string, int>(workshop.LiquidStorage, StringComparer.Ordinal);
        if (!warehouse.TryWithdrawLiquidExact(liquid, amount))
        {
            rejectionReason = "warehouse.balance.changed";
            return false;
        }

        try
        {
            if (!workshop.AddLiquid(liquid, amount))
                throw new InvalidOperationException("Workshop free-liquid account changed after port preflight.");
            beforeFinalize?.Invoke();
            rejectionReason = null;
            return true;
        }
        catch (Exception transferError)
        {
            try
            {
                warehouse.RestoreLiquidBalance(liquid, warehouseBefore);
                workshop.RestoreLiquidStorageSnapshot(workshopBefore);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Warehouse-to-Workshop liquid transfer failed and exact two-account compensation also failed.",
                    transferError, rollbackError);
            }
            throw;
        }
    }
}

/// <summary>One-shot transaction participant for Production Warehouse -> Workshop free-liquid staging.</summary>
public sealed class PreparedWarehouseWorkshopLiquidPortTransfer
{
    private readonly ResourceTransferRequest _request;
    private readonly Warehouse _warehouse;
    private readonly Workshop _workshop;
    private readonly WarehouseLiquidStoragePortAdapter _sourceAdapter;
    private readonly WorkshopFreeLiquidPortAdapter _targetAdapter;
    private readonly CircuitPortDefinition _sourcePort;
    private readonly CircuitPortDefinition _targetPort;
    private readonly ResourceLedger? _ledger;
    private readonly Func<int>? _ledgerTickProvider;
    private ResourceTransferReceipt? _receipt;
    private bool _cancelled;
    private bool _faulted;
    private bool _duplicateOperation;

    internal PreparedWarehouseWorkshopLiquidPortTransfer(
        ResourceTransferRequest request,
        Warehouse warehouse,
        Workshop workshop,
        WarehouseLiquidStoragePortAdapter sourceAdapter,
        WorkshopFreeLiquidPortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        ResourceLedger? ledger,
        Func<int>? ledgerTickProvider)
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
            throw new InvalidOperationException("This transfer participant faulted during commit and cannot be retried.");
        if (_duplicateOperation)
            throw new InvalidOperationException(ResourcePortTransferGuards.DuplicateOperationMessage);
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
            if (!WarehouseWorkshopLiquidTransfer.TryExecute(
                _warehouse, _workshop, _request.ResourceIdentity, _request.Amount, out _,
                () =>
                {
                    if (_ledger is not null &&
                        !_ledger.RecordBatchOnce(_request.OperationId, ledgerTick, new[]
                        {
                            new ResourceLedgerMutation(
                                ResourceCauses.Flow, ResourceKinds.Liquid, _request.ResourceIdentity,
                                _request.Amount, From: _request.SourceAccount.OwnerId, To: _request.TargetAccount.OwnerId)
                        }))
                        throw new InvalidOperationException(
                            "The transfer OperationId was recorded while its physical commit was in progress.");
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
            throw new InvalidOperationException("A completed transfer participant cannot be cancelled.");
        if (_faulted)
            throw new InvalidOperationException("A faulted transfer participant cannot be cancelled.");
        if (_duplicateOperation)
            throw new InvalidOperationException("A duplicate OperationId cannot be cancelled or replayed.");
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

/// <summary>Prepare an exact Production Warehouse liquid Output -> Workshop free-liquid Input transfer.</summary>
public static class WarehouseWorkshopLiquidPortTransfer
{
    public static PreparedWarehouseWorkshopLiquidPortTransfer Prepare(
        ResourceTransferRequest request,
        CircuitConnectionDefinition connection,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        WarehouseLiquidStoragePortAdapter sourceAdapter,
        WorkshopFreeLiquidPortAdapter targetAdapter,
        Warehouse warehouse,
        Workshop workshop,
        ResourceLedger? ledger = null,
        Func<int>? ledgerTickProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sourcePort);
        ArgumentNullException.ThrowIfNull(targetPort);
        ArgumentNullException.ThrowIfNull(sourceAdapter);
        ArgumentNullException.ThrowIfNull(targetAdapter);
        ArgumentNullException.ThrowIfNull(warehouse);
        ArgumentNullException.ThrowIfNull(workshop);
        request.Validate();
        ResourcePortTransferGuards.ValidateLedgerPair(ledger, ledgerTickProvider);

        if (connection.ConnectionId != request.ConnectionId ||
            connection.SourcePortId != request.SourcePortId ||
            connection.TargetPortId != request.TargetPortId)
            throw new ArgumentException("Transfer request does not match the supplied Connection.");
        if (sourcePort.PortId != connection.SourcePortId ||
            targetPort.PortId != connection.TargetPortId ||
            sourcePort.Direction != ResourcePortDirection.Output ||
            targetPort.Direction != ResourcePortDirection.Input ||
            sourcePort.ResourceKind != RuleResourceKind.Liquid ||
            targetPort.ResourceKind != RuleResourceKind.Liquid)
            throw new ArgumentException("Warehouse-to-Workshop transfer requires Liquid Output -> Input ports.");
        if (request.ResourceKind != RuleResourceKind.Liquid ||
            request.SourceAccount != sourceAdapter.Account ||
            request.TargetAccount != targetAdapter.Account)
            throw new ArgumentException("Transfer request account identities do not match the supplied adapters.");
        if (!sourceAdapter.IsBoundTo(warehouse) || !targetAdapter.IsBoundTo(workshop))
            throw new ArgumentException("Adapters must be bound to the exact supplied Warehouse and Workshop instances.");

        var source = sourceAdapter.Preflight(sourcePort, request.ResourceIdentity, request.Amount);
        var target = targetAdapter.Preflight(targetPort, request.ResourceIdentity, request.Amount);
        source.Validate();
        target.Validate();
        if (source.Account != request.SourceAccount || target.Account != request.TargetAccount)
            throw new ArgumentException("Port preflight returned an unexpected authoritative account.");

        return new PreparedWarehouseWorkshopLiquidPortTransfer(
            request, warehouse, workshop, sourceAdapter, targetAdapter,
            sourcePort, targetPort, ledger, ledgerTickProvider);
    }
}

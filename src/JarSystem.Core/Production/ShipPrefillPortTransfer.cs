using System;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// One-shot participant for an exact Ship Liquid Inventory -> Workshop prefill transfer.
/// It reuses the existing Ship return-capacity reservation and WorkshopPrefillTransaction;
/// it never routes through the Workshop order-liquid or free-liquid accounts.
/// </summary>
public sealed class PreparedShipPrefillPortTransfer
{
    private readonly ResourceTransferRequest _request;
    private readonly ShipLiquidInventory _ship;
    private readonly WorkshopLiquidStaging _staging;
    private readonly ShipLiquidInventoryPortAdapter _sourceAdapter;
    private readonly WorkshopPrefillLiquidPortAdapter _targetAdapter;
    private readonly CircuitPortDefinition _sourcePort;
    private readonly CircuitPortDefinition _targetPort;
    private readonly ResourceLedger? _ledger;
    private readonly Func<int>? _ledgerTickProvider;
    private ResourceTransferReceipt? _receipt;
    private bool _cancelled;
    private bool _faulted;
    private bool _duplicateOperation;

    internal PreparedShipPrefillPortTransfer(
        ResourceTransferRequest request,
        ShipLiquidInventory ship,
        WorkshopLiquidStaging staging,
        ShipLiquidInventoryPortAdapter sourceAdapter,
        WorkshopPrefillLiquidPortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        ResourceLedger? ledger,
        Func<int>? ledgerTickProvider)
    {
        _request = request;
        _ship = ship;
        _staging = staging;
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

    /// <summary>
    /// Commit is idempotent for this prepared instance. When a ResourceLedger is supplied,
    /// its persisted OperationIds also prevent cross-instance/process replay; without it,
    /// the caller must provide a durable operation journal.
    /// </summary>
    public ResourceTransferReceipt Commit()
    {
        if (_receipt is not null) return _receipt;
        if (_faulted)
            throw new InvalidOperationException("This transfer participant faulted during commit and cannot be retried.");
        if (_duplicateOperation)
            throw new InvalidOperationException("This OperationId was already committed; reconcile from the authoritative ledger instead of replaying.");
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
            var transaction = _staging.BeginPrefillTransferFromShip(
                _ship, _request.ResourceIdentity, _request.Amount);
            ResourceTransferReceipt? committedReceipt = null;
            try
            {
                transaction.Commit(() =>
                {
                    committedReceipt = ResourcePortTransferCommitCoordinator.Finalize(
                        _request, source, target,
                        () => _sourceAdapter.Preflight(_sourcePort, _request.ResourceIdentity, _request.Amount),
                        () => _targetAdapter.Preflight(_targetPort, _request.ResourceIdentity, _request.Amount),
                        _ledger, ledgerTick,
                        new[]
                        {
                            new ResourceLedgerMutation(
                                ResourceCauses.Flow, ResourceKinds.Liquid, _request.ResourceIdentity,
                                _request.Amount, From: _request.SourceAccount.OwnerId, To: _request.TargetAccount.OwnerId)
                        });
                });
            }
            catch (Exception commitError)
            {
                if (!transaction.IsCompleted)
                {
                    try
                    {
                        transaction.Rollback();
                    }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException(
                            "Ship-to-prefill commit failed and its compensation also failed.",
                            commitError, rollbackError);
                    }
                }
                throw;
            }

            _receipt = committedReceipt
                ?? throw new InvalidOperationException("Ship-to-prefill transfer completed without a validated receipt.");
            return _receipt;
        }
        catch
        {
            // If the physical prefill transaction has already committed, a later ledger or
            // receipt failure is ambiguous. Preserve the fault and require reconciliation.
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

/// <summary>Prepare an exact Ship inventory Output -> Workshop prefill Input transfer.</summary>
public static class ShipPrefillPortTransfer
{
    public static PreparedShipPrefillPortTransfer Prepare(
        ResourceTransferRequest request,
        CircuitConnectionDefinition connection,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        ShipLiquidInventoryPortAdapter sourceAdapter,
        WorkshopPrefillLiquidPortAdapter targetAdapter,
        ShipLiquidInventory ship,
        WorkshopLiquidStaging staging,
        ResourceLedger? ledger = null,
        Func<int>? ledgerTickProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sourcePort);
        ArgumentNullException.ThrowIfNull(targetPort);
        ArgumentNullException.ThrowIfNull(sourceAdapter);
        ArgumentNullException.ThrowIfNull(targetAdapter);
        ArgumentNullException.ThrowIfNull(ship);
        ArgumentNullException.ThrowIfNull(staging);
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
            throw new ArgumentException("Ship-to-prefill transfer requires Liquid Output -> Input ports.");
        if (request.ResourceKind != RuleResourceKind.Liquid ||
            request.SourceAccount != sourceAdapter.Account ||
            request.TargetAccount != targetAdapter.Account)
            throw new ArgumentException("Transfer request account identities do not match the supplied adapters.");
        if (!sourceAdapter.IsBoundTo(ship) || !targetAdapter.IsBoundTo(staging))
            throw new ArgumentException("Adapters must be bound to the exact supplied Ship and Workshop staging instances.");

        var source = sourceAdapter.Preflight(sourcePort, request.ResourceIdentity, request.Amount);
        var target = targetAdapter.Preflight(targetPort, request.ResourceIdentity, request.Amount);
        source.Validate();
        target.Validate();
        if (source.Account != request.SourceAccount || target.Account != request.TargetAccount)
            throw new ArgumentException("Port preflight returned an unexpected authoritative account.");

        return new PreparedShipPrefillPortTransfer(
            request, ship, staging, sourceAdapter, targetAdapter, sourcePort, targetPort,
            ledger, ledgerTickProvider);
    }
}

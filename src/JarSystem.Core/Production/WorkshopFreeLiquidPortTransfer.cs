using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>One-shot exact transfer between two distinct Workshop free-liquid staging accounts.</summary>
public sealed class PreparedWorkshopFreeLiquidPortTransfer
{
    private readonly ResourceTransferRequest _request;
    private readonly Workshop _sourceWorkshop;
    private readonly Workshop _targetWorkshop;
    private readonly WorkshopFreeLiquidPortAdapter _sourceAdapter;
    private readonly WorkshopFreeLiquidPortAdapter _targetAdapter;
    private readonly CircuitPortDefinition _sourcePort;
    private readonly CircuitPortDefinition _targetPort;
    private readonly ResourceLedger? _ledger;
    private readonly Func<int>? _ledgerTickProvider;
    private ResourceTransferReceipt? _receipt;
    private bool _cancelled;
    private bool _faulted;
    private bool _duplicateOperation;

    internal PreparedWorkshopFreeLiquidPortTransfer(
        ResourceTransferRequest request,
        Workshop sourceWorkshop,
        Workshop targetWorkshop,
        WorkshopFreeLiquidPortAdapter sourceAdapter,
        WorkshopFreeLiquidPortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        ResourceLedger? ledger,
        Func<int>? ledgerTickProvider)
    {
        _request = request;
        _sourceWorkshop = sourceWorkshop;
        _targetWorkshop = targetWorkshop;
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
            var batch = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [_request.ResourceIdentity] = _request.Amount
            };
            ResourceTransferReceipt? committedReceipt = null;
            LegacyWorkshopLiquidTransfer.Execute(
                _sourceWorkshop, _targetWorkshop, batch,
                () =>
                {
                    committedReceipt = ResourcePortTransferCommitCoordinator.Finalize(
                        _request, source, target,
                        () => _sourceAdapter.Preflight(_sourcePort, _request.ResourceIdentity, _request.Amount),
                        () => _targetAdapter.Preflight(_targetPort, _request.ResourceIdentity, _request.Amount),
                        _ledger, ledgerTick,
                        new[]
                        {
                            new ResourceLedgerMutation(
                                ResourceCauses.Transfer, ResourceKinds.Liquid, _request.ResourceIdentity,
                                _request.Amount, From: _request.SourceAccount.OwnerId, To: _request.TargetAccount.OwnerId,
                                FromAccountId: _request.SourceAccount.AccountId,
                                ToAccountId: _request.TargetAccount.AccountId,
                                FromEndpoint: ResourceEndpoint.ForAccount(_request.SourceAccount.AccountId),
                                ToEndpoint: ResourceEndpoint.ForAccount(_request.TargetAccount.AccountId))
                        });
                });

            _receipt = committedReceipt
                ?? throw new InvalidOperationException("Physical transfer completed without a prepared receipt.");
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

/// <summary>Prepare a single-liquid Workshop free-staging Output -> Input transfer.</summary>
public static class WorkshopFreeLiquidPortTransfer
{
    public static PreparedWorkshopFreeLiquidPortTransfer Prepare(
        ResourceTransferRequest request,
        CircuitConnectionDefinition connection,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        WorkshopFreeLiquidPortAdapter sourceAdapter,
        WorkshopFreeLiquidPortAdapter targetAdapter,
        Workshop sourceWorkshop,
        Workshop targetWorkshop,
        ResourceLedger? ledger = null,
        Func<int>? ledgerTickProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sourcePort);
        ArgumentNullException.ThrowIfNull(targetPort);
        ArgumentNullException.ThrowIfNull(sourceAdapter);
        ArgumentNullException.ThrowIfNull(targetAdapter);
        ArgumentNullException.ThrowIfNull(sourceWorkshop);
        ArgumentNullException.ThrowIfNull(targetWorkshop);
        request.Validate();
        ResourcePortTransferGuards.ValidateLedgerPair(ledger, ledgerTickProvider);

        if (ReferenceEquals(sourceWorkshop, targetWorkshop))
            throw new ArgumentException("Source and target must be distinct Workshop instances.");
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
            throw new ArgumentException("Workshop free-liquid transfer requires Liquid Output -> Input ports.");
        if (request.ResourceKind != RuleResourceKind.Liquid ||
            request.SourceAccount != sourceAdapter.Account ||
            request.TargetAccount != targetAdapter.Account)
            throw new ArgumentException("Transfer request account identities do not match the supplied adapters.");
        if (!sourceAdapter.IsBoundTo(sourceWorkshop) || !targetAdapter.IsBoundTo(targetWorkshop))
            throw new ArgumentException("Adapters must be bound to the exact supplied Workshop instances.");

        var source = sourceAdapter.Preflight(sourcePort, request.ResourceIdentity, request.Amount);
        var target = targetAdapter.Preflight(targetPort, request.ResourceIdentity, request.Amount);
        source.Validate();
        target.Validate();
        if (source.Account != request.SourceAccount || target.Account != request.TargetAccount)
            throw new ArgumentException("Port preflight returned an unexpected authoritative account.");

        return new PreparedWorkshopFreeLiquidPortTransfer(
            request, sourceWorkshop, targetWorkshop, sourceAdapter, targetAdapter,
            sourcePort, targetPort, ledger, ledgerTickProvider);
    }
}

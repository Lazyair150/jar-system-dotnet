using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// One-shot transactional participant for an exact order-liquid account transfer.
/// Prepare is read-only; Commit revalidates both account versions and delegates the
/// synchronous all-or-nothing mutation to OrderLiquidTransfer.
/// This participant intentionally supports one liquid identity per request.
/// </summary>
public sealed class PreparedOrderLiquidPortTransfer
{
    private readonly ResourceTransferRequest _request;
    private readonly int _orderId;
    private readonly WorkshopLiquidStaging _sourceStaging;
    private readonly WorkshopLiquidStaging _targetStaging;
    private readonly WorkshopOrderLiquidPortAdapter _sourceAdapter;
    private readonly WorkshopOrderLiquidPortAdapter _targetAdapter;
    private readonly CircuitPortDefinition _sourcePort;
    private readonly CircuitPortDefinition _targetPort;
    private readonly ResourceLedger? _ledger;
    private readonly Func<int>? _ledgerTickProvider;
    private ResourceTransferReceipt? _receipt;
    private bool _cancelled;
    private bool _faulted;
    private bool _duplicateOperation;

    internal PreparedOrderLiquidPortTransfer(
        ResourceTransferRequest request,
        int orderId,
        WorkshopLiquidStaging sourceStaging,
        WorkshopLiquidStaging targetStaging,
        WorkshopOrderLiquidPortAdapter sourceAdapter,
        WorkshopOrderLiquidPortAdapter targetAdapter,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        ResourceLedger? ledger,
        Func<int>? ledgerTickProvider)
    {
        _request = request;
        _orderId = orderId;
        _sourceStaging = sourceStaging;
        _targetStaging = targetStaging;
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
            return _receipt = CreateNoMutationReceipt(
                ResourceTransferOutcome.Rejected, "transfer.cancelled",
                _sourceAdapter.Preflight(_sourcePort, _request.ResourceIdentity, _request.Amount),
                _targetAdapter.Preflight(_targetPort, _request.ResourceIdentity, _request.Amount));

        var source = _sourceAdapter.Preflight(_sourcePort, _request.ResourceIdentity, _request.Amount);
        var target = _targetAdapter.Preflight(_targetPort, _request.ResourceIdentity, _request.Amount);
        var preflightReceipt = ResourcePortTransferGuards.EvaluateCommitPreflight(
            _request, source, target, _ledger,
            () => _duplicateOperation = true,
            (outcome, reason) => CreateNoMutationReceipt(outcome, reason, source, target));
        if (preflightReceipt is not null)
            return _receipt = preflightReceipt;

        var ledgerTick = _ledger is null ? 0 : _ledgerTickProvider!();
        if (ledgerTick < 0)
            return _receipt = CreateNoMutationReceipt(
                ResourceTransferOutcome.Rejected, "ledger.tick.invalid", source, target);

        try
        {
            var batch = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [_request.ResourceIdentity] = _request.Amount
            };
            ResourceTransferReceipt? committedReceipt = null;
            if (!OrderLiquidTransfer.TryExecute(
                _orderId, _sourceStaging, _targetStaging, batch, out _,
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
                }))
            {
                var sourceAfterFailure = _sourceAdapter.Preflight(
                    _sourcePort, _request.ResourceIdentity, _request.Amount);
                var targetAfterFailure = _targetAdapter.Preflight(
                    _targetPort, _request.ResourceIdentity, _request.Amount);
                var changed = sourceAfterFailure.ObservedVersionToken != source.ObservedVersionToken ||
                    targetAfterFailure.ObservedVersionToken != target.ObservedVersionToken;
                return _receipt = CreateNoMutationReceipt(
                    changed ? ResourceTransferOutcome.Undetermined : ResourceTransferOutcome.Rejected,
                    changed ? "account.version.changed" : "transfer.precondition.rejected",
                    sourceAfterFailure, targetAfterFailure);
            }

            _receipt = committedReceipt
                ?? throw new InvalidOperationException("Order transfer completed without a validated receipt.");
            return _receipt;
        }
        catch
        {
            // OrderLiquidTransfer compensates both account snapshots on mutation failure.
            // A failure after physical commit (for example, ledger publication) is ambiguous:
            // preserve the fault and require authoritative reconciliation, never auto-retry.
            _faulted = true;
            throw;
        }
    }

    /// <summary>Cancel is safe only before Commit; no account mutation occurs during Prepare.</summary>
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

    private ResourceTransferReceipt CreateNoMutationReceipt(
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

/// <summary>
/// Factory for the first concrete cross-area participant: one order's liquid staging
/// account to the same order's liquid staging account in another Workshop.
/// </summary>
public static class OrderLiquidPortTransfer
{
    public static PreparedOrderLiquidPortTransfer Prepare(
        ResourceTransferRequest request,
        int orderId,
        CircuitConnectionDefinition connection,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        WorkshopOrderLiquidPortAdapter sourceAdapter,
        WorkshopOrderLiquidPortAdapter targetAdapter,
        WorkshopLiquidStaging sourceStaging,
        WorkshopLiquidStaging targetStaging,
        ResourceLedger? ledger = null,
        Func<int>? ledgerTickProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sourcePort);
        ArgumentNullException.ThrowIfNull(targetPort);
        ArgumentNullException.ThrowIfNull(sourceAdapter);
        ArgumentNullException.ThrowIfNull(targetAdapter);
        ArgumentNullException.ThrowIfNull(sourceStaging);
        ArgumentNullException.ThrowIfNull(targetStaging);
        request.Validate();
        ResourcePortTransferGuards.ValidateLedgerPair(ledger, ledgerTickProvider);

        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));
        if (ReferenceEquals(sourceStaging, targetStaging))
            throw new ArgumentException("Source and target must be distinct Workshop staging instances.");
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
            throw new ArgumentException("Order-liquid transfer requires the Connection's Liquid Output -> Input ports.");
        if (request.ResourceKind != RuleResourceKind.Liquid ||
            request.SourcePortId == request.TargetPortId ||
            request.SourceAccount != sourceAdapter.Account ||
            request.TargetAccount != targetAdapter.Account)
            throw new ArgumentException("Transfer request resource kind or authoritative account identity does not match its adapters.");

        if (sourceAdapter.OrderId != orderId || targetAdapter.OrderId != orderId ||
            !sourceAdapter.IsBoundTo(sourceStaging) || !targetAdapter.IsBoundTo(targetStaging))
            throw new ArgumentException(
                "Each order-liquid adapter must be bound to the exact supplied staging instance and order ID.");

        var source = sourceAdapter.Preflight(sourcePort, request.ResourceIdentity, request.Amount);
        var target = targetAdapter.Preflight(targetPort, request.ResourceIdentity, request.Amount);
        source.Validate();
        target.Validate();
        if (source.Account != request.SourceAccount || target.Account != request.TargetAccount)
            throw new ArgumentException("Port preflight returned an unexpected authoritative account.");

        return new PreparedOrderLiquidPortTransfer(
            request, orderId, sourceStaging, targetStaging,
            sourceAdapter, targetAdapter, sourcePort, targetPort,
            ledger, ledgerTickProvider);
    }
}

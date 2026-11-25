using System;
using System.Linq;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// Exact ownership transfer between two Workshop Jar accounts. Rollback restores the original
/// Workshop snapshots, including the same Jar entity references; no Jar is cloned or recycled.
/// </summary>
internal static class WorkshopWorkshopJarTransfer
{
    public static bool TryExecute(
        Workshop source,
        Workshop target,
        int jarId,
        out string? rejectionReason,
        Action? beforeFinalize = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(source, target))
            throw new DomainValidationException("Source and target Workshop must be different instances.");
        if (jarId <= 0)
            throw new DomainValidationException("Jar identity must be a positive integer.");

        var matches = source.Jars.Where(jar => jar.Id == jarId).ToArray();
        if (matches.Length != 1 || target.Jars.Any(jar => jar.Id == jarId) || !target.HasJarCapacity())
        {
            rejectionReason = "jar.transfer.precondition.rejected";
            return false;
        }

        var jar = matches[0];
        var sourceBefore = source.CreateSnapshot();
        var targetBefore = target.CreateSnapshot();
        try
        {
            if (!ReferenceEquals(source.TransferOut(jar), jar))
                throw new InvalidOperationException("Source Workshop no longer owns the requested Jar.");
            if (!target.TransferIn(jar))
                throw new InvalidOperationException("Target Workshop Jar storage changed after port preflight.");
            beforeFinalize?.Invoke();
            rejectionReason = null;
            return true;
        }
        catch (Exception transferError)
        {
            try
            {
                target.RestoreFromSnapshot(targetBefore);
                source.RestoreFromSnapshot(sourceBefore);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Workshop-to-Workshop Jar transfer failed and exact ownership compensation also failed.",
                    transferError, rollbackError);
            }
            throw;
        }
    }
}

/// <summary>One-shot transaction participant for one discrete Jar between two Workshop Jar accounts.</summary>
public sealed class PreparedWorkshopJarPortTransfer
{
    private readonly ResourceTransferRequest _request;
    private readonly Workshop _sourceWorkshop;
    private readonly Workshop _targetWorkshop;
    private readonly WorkshopJarStoragePortAdapter _sourceAdapter;
    private readonly WorkshopJarStoragePortAdapter _targetAdapter;
    private readonly CircuitPortDefinition _sourcePort;
    private readonly CircuitPortDefinition _targetPort;
    private readonly ResourceLedger? _ledger;
    private readonly Func<int>? _ledgerTickProvider;
    private ResourceTransferReceipt? _receipt;
    private bool _cancelled;
    private bool _faulted;
    private bool _duplicateOperation;

    internal PreparedWorkshopJarPortTransfer(
        ResourceTransferRequest request,
        Workshop sourceWorkshop,
        Workshop targetWorkshop,
        WorkshopJarStoragePortAdapter sourceAdapter,
        WorkshopJarStoragePortAdapter targetAdapter,
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
            var jarId = int.Parse(_request.ResourceIdentity, System.Globalization.CultureInfo.InvariantCulture);
            ResourceTransferReceipt? committedReceipt = null;
            if (!WorkshopWorkshopJarTransfer.TryExecute(
                _sourceWorkshop, _targetWorkshop, jarId, out _,
                () =>
                {
                    var sourceAfter = _sourceAdapter.Preflight(
                        _sourcePort, _request.ResourceIdentity, _request.Amount);
                    var targetAfter = _targetAdapter.Preflight(
                        _targetPort, _request.ResourceIdentity, _request.Amount);
                    committedReceipt = ResourcePortTransferGuards.CreateCommittedReceipt(
                        _request, source, target, sourceAfter, targetAfter);
                    if (_ledger is not null)
                    {
                        var jar = _targetWorkshop.Jars.Single(item => item.Id == jarId);
                        if (!_ledger.RecordBatchOnce(_request.OperationId, ledgerTick, new[]
                        {
                            new ResourceLedgerMutation(
                                ResourceCauses.Transfer, ResourceKinds.Jar, jar.Shape, 1,
                                From: _request.SourceAccount.OwnerId, To: _request.TargetAccount.OwnerId)
                        }))
                            throw new InvalidOperationException(
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

/// <summary>Prepare an exact single-Jar Workshop Output -> Workshop Input transfer.</summary>
public static class WorkshopJarPortTransfer
{
    public static PreparedWorkshopJarPortTransfer Prepare(
        ResourceTransferRequest request,
        CircuitConnectionDefinition connection,
        CircuitPortDefinition sourcePort,
        CircuitPortDefinition targetPort,
        WorkshopJarStoragePortAdapter sourceAdapter,
        WorkshopJarStoragePortAdapter targetAdapter,
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
            sourcePort.ResourceKind != RuleResourceKind.Jar ||
            targetPort.ResourceKind != RuleResourceKind.Jar)
            throw new ArgumentException("Workshop Jar transfer requires Jar Output -> Input ports.");
        if (request.ResourceKind != RuleResourceKind.Jar ||
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

        return new PreparedWorkshopJarPortTransfer(
            request, sourceWorkshop, targetWorkshop, sourceAdapter, targetAdapter,
            sourcePort, targetPort, ledger, ledgerTickProvider);
    }
}

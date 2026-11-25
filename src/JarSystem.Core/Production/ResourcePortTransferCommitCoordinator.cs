using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// Shared finalization protocol for resource-port transactions. The caller invokes this
/// only from inside a physical transaction's compensation boundary. It validates the
/// final receipt first and publishes the OperationId + ledger batch last.
/// </summary>
internal static class ResourcePortTransferCommitCoordinator
{
    public static ResourceTransferReceipt Finalize(
        ResourceTransferRequest request,
        ResourcePortPreflight sourceBefore,
        ResourcePortPreflight targetBefore,
        Func<ResourcePortPreflight> readSourceAfter,
        Func<ResourcePortPreflight> readTargetAfter,
        ResourceLedger? ledger,
        int ledgerTick,
        IReadOnlyList<ResourceLedgerMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceBefore);
        ArgumentNullException.ThrowIfNull(targetBefore);
        ArgumentNullException.ThrowIfNull(readSourceAfter);
        ArgumentNullException.ThrowIfNull(readTargetAfter);
        ArgumentNullException.ThrowIfNull(mutations);

        if (ledger is not null && ledgerTick < 0)
            throw new ArgumentOutOfRangeException(nameof(ledgerTick));

        var sourceAfter = readSourceAfter();
        var targetAfter = readTargetAfter();
        ValidateFinalEndpoint(sourceAfter, request.SourcePortId, request.SourceAccount,
            request.ResourceKind, request.ResourceIdentity, request.Amount);
        ValidateFinalEndpoint(targetAfter, request.TargetPortId, request.TargetAccount,
            request.ResourceKind, request.ResourceIdentity, request.Amount);
        var receipt = ResourcePortTransferGuards.CreateCommittedReceipt(
            request, sourceBefore, targetBefore, sourceAfter, targetAfter);

        // This is the final fallible publication. Every caller must keep this invocation
        // inside a transaction that restores its physical account snapshots if it throws.
        if (ledger is not null &&
            !ledger.RecordTransferOnce(request.OperationId, ledgerTick, mutations, receipt))
            throw new InvalidOperationException(ResourcePortTransferGuards.DuplicateOperationMessage);

        return receipt;
    }

    private static void ValidateFinalEndpoint(
        ResourcePortPreflight preflight,
        string portId,
        RuleResourceAccount account,
        RuleResourceKind resourceKind,
        string resourceIdentity,
        int amount)
    {
        preflight.Validate();
        if (preflight.PortId != portId || preflight.Account != account ||
            preflight.ResourceKind != resourceKind ||
            preflight.ResourceIdentity != resourceIdentity ||
            preflight.Amount != amount)
            throw new DomainValidationException(
                "Final resource-port preflight no longer identifies the prepared endpoint and resource.");
    }
}

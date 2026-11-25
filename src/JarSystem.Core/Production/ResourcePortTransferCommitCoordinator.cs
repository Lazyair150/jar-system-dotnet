using System;
using System.Collections.Generic;

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
        var receipt = ResourcePortTransferGuards.CreateCommittedReceipt(
            request, sourceBefore, targetBefore, sourceAfter, targetAfter);

        // This is the final fallible publication. Every caller must keep this invocation
        // inside a transaction that restores its physical account snapshots if it throws.
        if (ledger is not null &&
            !ledger.RecordTransferOnce(request.OperationId, ledgerTick, mutations, receipt))
            throw new InvalidOperationException(ResourcePortTransferGuards.DuplicateOperationMessage);

        return receipt;
    }
}

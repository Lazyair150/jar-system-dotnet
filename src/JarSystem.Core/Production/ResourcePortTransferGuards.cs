using System;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// Shared fail-closed guard for resource-port transaction participants. It centralizes
/// account-version revalidation, durable OperationId deduplication, and policy outcomes
/// so individual participants cannot drift into subtly different commit semantics.
/// </summary>
internal static class ResourcePortTransferGuards
{
    public const string DuplicateOperationMessage =
        "This OperationId was already committed; reconcile from the authoritative ledger instead of replaying.";

    public static void ValidateLedgerPair(ResourceLedger? ledger, Func<int>? ledgerTickProvider)
    {
        if ((ledger is null) != (ledgerTickProvider is null))
            throw new ArgumentException("ResourceLedger and its logical tick provider must be supplied together.");
    }

    /// <summary>
    /// Returns a no-mutation receipt when commit must stop, or null when both endpoints
    /// remain allowed at the exact versions captured by Prepare. Duplicate IDs are terminal.
    /// </summary>
    public static ResourceTransferReceipt? EvaluateCommitPreflight(
        ResourceTransferRequest request,
        ResourcePortPreflight source,
        ResourcePortPreflight target,
        ResourceLedger? ledger,
        Action markDuplicate,
        Func<ResourceTransferOutcome, string, ResourceTransferReceipt> noMutation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(markDuplicate);
        ArgumentNullException.ThrowIfNull(noMutation);

        source.Validate();
        target.Validate();

        if (ledger?.HasRecordedOperation(request.OperationId) == true)
        {
            markDuplicate();
            throw new InvalidOperationException(DuplicateOperationMessage);
        }

        if (source.ObservedVersionToken != request.ExpectedSourceVersionToken ||
            target.ObservedVersionToken != request.ExpectedTargetVersionToken)
            return noMutation(ResourceTransferOutcome.Undetermined, "account.version.changed");

        if (source.Decision == RuleDecisionKind.Allowed &&
            target.Decision == RuleDecisionKind.Allowed)
            return null;

        var outcome = source.Decision == RuleDecisionKind.Undetermined ||
            target.Decision == RuleDecisionKind.Undetermined
            ? ResourceTransferOutcome.Undetermined
            : ResourceTransferOutcome.Rejected;
        var reason = source.Decision != RuleDecisionKind.Allowed
            ? source.ReasonCode
            : target.ReasonCode;
        return noMutation(outcome, reason);
    }
}

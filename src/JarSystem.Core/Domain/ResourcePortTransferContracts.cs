using System;

namespace JarSystem.Core.Domain;

/// <summary>Outcome of a pure Input/Output port preflight.</summary>
public sealed record ResourcePortPreflight(
    RuleDecisionKind Decision,
    string PortId,
    RuleResourceAccount Account,
    RuleResourceKind ResourceKind,
    string ResourceIdentity,
    int Amount,
    string ObservedVersionToken,
    string ReasonCode,
    string? Diagnostic = null)
{
    public void Validate()
    {
        CircuitAreaDefinition.RequireIdentity(PortId, nameof(PortId));
        Account.Validate();
        CircuitAreaDefinition.RequireIdentity(ResourceIdentity, nameof(ResourceIdentity));
        if (!Enum.IsDefined(Decision) || !Enum.IsDefined(ResourceKind) ||
            Amount <= 0 || string.IsNullOrWhiteSpace(ObservedVersionToken) ||
            string.IsNullOrWhiteSpace(ReasonCode))
            throw new ArgumentException("Port preflight contains an invalid decision or resource/version contract.");

        if (Decision == RuleDecisionKind.Allowed && ReasonCode != "allowed")
            throw new ArgumentException("Allowed port preflight must use the stable 'allowed' reason code.");
        if (Decision != RuleDecisionKind.Allowed && ReasonCode == "allowed")
            throw new ArgumentException("Rejected/undetermined port preflight cannot use the 'allowed' reason code.");
    }
}

/// <summary>
/// Immutable request to move one resource identity across one explicit Connection.
/// It records account ownership and optimistic versions; it does not perform the transfer.
/// </summary>
public sealed record ResourceTransferRequest(
    string OperationId,
    string ConnectionId,
    string SourcePortId,
    string TargetPortId,
    RuleResourceKind ResourceKind,
    string ResourceIdentity,
    int Amount,
    RuleResourceAccount SourceAccount,
    RuleResourceAccount TargetAccount,
    string ExpectedSourceVersionToken,
    string ExpectedTargetVersionToken)
{
    public void Validate()
    {
        CircuitAreaDefinition.RequireIdentity(OperationId, nameof(OperationId));
        CircuitAreaDefinition.RequireIdentity(ConnectionId, nameof(ConnectionId));
        CircuitAreaDefinition.RequireIdentity(SourcePortId, nameof(SourcePortId));
        CircuitAreaDefinition.RequireIdentity(TargetPortId, nameof(TargetPortId));
        CircuitAreaDefinition.RequireIdentity(ResourceIdentity, nameof(ResourceIdentity));
        SourceAccount.Validate();
        TargetAccount.Validate();
        if (!Enum.IsDefined(ResourceKind) || Amount <= 0 ||
            string.IsNullOrWhiteSpace(ExpectedSourceVersionToken) || string.IsNullOrWhiteSpace(ExpectedTargetVersionToken))
            throw new ArgumentException("Resource transfer request contains an invalid kind, amount or expected version.");
        if (SourcePortId == TargetPortId || SourceAccount == TargetAccount)
            throw new ArgumentException("A transfer must cross distinct ports and authoritative accounts.");
    }
}

public enum ResourceTransferOutcome
{
    Transferred,
    PartiallyTransferred,
    Rejected,
    Undetermined
}

/// <summary>
/// Immutable audit receipt for a completed or rejected transfer attempt.
/// Requested = transferred + retained at source + explicitly discarded.
/// </summary>
public sealed record ResourceTransferReceipt(
    string OperationId,
    string ConnectionId,
    string SourcePortId,
    string TargetPortId,
    RuleResourceKind ResourceKind,
    string ResourceIdentity,
    RuleResourceAccount SourceAccount,
    RuleResourceAccount TargetAccount,
    int RequestedAmount,
    int TransferredAmount,
    int RetainedAtSourceAmount,
    int DiscardedAmount,
    ResourceTransferOutcome Outcome,
    string SourceVersionBefore,
    string SourceVersionAfter,
    string TargetVersionBefore,
    string TargetVersionAfter,
    string ReasonCode)
{
    public void Validate()
    {
        CircuitAreaDefinition.RequireIdentity(OperationId, nameof(OperationId));
        CircuitAreaDefinition.RequireIdentity(ConnectionId, nameof(ConnectionId));
        CircuitAreaDefinition.RequireIdentity(SourcePortId, nameof(SourcePortId));
        CircuitAreaDefinition.RequireIdentity(TargetPortId, nameof(TargetPortId));
        CircuitAreaDefinition.RequireIdentity(ResourceIdentity, nameof(ResourceIdentity));
        SourceAccount.Validate();
        TargetAccount.Validate();

        if (!Enum.IsDefined(ResourceKind) || !Enum.IsDefined(Outcome) ||
            RequestedAmount <= 0 || TransferredAmount < 0 ||
            RetainedAtSourceAmount < 0 || DiscardedAmount < 0 ||
            (long)TransferredAmount + RetainedAtSourceAmount + DiscardedAmount != RequestedAmount ||
            string.IsNullOrWhiteSpace(SourceVersionBefore) || string.IsNullOrWhiteSpace(SourceVersionAfter) ||
            string.IsNullOrWhiteSpace(TargetVersionBefore) || string.IsNullOrWhiteSpace(TargetVersionAfter) ||
            string.IsNullOrWhiteSpace(ReasonCode) ||
            SourcePortId == TargetPortId || SourceAccount == TargetAccount)
            throw new ArgumentException("Resource transfer receipt violates its identity, quantity or version contract.");

        switch (Outcome)
        {
            case ResourceTransferOutcome.Transferred when
                TransferredAmount != RequestedAmount || RetainedAtSourceAmount != 0 || DiscardedAmount != 0:
                throw new ArgumentException("Transferred outcome must account for the complete requested quantity.");
            case ResourceTransferOutcome.PartiallyTransferred when
                TransferredAmount <= 0 || TransferredAmount >= RequestedAmount:
                throw new ArgumentException("Partially transferred outcome requires a non-zero strict subset.");
            case ResourceTransferOutcome.Rejected:
            case ResourceTransferOutcome.Undetermined:
                if (TransferredAmount != 0 || RetainedAtSourceAmount != RequestedAmount || DiscardedAmount != 0)
                    throw new ArgumentException("Rejected/undetermined attempts must leave the complete quantity at source.");
                if (SourceVersionAfter != SourceVersionBefore || TargetVersionAfter != TargetVersionBefore)
                    throw new ArgumentException("Rejected/undetermined attempts cannot mutate account versions.");
                break;
        }
    }
}

using System;
using System.Text.Json.Serialization;

namespace JarSystem.Core.Domain;

/// <summary>
/// The provenance endpoint of a resource fact. Null is never used to mean several
/// different things: external movement and historical unknown provenance are tagged.
/// </summary>
public enum ResourceEndpointKind
{
    LegacyUnknown = 0,
    Account = 1,
    ExternalSource = 2,
    ExternalSink = 3
}

/// <summary>
/// Immutable tagged endpoint for resource-ledger facts. Account endpoints carry a
/// versioned AccountId; external endpoints carry a stable machine identity.
/// LegacyUnknown exists only to preserve historical events during staged migration.
/// </summary>
public readonly record struct ResourceEndpoint
{
    public ResourceEndpointKind Kind { get; }
    public string? AccountId { get; }
    public string? ExternalIdentity { get; }

    [JsonConstructor]
    public ResourceEndpoint(ResourceEndpointKind kind, string? accountId, string? externalIdentity)
    {
        Kind = kind;
        AccountId = accountId;
        ExternalIdentity = externalIdentity;
        Validate();
    }

    public static ResourceEndpoint LegacyUnknown => new(ResourceEndpointKind.LegacyUnknown, null, null);

    public static ResourceEndpoint ForAccount(string accountId)
    {
        OwnerIdentity.ValidateIdentityPart(accountId, nameof(accountId));
        if (!accountId.StartsWith("account:v1:", StringComparison.Ordinal) &&
            !accountId.StartsWith("account:v2:", StringComparison.Ordinal))
            throw new ArgumentException("Account endpoint must contain a recognized versioned AccountId.", nameof(accountId));
        if (accountId.StartsWith("account:v2:", StringComparison.Ordinal))
            _ = AccountIdentity.ParseV2(accountId);
        return new ResourceEndpoint(ResourceEndpointKind.Account, accountId, null);
    }

    public static ResourceEndpoint ExternalSource(string externalIdentity) =>
        External(ResourceEndpointKind.ExternalSource, externalIdentity);

    public static ResourceEndpoint ExternalSink(string externalIdentity) =>
        External(ResourceEndpointKind.ExternalSink, externalIdentity);

    private static ResourceEndpoint External(ResourceEndpointKind kind, string identity)
    {
        OwnerIdentity.ValidateIdentityPart(identity, nameof(identity));
        return new ResourceEndpoint(kind, null, identity);
    }

    public bool IsValid => Kind switch
    {
        ResourceEndpointKind.LegacyUnknown => AccountId == null && ExternalIdentity == null,
        ResourceEndpointKind.Account => IsValidAccountId(AccountId, ExternalIdentity),
        ResourceEndpointKind.ExternalSource or ResourceEndpointKind.ExternalSink =>
            AccountId == null && !string.IsNullOrWhiteSpace(ExternalIdentity) &&
            string.Equals(ExternalIdentity, ExternalIdentity.Trim(), StringComparison.Ordinal),
        _ => false
    };

    private static bool IsValidAccountId(string? accountId, string? externalIdentity)
    {
        if (accountId == null || externalIdentity != null) return false;
        if (!accountId.StartsWith("account:v1:", StringComparison.Ordinal) &&
            !accountId.StartsWith("account:v2:", StringComparison.Ordinal))
            return false;
        try
        {
            OwnerIdentity.ValidateIdentityPart(accountId, nameof(accountId));
            if (accountId.StartsWith("account:v2:", StringComparison.Ordinal))
                _ = AccountIdentity.ParseV2(accountId);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
    }

    public void Validate()
    {
        if (!IsValid)
            throw new ArgumentException("Resource endpoint tag and payload do not form a valid endpoint.");
        if (Kind is ResourceEndpointKind.ExternalSource or ResourceEndpointKind.ExternalSink)
            OwnerIdentity.ValidateIdentityPart(ExternalIdentity!, nameof(ExternalIdentity));
    }
}

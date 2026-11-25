using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JarSystem.Core.Domain;

/// <summary>
/// Stable machine identity for one resource-owning individual inside one World.
/// Display names and movable roles are deliberately excluded.
/// </summary>
public readonly record struct OwnerIdentity
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public WorldInstanceId WorldInstanceId { get; }
    public ResourceOwnerKind OwnerKind { get; }
    public string LocalOwnerId { get; }

    public OwnerIdentity(
        WorldInstanceId worldInstanceId,
        ResourceOwnerKind ownerKind,
        string localOwnerId)
    {
        if (!worldInstanceId.IsValid)
            throw new ArgumentException("Owner identity requires a valid WorldInstanceId.", nameof(worldInstanceId));
        if (!Enum.IsDefined(ownerKind))
            throw new ArgumentOutOfRangeException(nameof(ownerKind));
        ValidateIdentityPart(localOwnerId, nameof(localOwnerId));
        _ = StrictUtf8.GetByteCount(localOwnerId);

        WorldInstanceId = worldInstanceId;
        OwnerKind = ownerKind;
        LocalOwnerId = localOwnerId;
    }

    public bool IsValid =>
        WorldInstanceId.IsValid &&
        Enum.IsDefined(OwnerKind) &&
        !string.IsNullOrWhiteSpace(LocalOwnerId) &&
        string.Equals(LocalOwnerId, LocalOwnerId.Trim(), StringComparison.Ordinal);

    internal static string EncodeUtf8Field(string value)
    {
        ValidateIdentityPart(value, nameof(value));
        var byteLength = StrictUtf8.GetByteCount(value);
        return byteLength.ToString(CultureInfo.InvariantCulture) + ":" + value;
    }

    internal static void ValidateIdentityPart(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
            throw new ArgumentException("Identity fields must be non-empty, trimmed and contain no control characters.", parameterName);
    }

    internal static string OwnerKindToken(ResourceOwnerKind ownerKind) => ownerKind switch
    {
        ResourceOwnerKind.Ship => "ship",
        ResourceOwnerKind.CircuitBoard => "circuit-board",
        ResourceOwnerKind.ProductionArea => "production-area",
        ResourceOwnerKind.Warehouse => "warehouse",
        ResourceOwnerKind.Producer => "producer",
        ResourceOwnerKind.Workshop => "workshop",
        _ => throw new ArgumentOutOfRangeException(nameof(ownerKind))
    };
}

/// <summary>Stable categories of resource-owning runtime individuals.</summary>
public enum ResourceOwnerKind
{
    Ship = 1,
    CircuitBoard = 2,
    ProductionArea = 3,
    Warehouse = 4,
    Producer = 5,
    Workshop = 6
}

/// <summary>
/// Versioned identity for one exact resource account. The v2 wire form is UTF-8
/// byte-length-prefixed and therefore unambiguous even when fields contain separators.
/// This value type does not reinterpret legacy account:v1 strings.
/// </summary>
public readonly record struct AccountIdentity
{
    public OwnerIdentity Owner { get; }
    public string AccountKind { get; }

    public AccountIdentity(OwnerIdentity owner, string accountKind)
    {
        if (!owner.IsValid)
            throw new ArgumentException("Account identity requires a valid OwnerIdentity.", nameof(owner));
        OwnerIdentity.ValidateIdentityPart(accountKind, nameof(accountKind));
        _ = new UTF8Encoding(false, true).GetByteCount(accountKind);

        Owner = owner;
        AccountKind = accountKind;
    }

    public bool IsValid => Owner.IsValid &&
        !string.IsNullOrWhiteSpace(AccountKind) &&
        string.Equals(AccountKind, AccountKind.Trim(), StringComparison.Ordinal);

    public string ToCanonicalString()
    {
        if (!IsValid)
            throw new InvalidOperationException("Cannot serialize an invalid AccountIdentity.");

        return "account:v2:" +
            OwnerIdentity.EncodeUtf8Field(Owner.WorldInstanceId.Value) +
            OwnerIdentity.EncodeUtf8Field(OwnerIdentity.OwnerKindToken(Owner.OwnerKind)) +
            OwnerIdentity.EncodeUtf8Field(Owner.LocalOwnerId) +
            OwnerIdentity.EncodeUtf8Field(AccountKind);
    }

    public override string ToString() => ToCanonicalString();
}

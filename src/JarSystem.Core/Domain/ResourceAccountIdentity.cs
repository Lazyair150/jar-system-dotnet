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

    public static AccountIdentity ParseV2(string value)
    {
        const string prefix = "account:v2:";
        if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new FormatException("Account identity must use the account:v2 prefix.");

        byte[] bytes;
        try
        {
            bytes = new UTF8Encoding(false, true).GetBytes(value[prefix.Length..]);
        }
        catch (EncoderFallbackException exception)
        {
            throw new FormatException("Account identity contains invalid UTF-8 text.", exception);
        }

        var offset = 0;
        var worldId = ReadUtf8Field(bytes, ref offset);
        var ownerKindToken = ReadUtf8Field(bytes, ref offset);
        var localOwnerId = ReadUtf8Field(bytes, ref offset);
        var accountKind = ReadUtf8Field(bytes, ref offset);
        if (offset != bytes.Length)
            throw new FormatException("Account identity contains trailing bytes.");

        var ownerKind = ownerKindToken switch
        {
            "ship" => ResourceOwnerKind.Ship,
            "circuit-board" => ResourceOwnerKind.CircuitBoard,
            "production-area" => ResourceOwnerKind.ProductionArea,
            "warehouse" => ResourceOwnerKind.Warehouse,
            "producer" => ResourceOwnerKind.Producer,
            "workshop" => ResourceOwnerKind.Workshop,
            _ => throw new FormatException("Account identity contains an unknown OwnerKind.")
        };

        try
        {
            var parsed = new AccountIdentity(
                new OwnerIdentity(new WorldInstanceId(worldId), ownerKind, localOwnerId),
                accountKind);
            if (!string.Equals(parsed.ToCanonicalString(), value, StringComparison.Ordinal))
                throw new FormatException("Account identity is not in canonical form.");
            return parsed;
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Account identity contains an invalid field.", exception);
        }
    }

    private static string ReadUtf8Field(byte[] bytes, ref int offset)
    {
        var lengthStart = offset;
        while (offset < bytes.Length && bytes[offset] != (byte)':')
        {
            if (bytes[offset] < (byte)'0' || bytes[offset] > (byte)'9')
                throw new FormatException("Account identity field length is malformed.");
            offset++;
        }
        if (offset == lengthStart || offset >= bytes.Length)
            throw new FormatException("Account identity field length is missing.");
        var lengthText = Encoding.ASCII.GetString(bytes, lengthStart, offset - lengthStart);
        if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var byteLength) ||
            byteLength <= 0 ||
            (lengthText.Length > 1 && lengthText[0] == '0'))
            throw new FormatException("Account identity field length is not canonical.");

        offset++;
        if (byteLength > bytes.Length - offset)
            throw new FormatException("Account identity field is truncated.");
        try
        {
            var field = new UTF8Encoding(false, true).GetString(bytes, offset, byteLength);
            offset += byteLength;
            return field;
        }
        catch (DecoderFallbackException exception)
        {
            throw new FormatException("Account identity field contains invalid UTF-8.", exception);
        }
    }

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

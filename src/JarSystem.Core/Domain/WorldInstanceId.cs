using System.Text;

namespace JarSystem.Core.Domain;

/// <summary>
/// Host-assigned identity for one live World instance.
/// It is deliberately independent from configuration fingerprints and definition IDs.
/// </summary>
public readonly record struct WorldInstanceId
{
    public string Value { get; }
    public bool IsValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Value) ||
                !string.Equals(Value, Value.Trim(), StringComparison.Ordinal) ||
                ContainsControlCharacter(Value))
                return false;
            try
            {
                _ = new UTF8Encoding(false, true).GetByteCount(Value);
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }
    }

    public WorldInstanceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("WorldInstanceId cannot be null, empty, or whitespace.", nameof(value));
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("WorldInstanceId cannot contain leading or trailing whitespace.", nameof(value));
        if (ContainsControlCharacter(value))
            throw new ArgumentException("WorldInstanceId cannot contain control characters.", nameof(value));
        _ = new UTF8Encoding(false, true).GetByteCount(value);
        Value = value;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var character in value)
            if (char.IsControl(character)) return true;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;
}

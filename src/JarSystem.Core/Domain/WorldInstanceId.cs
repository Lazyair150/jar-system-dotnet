namespace JarSystem.Core.Domain;

/// <summary>
/// Host-assigned identity for one live World instance.
/// It is deliberately independent from configuration fingerprints and definition IDs.
/// </summary>
public readonly record struct WorldInstanceId
{
    public string Value { get; }

    public WorldInstanceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("WorldInstanceId cannot be null, empty, or whitespace.", nameof(value));
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("WorldInstanceId cannot contain leading or trailing whitespace.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value ?? string.Empty;
}

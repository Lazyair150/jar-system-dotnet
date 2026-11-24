using System;

namespace JarSystem.Core.Domain;

/// <summary>
/// Derived three-state classification of a Jar's internal liquid.
/// This is a domain abstraction, not an independently mutable Jar field.
/// </summary>
public enum JarPolarity
{
    Neutral,
    Cathode,
    Anode
}

/// <summary>Operating mode derived from a Workshop's banned liquid categories.</summary>
public enum WorkshopPolarityMode
{
    Unrestricted,
    Cathode,
    Anode,
    NeutralOnly
}

/// <summary>
/// Maps the current two-liquid vocabulary to the three Jar polarity states.
/// Unknown non-empty liquid identities fail closed instead of being assigned
/// an arbitrary polarity. Empty Jar is always Neutral.
/// </summary>
public static class JarPolarityResolver
{
    public static bool TryFromLiquid(string? liquid, out JarPolarity polarity)
    {
        if (liquid is null)
        {
            polarity = JarPolarity.Neutral;
            return true;
        }

        if (string.Equals(liquid, "酒", StringComparison.Ordinal))
        {
            polarity = JarPolarity.Cathode;
            return true;
        }

        if (string.Equals(liquid, "蜜", StringComparison.Ordinal))
        {
            polarity = JarPolarity.Anode;
            return true;
        }

        polarity = default;
        return false;
    }

    public static JarPolarity Resolve(string? liquid) =>
        TryFromLiquid(liquid, out var polarity)
            ? polarity
            : throw new ArgumentException("Liquid has no registered Jar polarity: " + liquid, nameof(liquid));
}

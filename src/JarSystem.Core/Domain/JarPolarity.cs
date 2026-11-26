using System;
using JarSystem.Core.Config;

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

/// <summary>Legacy transition-contract projection. Ban no longer derives Jar-operation polarity; this value remains Unrestricted for compatibility.</summary>
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
    public static bool TryFromLiquid(
        string? liquid,
        Vocabulary vocabulary,
        out JarPolarity polarity)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (liquid is null)
        {
            polarity = JarPolarity.Neutral;
            return true;
        }

        if (vocabulary.TryGetLiquidPolarity(liquid, out var configured))
        {
            polarity = configured == LiquidPolarity.Cathode
                ? JarPolarity.Cathode
                : JarPolarity.Anode;
            return true;
        }

        polarity = default;
        return false;
    }

    public static JarPolarity Resolve(string? liquid, Vocabulary vocabulary) =>
        TryFromLiquid(liquid, vocabulary, out var polarity)
            ? polarity
            : throw new ArgumentException("Liquid identity has no polarity in this World: " + liquid, nameof(liquid));
}

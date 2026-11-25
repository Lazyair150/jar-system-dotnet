using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace JarSystem.Core.Config;

/// <summary>
/// World vocabulary: the available shape and liquid identities, plus the explicit
/// binary polarity assignment for the World's two mutually exclusive liquids.
/// Identity strings are data; they never imply polarity by their spelling or order.
/// </summary>
public sealed class Vocabulary
{
    private readonly List<string> _shapes;
    private readonly List<string> _liquids;
    private readonly IReadOnlyList<string> _shapesView;
    private readonly IReadOnlyList<string> _liquidsView;
    private readonly ReadOnlyDictionary<string, LiquidPolarity> _liquidPolarities;

    public Vocabulary(
        IEnumerable<string> shapes,
        IEnumerable<string> liquids,
        IReadOnlyDictionary<string, LiquidPolarity>? liquidPolarities = null)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(liquids);

        _shapes = new List<string>(shapes);
        _liquids = new List<string>(liquids);
        _shapesView = _shapes.AsReadOnly();
        _liquidsView = _liquids.AsReadOnly();

        var mapping = liquidPolarities == null
            ? CreateLegacyDefaultMappingIfApplicable(_liquids)
            : new Dictionary<string, LiquidPolarity>(liquidPolarities, StringComparer.Ordinal);

        ValidateLiquidPolarities(_liquids, mapping);
        _liquidPolarities = new ReadOnlyDictionary<string, LiquidPolarity>(mapping);
    }

    /// <summary>Shape identity directory. Order remains part of the stable world contract.</summary>
    public IReadOnlyList<string> Shapes => _shapesView;

    /// <summary>Exactly two liquid identities. Their order is not their polarity.</summary>
    public IReadOnlyList<string> Liquids => _liquidsView;

    /// <summary>Immutable identity-to-polarity assignment owned by this World.</summary>
    public IReadOnlyDictionary<string, LiquidPolarity> LiquidPolarities => _liquidPolarities;

    public bool IsValidShape(string shape) => _shapes.Contains(shape);

    public bool IsValidLiquid(string liquid) => _liquidPolarities.ContainsKey(liquid);

    public bool TryGetLiquidPolarity(string? liquid, out LiquidPolarity polarity)
    {
        if (liquid != null && _liquidPolarities.TryGetValue(liquid, out polarity))
            return true;

        polarity = default;
        return false;
    }

    public LiquidPolarity ResolveLiquidPolarity(string liquid) =>
        TryGetLiquidPolarity(liquid, out var polarity)
            ? polarity
            : throw new ArgumentException("Liquid identity has no polarity in this World: " + liquid, nameof(liquid));

    private static Dictionary<string, LiquidPolarity> CreateLegacyDefaultMappingIfApplicable(
        IReadOnlyList<string> liquids)
    {
        if (liquids.Count == 2 &&
            string.Equals(liquids[0], Constants.LIQUIDS[0], StringComparison.Ordinal) &&
            string.Equals(liquids[1], Constants.LIQUIDS[1], StringComparison.Ordinal))
        {
            return new Dictionary<string, LiquidPolarity>(StringComparer.Ordinal)
            {
                [liquids[0]] = LiquidPolarity.Cathode,
                [liquids[1]] = LiquidPolarity.Anode
            };
        }

        throw new ArgumentException(
            "A non-default World must explicitly assign one Cathode and one Anode liquid identity.",
            nameof(liquids));
    }

    private static void ValidateLiquidPolarities(
        IReadOnlyList<string> liquids,
        IReadOnlyDictionary<string, LiquidPolarity> mapping)
    {
        if (liquids.Count != 2)
            throw new ArgumentException("A World must define exactly two mutually exclusive liquid identities.", nameof(liquids));
        if (mapping.Count != 2)
            throw new ArgumentException("A World must assign polarity to exactly two liquid identities.", nameof(mapping));

        foreach (var liquid in liquids)
        {
            if (string.IsNullOrWhiteSpace(liquid) || !mapping.ContainsKey(liquid))
                throw new ArgumentException("Every liquid identity must have exactly one polarity assignment.", nameof(mapping));
        }

        foreach (var key in mapping.Keys)
        {
            if (!liquids.Contains(key))
                throw new ArgumentException("Polarity mapping contains an identity outside the World liquid vocabulary: " + key, nameof(mapping));
        }

        var cathodes = 0;
        var anodes = 0;
        foreach (var polarity in mapping.Values)
        {
            if (polarity == LiquidPolarity.Cathode) cathodes++;
            else if (polarity == LiquidPolarity.Anode) anodes++;
            else throw new ArgumentException("Unknown liquid polarity value.", nameof(mapping));
        }

        if (cathodes != 1 || anodes != 1)
            throw new ArgumentException("The two liquid identities must map to Cathode and Anode exactly once each.", nameof(mapping));
    }

    /// <summary>Legacy default vocabulary retained for old callers and the default world only.</summary>
    public static Vocabulary Default { get; } = new(Constants.SHAPES, Constants.LIQUIDS);
}

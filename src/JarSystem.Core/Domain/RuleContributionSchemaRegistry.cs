using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JarSystem.Core.Domain;

/// <summary>Version-1 parameter contract for a serializable RuleContribution effect.</summary>
public sealed record RuleContributionEffectSchema(
    RuleContributionEffect Effect,
    IReadOnlyList<string> RequiredParameters,
    IReadOnlyList<string> OptionalParameters,
    bool SupportsStaticSolverProjection);

/// <summary>
/// Single source of truth for contribution effect schemas. Unknown effects and malformed
/// parameters fail during profile assembly. Known non-projectable effects remain explicit
/// and are reported as Undetermined by the solver.
/// </summary>
public static class RuleContributionSchemaRegistry
{
    /// <summary>Increment when effect parameter validation or projection semantics change.</summary>
    public const int CurrentVersion = 1;

    private static readonly IReadOnlyDictionary<RuleContributionEffect, RuleContributionEffectSchema> Schemas =
        new Dictionary<RuleContributionEffect, RuleContributionEffectSchema>
        {
            [RuleContributionEffect.GrantBaseMark] = Schema(RuleContributionEffect.GrantBaseMark, true),
            [RuleContributionEffect.GrantAdditionalMark] = Schema(RuleContributionEffect.GrantAdditionalMark, true),
            [RuleContributionEffect.BanLiquid] = Schema(RuleContributionEffect.BanLiquid, true),
            [RuleContributionEffect.SuppressOperation] = Schema(
                RuleContributionEffect.SuppressOperation, true,
                ["modifierName", "modifierSource", "operation", "shape", "liquid", "reason"],
                ["windowFrom", "windowTo"]),
            [RuleContributionEffect.RequireCondition] = Schema(
                RuleContributionEffect.RequireCondition, false,
                ["modifierName", "modifierSource", "predicate"],
                ["windowFrom", "windowTo"]),
            [RuleContributionEffect.ModifyCapacity] = Schema(RuleContributionEffect.ModifyCapacity, false),
            [RuleContributionEffect.RetimeProcess] = Schema(
                RuleContributionEffect.RetimeProcess, true,
                ["modifierName", "modifierSource", "ticksPerStep"],
                ["windowFrom", "windowTo"]),
            [RuleContributionEffect.ModifyIntensity] = Schema(
                RuleContributionEffect.ModifyIntensity, true,
                ["modifierName", "modifierSource", "factorPerMille"],
                ["windowFrom", "windowTo"])
        };

    public static IReadOnlyCollection<RuleContributionEffectSchema> All => Array.AsReadOnly(Schemas.Values.ToArray());

    public static RuleContributionEffectSchema Get(RuleContributionEffect effect) =>
        Schemas.TryGetValue(effect, out var schema)
            ? schema
            : throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unknown RuleContribution effect.");

    public static void Validate(RuleContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        var schema = Get(contribution.Effect);
        var required = new HashSet<string>(schema.RequiredParameters, StringComparer.Ordinal);
        var optional = new HashSet<string>(schema.OptionalParameters, StringComparer.Ordinal);

        if (contribution.Effect == RuleContributionEffect.GrantAdditionalMark &&
            contribution.Source.SourceKind == "temporary-modifier")
        {
            required.Add("modifierName");
            required.Add("modifierSource");
            optional.Add("windowFrom");
            optional.Add("windowTo");
        }

        var supplied = contribution.Parameters.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
        foreach (var name in required)
        {
            if (!supplied.ContainsKey(name))
                throw new ArgumentException(
                    $"Rule contribution {contribution.RuleId} is missing required parameter '{name}'.");
        }

        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        allowed.UnionWith(optional);
        foreach (var name in supplied.Keys)
        {
            if (!allowed.Contains(name))
                throw new ArgumentException(
                    $"Rule contribution {contribution.RuleId} contains unsupported parameter '{name}'.");
        }

        if (contribution.Effect == RuleContributionEffect.RetimeProcess &&
            (!int.TryParse(supplied["ticksPerStep"], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
             ticks <= 0))
            throw new ArgumentException("RetimeProcess ticksPerStep must be a positive invariant integer.");

        if (contribution.Effect == RuleContributionEffect.ModifyIntensity &&
            (!int.TryParse(supplied["factorPerMille"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var factor) ||
             factor < 0))
            throw new ArgumentException("ModifyIntensity factorPerMille must be a non-negative invariant integer.");

        var hasWindowFrom = supplied.TryGetValue("windowFrom", out var fromText);
        var hasWindowTo = supplied.TryGetValue("windowTo", out var toText);
        if (hasWindowFrom != hasWindowTo)
            throw new ArgumentException("Rule contribution windowFrom and windowTo must be supplied together.");
        if (hasWindowFrom)
        {
            if (!long.TryParse(fromText, NumberStyles.None, CultureInfo.InvariantCulture, out var from) || from < 0)
                throw new ArgumentException("Rule contribution windowFrom must be a non-negative invariant integer.");
            if (!string.IsNullOrEmpty(toText) &&
                (!long.TryParse(toText, NumberStyles.None, CultureInfo.InvariantCulture, out var to) || to < from))
                throw new ArgumentException("Rule contribution windowTo must be empty or not earlier than windowFrom.");
        }
    }

    private static RuleContributionEffectSchema Schema(
        RuleContributionEffect effect,
        bool supportsStaticSolverProjection,
        IReadOnlyList<string>? required = null,
        IReadOnlyList<string>? optional = null) =>
        new(
            effect,
            Array.AsReadOnly((required ?? Array.Empty<string>()).ToArray()),
            Array.AsReadOnly((optional ?? Array.Empty<string>()).ToArray()),
            supportsStaticSolverProjection);
}

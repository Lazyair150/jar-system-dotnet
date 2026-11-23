namespace JarSystem.Core.Domain;

/// <summary>
/// Transitional adapter from the existing WorkshopCapability/ModifierStack model to
/// the declarative contribution contract. It preserves today's semantics while making
/// every contribution attributable, serializable and fingerprintable.
/// </summary>
public static class WorkshopRuleContributionAdapter
{
    public static EffectiveRuleProfile Build(
        string boardId,
        int boardDefinitionVersion,
        long assignmentRevision,
        RuleContributionSource workshopSource,
        WorkshopCapability capability,
        ModifierStack modifiers,
        bool includeExecutionModifiers = true)
    {
        ArgumentNullException.ThrowIfNull(workshopSource);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(modifiers);
        workshopSource.Validate();

        var contributions = new List<RuleContribution>();
        foreach (var shape in capability.BaseMarks)
            contributions.Add(Create("core.workshop.base-mark", RuleContributionEffect.GrantBaseMark, shape, workshopSource));
        foreach (var shape in capability.AdditionalMarks)
            contributions.Add(Create("core.workshop.additional-mark", RuleContributionEffect.GrantAdditionalMark, shape, workshopSource));
        foreach (var liquid in capability.Ban)
            contributions.Add(Create("core.workshop.ban-liquid", RuleContributionEffect.BanLiquid, liquid, workshopSource));

        var index = 0;
        foreach (var modifier in modifiers.CaptureEffectiveModifiers())
        {
            if (!includeExecutionModifiers && (modifier is RetimeModifier or IntensityModifier))
                continue;
            var source = new RuleContributionSource(
                "temporary-modifier",
                modifier.Kind.ToString().ToLowerInvariant(),
                1,
                workshopSource.SourceInstanceId + "/" + modifier.Source + "/" + modifier.Name + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                workshopSource.SourceRevision,
                workshopSource.SlotId);
            var parameters = new List<RuleContributionParameter>();
            var effect = modifier.Kind switch
            {
                ModifierKind.Suppress => RuleContributionEffect.SuppressOperation,
                ModifierKind.Require => RuleContributionEffect.RequireCondition,
                ModifierKind.Grant => RuleContributionEffect.GrantAdditionalMark,
                ModifierKind.Retime => RuleContributionEffect.RetimeProcess,
                ModifierKind.Intensity => RuleContributionEffect.ModifyIntensity,
                _ => throw new ArgumentOutOfRangeException(nameof(modifiers), modifier.Kind, "Unsupported modifier kind.")
            };
            var target = modifier.Name;
            switch (modifier)
            {
                case GrantModifier grant:
                    target = string.IsNullOrWhiteSpace(grant.Shape) ? grant.Name : grant.Shape;
                    if (string.IsNullOrWhiteSpace(grant.Shape)) effect = RuleContributionEffect.RequireCondition;
                    break;
                case SuppressModifier suppress:
                    target = string.Join("|", suppress.Operation ?? "*", suppress.Shape ?? "*", suppress.Liquid ?? "*");
                    parameters.Add(new("operation", suppress.Operation ?? ""));
                    parameters.Add(new("shape", suppress.Shape ?? ""));
                    parameters.Add(new("liquid", suppress.Liquid ?? ""));
                    parameters.Add(new("reason", suppress.Reason));
                    break;
                case RequireModifier require:
                    target = string.IsNullOrWhiteSpace(require.Text) ? require.Name : require.Text;
                    parameters.Add(new("predicate", require.Violation == null ? "opaque" : "runtime"));
                    break;
                case RetimeModifier retime:
                    target = retime.ProcessKind ?? "*";
                    parameters.Add(new("ticksPerStep", retime.TicksPerStep.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    break;
                case IntensityModifier intensity:
                    target = string.IsNullOrWhiteSpace(intensity.Target) ? intensity.Name : intensity.Target;
                    parameters.Add(new("factorPerMille", intensity.FactorPerMille.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    break;
            }

            if (modifier.Window is { } window)
            {
                parameters.Add(new("windowFrom", window.From.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                parameters.Add(new("windowTo", window.To?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""));
            }
            parameters.Add(new("modifierSource", modifier.Source));
            parameters.Add(new("modifierName", modifier.Name));
            contributions.Add(new RuleContribution(
                "modifier." + modifier.Kind.ToString().ToLowerInvariant(),
                1,
                effect,
                string.IsNullOrWhiteSpace(target) ? modifier.Name : target,
                "workshop",
                modifier.Priority,
                source,
                Array.AsReadOnly(parameters.ToArray())));
            index++;
        }

        return EffectiveRuleProfile.Create(boardId, boardDefinitionVersion, assignmentRevision, contributions);
    }

    private static RuleContribution Create(
        string ruleId,
        RuleContributionEffect effect,
        string target,
        RuleContributionSource source) =>
        new(ruleId, 1, effect, target, "workshop", 0, source, Array.Empty<RuleContributionParameter>());
}

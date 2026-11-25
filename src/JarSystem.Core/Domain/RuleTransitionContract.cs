using System.Globalization;

namespace JarSystem.Core.Domain;

/// <summary>Deterministic result of evaluating one rule against one frozen state.</summary>
public sealed record RuleEvaluationResult(
    RuleDecisionKind Decision,
    string ReasonCode,
    string? Reason,
    RuleTransition? Transition)
{
    public bool IsAllowed => Decision == RuleDecisionKind.Allowed;
    public bool IsRejected => Decision == RuleDecisionKind.Rejected;
    public bool IsUndetermined => Decision == RuleDecisionKind.Undetermined;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ReasonCode))
            throw new ArgumentException("Rule evaluation requires a stable reason code.");
        if (IsAllowed && Transition is null)
            throw new ArgumentException("An allowed rule evaluation must carry its transition.");
        if (!IsAllowed && Transition is not null)
            throw new ArgumentException("Rejected or undetermined evaluation cannot carry an executable transition.");
    }
}

public enum RuleDecisionKind
{
    Allowed,
    Rejected,
    Undetermined
}

public enum RuleResourceKind
{
    Jar,
    Liquid
}

public enum RuleResourceEffectKind
{
    Consume,
    Produce,
    Transfer,
    Discard
}

/// <summary>
/// Stable address of an authoritative resource account. OwnerId is an exact runtime identity,
/// not a display name or a role label.
/// </summary>
public sealed record RuleResourceAccount(
    string AccountKind,
    string OwnerId)
{
    /// <summary>Present only for the new World-scoped identity contract; null preserves legacy v1.</summary>
    public AccountIdentity? WorldScopedIdentity { get; init; }

    public RuleResourceAccount(OwnerIdentity owner, string accountKind)
        : this(accountKind, owner.LocalOwnerId)
    {
        WorldScopedIdentity = new AccountIdentity(owner, accountKind);
    }

    /// <summary>
    /// Canonical account identity. New accounts use account:v2; the two-argument constructor
    /// remains an explicit legacy v1 compatibility path and is never silently upgraded.
    /// </summary>
    public string AccountId
    {
        get
        {
            Validate();
            return WorldScopedIdentity?.ToCanonicalString() ??
                "account:v1:" +
                AccountKind.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                AccountKind +
                OwnerId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
                OwnerId;
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountKind) || string.IsNullOrWhiteSpace(OwnerId))
            throw new ArgumentException("Resource account requires exact kind and owner identity.");
        if (WorldScopedIdentity is { } identity &&
            (!identity.IsValid ||
             !string.Equals(identity.AccountKind, AccountKind, StringComparison.Ordinal) ||
             !string.Equals(identity.Owner.LocalOwnerId, OwnerId, StringComparison.Ordinal)))
            throw new ArgumentException("World-scoped account identity does not match its legacy projection fields.");
    }
}

/// <summary>
/// Declarative resource ledger delta. It never mutates an account; the owning transaction applies it.
/// </summary>
public sealed record RuleResourceEffect(
    RuleResourceEffectKind Kind,
    RuleResourceKind ResourceKind,
    string ResourceId,
    int Amount,
    RuleResourceAccount? From,
    RuleResourceAccount? To)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ResourceId) || Amount <= 0)
            throw new ArgumentException("Resource effect requires a resource identity and positive amount.");
        From?.Validate();
        To?.Validate();
        switch (Kind)
        {
            case RuleResourceEffectKind.Consume when From is null || To is not null:
            case RuleResourceEffectKind.Discard when From is null || To is not null:
            case RuleResourceEffectKind.Produce when From is not null || To is null:
            case RuleResourceEffectKind.Transfer when From is null || To is null:
                throw new ArgumentException("Resource effect account endpoints do not match its effect kind.");
            case RuleResourceEffectKind.Transfer when From == To:
                throw new ArgumentException("A resource transfer must change account ownership/location.");
        }
    }
}

/// <summary>Explicit polarity edge carried by one Jar state transition.</summary>
public readonly record struct JarPolarityTransition(JarPolarity Before, JarPolarity After)
{
    public bool ChangesPolarity => Before != After;

    public static JarPolarityTransition From(RuleStateSnapshot before, RuleStateSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!Enum.TryParse<JarPolarity>(before.Values.GetValueOrDefault("polarity"), out var from) ||
            !Enum.IsDefined(from) ||
            !Enum.TryParse<JarPolarity>(after.Values.GetValueOrDefault("polarity"), out var to) ||
            !Enum.IsDefined(to))
            throw new ArgumentException("Jar transition snapshots require recognized before/after polarity values.");
        return new JarPolarityTransition(from, to);
    }
}

/// <summary>Canonical immutable snapshot of the facts consumed or produced by a rule.</summary>
public sealed class RuleStateSnapshot
{
    public IReadOnlyDictionary<string, string> Values { get; }

    public RuleStateSnapshot(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Value is null)
                throw new ArgumentException("Rule state fields require non-empty keys and canonical string values.");
            if (!canonical.TryAdd(item.Key, item.Value))
                throw new ArgumentException("Duplicate rule state field: " + item.Key);
        }
        if (canonical.ContainsKey("jarInstanceId"))
        {
            if (!canonical.TryGetValue("liquid", out var liquidValue) ||
                !canonical.TryGetValue("polarity", out var polarityValue))
                throw new ArgumentException("Jar transition snapshots require both liquid and derived polarity.");

            if (!Enum.TryParse<JarPolarity>(polarityValue, out var polarity) || !Enum.IsDefined(polarity))
                throw new ArgumentException("Jar transition snapshot contains an unknown polarity value.");
            if (string.IsNullOrEmpty(liquidValue) != (polarity == JarPolarity.Neutral))
                throw new ArgumentException("Jar transition snapshot must pair empty liquid with Neutral and non-empty liquid with one of the two opposed polarities.");
            // The identity-to-polarity mapping belongs to the owning World vocabulary.
            // This detached transition contract validates the structural three-state relation,
            // but must not infer polarity from a liquid display/resource name.
        }
        else if (canonical.ContainsKey("polarity"))
        {
            throw new ArgumentException("A polarity field requires an exact Jar instance identity.");
        }

        Values = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(canonical);
    }
}

/// <summary>
/// Canonical polarity preconditions and outputs for the existing authoritative Jar operations.
/// This validates state edges only; it never performs resource accounting.
/// </summary>
public static class JarPolarityRuleContract
{
    private static bool IsLiquid(JarPolarity polarity) =>
        polarity is JarPolarity.Cathode or JarPolarity.Anode;

    public static void Validate(string ruleId, JarPolarityTransition edge)
    {
        var valid = ruleId switch
        {
            "jar.workshop.pour-in" =>
                edge.Before == JarPolarity.Neutral && IsLiquid(edge.After),
            "jar.workshop.pour-out" =>
                IsLiquid(edge.Before) && edge.After == JarPolarity.Neutral,
            "jar.workshop.reshape-empty" =>
                edge.Before == JarPolarity.Neutral && edge.After == JarPolarity.Neutral,
            "jar.workshop.reshape-full" =>
                IsLiquid(edge.Before) && edge.After == JarPolarity.Neutral,
            "jar.workshop.match" =>
                edge.Before == edge.After,
            _ => true
        };

        if (!valid)
            throw new ArgumentException(
                "Jar polarity edge is incompatible with rule " + ruleId + ": " +
                edge.Before + " -> " + edge.After,
                nameof(edge));
    }
}

/// <summary>
/// Pure state transition emitted by a rule evaluator. State payloads are canonical immutable
/// snapshots; resource effects are explicit and account-addressed. No external state is mutated here.
/// </summary>
public sealed class RuleTransition
{
    public string RuleId { get; }
    public int RuleVersion { get; }
    public string Scope { get; }
    public string Target { get; }
    public RuleStateSnapshot Before { get; }
    public RuleStateSnapshot After { get; }
    public JarPolarityTransition? PolarityTransition { get; }
    public IReadOnlyList<RuleResourceEffect> ResourceEffects { get; }

    public RuleTransition(
        string ruleId,
        int ruleVersion,
        string scope,
        string target,
        RuleStateSnapshot before,
        RuleStateSnapshot after,
        IEnumerable<RuleResourceEffect> resourceEffects)
    {
        RuleId = ruleId;
        RuleVersion = ruleVersion;
        Scope = scope;
        Target = target;
        Before = before ?? throw new ArgumentNullException(nameof(before));
        After = after ?? throw new ArgumentNullException(nameof(after));
        var beforeHasJar = Before.Values.ContainsKey("jarInstanceId");
        var afterHasJar = After.Values.ContainsKey("jarInstanceId");
        if (beforeHasJar != afterHasJar)
            throw new ArgumentException("A rule transition cannot add or remove its Jar identity.");
        PolarityTransition = beforeHasJar
            ? JarPolarityTransition.From(Before, After)
            : null;
        ArgumentNullException.ThrowIfNull(resourceEffects);
        var copied = resourceEffects.ToArray();
        if (copied.Any(x => x is null))
            throw new ArgumentException("Rule transition resource effects cannot contain null.");
        ResourceEffects = Array.AsReadOnly(copied);
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RuleId) || RuleVersion <= 0 ||
            string.IsNullOrWhiteSpace(Scope) || string.IsNullOrWhiteSpace(Target))
            throw new ArgumentException("Rule transition requires exact rule identity, version, scope and target.");
        if (PolarityTransition is { } polarityTransition)
        {
            JarPolarityRuleContract.Validate(RuleId, polarityTransition);
            if (RuleId.StartsWith("jar.workshop.", StringComparison.Ordinal))
            {
                if (!Before.Values.TryGetValue("workshopPolarityMode", out var beforeMode) ||
                    !After.Values.TryGetValue("workshopPolarityMode", out var afterMode) ||
                    !string.Equals(beforeMode, afterMode, StringComparison.Ordinal))
                    throw new ArgumentException("Jar workshop transitions must freeze one unchanged Workshop polarity mode.");
            }
        }
        else if (RuleId.StartsWith("jar.workshop.", StringComparison.Ordinal))
            throw new ArgumentException("Jar workshop transitions require exact Jar state and polarity snapshots.");
        foreach (var effect in ResourceEffects) effect.Validate();
    }
}

/// <summary>
/// Adapts the existing authoritative WorkshopRulebook proof into the shared transition contract.
/// This adapter is intentionally limited to the four chemical operations. MATCH has its own lifecycle adapter.
/// </summary>
public static class WorkshopRuleTransitionAdapter
{

    private static RuleStateSnapshot Snapshot(
        in JarFacts facts,
        string workshopInstanceId,
        string jarInstanceId,
        WorkshopPolarityMode workshopPolarityMode) =>
        new(new[]
        {
            new KeyValuePair<string, string>("shape", facts.Shape),
            new KeyValuePair<string, string>("liquid", facts.Liquid ?? string.Empty),
            new KeyValuePair<string, string>("polarity", facts.Polarity.ToString()),
            new KeyValuePair<string, string>("marked", facts.IsMarked ? "true" : "false"),
            new KeyValuePair<string, string>("workshopInstanceId", workshopInstanceId),
            new KeyValuePair<string, string>("workshopPolarityMode", workshopPolarityMode.ToString()),
            new KeyValuePair<string, string>("jarInstanceId", jarInstanceId)
        });

    public static RuleEvaluationResult Evaluate(
        WorkshopRulebook.WorkshopOperationEvaluation evaluation,
        in JarFacts before,
        WorkshopOperation operation,
        string workshopInstanceId,
        string jarInstanceId,
        string scope = "workshop",
        int? orderId = null)
    {
        if (string.IsNullOrWhiteSpace(workshopInstanceId))
            throw new ArgumentException("Exact workshop instance identity is required.", nameof(workshopInstanceId));
        if (string.IsNullOrWhiteSpace(jarInstanceId))
            throw new ArgumentException("Exact Jar instance identity is required.", nameof(jarInstanceId));
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Rule scope is required.", nameof(scope));
        if (orderId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(orderId));

        var decision = evaluation.Decision;
        if (!decision.IsAllowed)
        {
            var rejected = new RuleEvaluationResult(
                decision.IsUndetermined ? RuleDecisionKind.Undetermined : RuleDecisionKind.Rejected,
                decision.ReasonCode, decision.Reason, null);
            rejected.Validate();
            return rejected;
        }

        if (evaluation.Transition is not { } transition)
            throw new ArgumentException("Allowed WorkshopRulebook evaluation is missing its transition.", nameof(evaluation));

        var expectedRuleId = operation.Kind switch
        {
            WorkshopOperationKind.PourIn => "jar.workshop.pour-in",
            WorkshopOperationKind.PourOut => "jar.workshop.pour-out",
            WorkshopOperationKind.ReshapeEmpty => "jar.workshop.reshape-empty",
            WorkshopOperationKind.ReshapeFull => "jar.workshop.reshape-full",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var definition = WorkshopRulebook.Definitions.First(item => item.RuleId == expectedRuleId);
        if (!string.Equals(transition.RuleId, expectedRuleId, StringComparison.Ordinal) ||
            transition.RuleVersion != definition.RuleVersion)
            throw new ArgumentException("Workshop proof identity/version does not match the requested operation.", nameof(evaluation));

        var effects = new List<RuleResourceEffect>(2);
        var freeLiquid = new RuleResourceAccount("workshop.free-liquid-staging", workshopInstanceId);
        var jarLiquid = new RuleResourceAccount("jar.internal-liquid", jarInstanceId);

        if (transition.ConsumedFreeLiquid is { } consumed)
        {
            if (operation.Kind == WorkshopOperationKind.PourIn)
            {
                effects.Add(new RuleResourceEffect(RuleResourceEffectKind.Transfer,
                    RuleResourceKind.Liquid, consumed, 1, freeLiquid, jarLiquid));
            }
            else
            {
                effects.Add(new RuleResourceEffect(RuleResourceEffectKind.Consume,
                    RuleResourceKind.Liquid, consumed, 1, freeLiquid, null));
            }
        }
        if (transition.ProducedFreeLiquid is { } produced)
        {
            var outputOwnerId = orderId is int exactOrderId
                ? workshopInstanceId + "/order/" + exactOrderId.ToString(CultureInfo.InvariantCulture)
                : workshopInstanceId + "/operation/" + jarInstanceId;
            var outputBoundary = new RuleResourceAccount("workshop.order-output-boundary", outputOwnerId);
            effects.Add(new RuleResourceEffect(RuleResourceEffectKind.Transfer,
                RuleResourceKind.Liquid, produced, 1, jarLiquid, outputBoundary));
        }
        if (transition.ConsumedJarLiquid is { } selfConsumed)
            effects.Add(new RuleResourceEffect(RuleResourceEffectKind.Consume,
                RuleResourceKind.Liquid, selfConsumed, 1, jarLiquid, null));

        var resultTransition = new RuleTransition(
            transition.RuleId,
            transition.RuleVersion,
            scope,
            operation.Kind.ToString(),
            Snapshot(before, workshopInstanceId, jarInstanceId, transition.WorkshopPolarityMode),
            Snapshot(transition.After, workshopInstanceId, jarInstanceId, transition.WorkshopPolarityMode),
            Array.AsReadOnly(effects.ToArray()));
        resultTransition.Validate();

        var allowed = new RuleEvaluationResult(
            RuleDecisionKind.Allowed, "rule.allowed", null, resultTransition);
        allowed.Validate();
        return allowed;
    }
}


/// <summary>
/// Adapts the authoritative MATCH lifecycle proof. MATCH is deliberately separate from
/// WorkshopOperation: it marks one exact Jar but does not perform a chemical operation
/// or move liquid between resource accounts.
/// </summary>
public static class MatchRuleTransitionAdapter
{
    private static RuleStateSnapshot Snapshot(
        in JarFacts facts,
        string workshopInstanceId,
        string jarInstanceId,
        WorkshopPolarityMode workshopPolarityMode) =>
        new(new[]
        {
            new KeyValuePair<string, string>("shape", facts.Shape),
            new KeyValuePair<string, string>("liquid", facts.Liquid ?? string.Empty),
            new KeyValuePair<string, string>("polarity", facts.Polarity.ToString()),
            new KeyValuePair<string, string>("marked", facts.IsMarked ? "true" : "false"),
            new KeyValuePair<string, string>("workshopInstanceId", workshopInstanceId),
            new KeyValuePair<string, string>("workshopPolarityMode", workshopPolarityMode.ToString()),
            new KeyValuePair<string, string>("jarInstanceId", jarInstanceId)
        });

    public static RuleEvaluationResult Evaluate(
        WorkshopRulebook.WorkshopOperationEvaluation evaluation,
        in JarFacts before,
        string workshopInstanceId,
        string jarInstanceId,
        string scope = "workshop")
    {
        if (string.IsNullOrWhiteSpace(workshopInstanceId))
            throw new ArgumentException("Exact workshop instance identity is required.", nameof(workshopInstanceId));
        if (string.IsNullOrWhiteSpace(jarInstanceId))
            throw new ArgumentException("Exact Jar instance identity is required.", nameof(jarInstanceId));
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Rule scope is required.", nameof(scope));

        var decision = evaluation.Decision;
        if (!decision.IsAllowed)
        {
            var rejected = new RuleEvaluationResult(
                decision.IsUndetermined ? RuleDecisionKind.Undetermined : RuleDecisionKind.Rejected,
                decision.ReasonCode, decision.Reason, null);
            rejected.Validate();
            return rejected;
        }

        if (evaluation.Transition is not { } transition)
            throw new ArgumentException("Allowed MATCH proof is missing its lifecycle transition.", nameof(evaluation));
        var matchDefinition = WorkshopRulebook.Definitions.First(item => item.Operator == "MATCH");
        if (!string.Equals(transition.RuleId, matchDefinition.RuleId, StringComparison.Ordinal) ||
            transition.RuleVersion != matchDefinition.RuleVersion)
            throw new ArgumentException("MATCH adapter received a proof with an unexpected rule identity/version.", nameof(evaluation));
        if (transition.After.Shape != before.Shape ||
            !string.Equals(transition.After.Liquid, before.Liquid, StringComparison.Ordinal) ||
            transition.After.IsMarked != true || before.IsMarked)
            throw new ArgumentException("MATCH proof does not represent the expected mark-only lifecycle transition.", nameof(evaluation));

        var resultTransition = new RuleTransition(
            transition.RuleId,
            transition.RuleVersion,
            scope,
            jarInstanceId,
            Snapshot(before, workshopInstanceId, jarInstanceId, transition.WorkshopPolarityMode),
            Snapshot(transition.After, workshopInstanceId, jarInstanceId, transition.WorkshopPolarityMode),
            Array.Empty<RuleResourceEffect>());
        resultTransition.Validate();

        var allowed = new RuleEvaluationResult(
            RuleDecisionKind.Allowed, "rule.allowed", null, resultTransition);
        allowed.Validate();
        return allowed;
    }
}

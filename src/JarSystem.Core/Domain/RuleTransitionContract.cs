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
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccountKind) || string.IsNullOrWhiteSpace(OwnerId))
            throw new ArgumentException("Resource account requires exact kind and owner identity.");
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
        Values = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(canonical);
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
        string jarInstanceId) =>
        new(new[]
        {
            new KeyValuePair<string, string>("shape", facts.Shape),
            new KeyValuePair<string, string>("liquid", facts.Liquid ?? string.Empty),
            new KeyValuePair<string, string>("polarity", facts.Polarity.ToString()),
            new KeyValuePair<string, string>("marked", facts.IsMarked ? "true" : "false"),
            new KeyValuePair<string, string>("workshopInstanceId", workshopInstanceId),
            new KeyValuePair<string, string>("jarInstanceId", jarInstanceId)
        });

    public static RuleEvaluationResult Evaluate(
        WorkshopRulebook.WorkshopOperationEvaluation evaluation,
        in JarFacts before,
        WorkshopOperation operation,
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
            effects.Add(new RuleResourceEffect(RuleResourceEffectKind.Consume,
                RuleResourceKind.Liquid, consumed, 1, freeLiquid, null));
        if (transition.ProducedFreeLiquid is { } produced)
            effects.Add(new RuleResourceEffect(RuleResourceEffectKind.Produce,
                RuleResourceKind.Liquid, produced, 1, null, freeLiquid));
        if (transition.ConsumedJarLiquid is { } selfConsumed)
            effects.Add(new RuleResourceEffect(RuleResourceEffectKind.Consume,
                RuleResourceKind.Liquid, selfConsumed, 1, jarLiquid, null));

        var resultTransition = new RuleTransition(
            transition.RuleId,
            transition.RuleVersion,
            scope,
            operation.Kind.ToString(),
            Snapshot(before, workshopInstanceId, jarInstanceId),
            Snapshot(transition.After, workshopInstanceId, jarInstanceId),
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
        string jarInstanceId) =>
        new(new[]
        {
            new KeyValuePair<string, string>("shape", facts.Shape),
            new KeyValuePair<string, string>("liquid", facts.Liquid ?? string.Empty),
            new KeyValuePair<string, string>("polarity", facts.Polarity.ToString()),
            new KeyValuePair<string, string>("marked", facts.IsMarked ? "true" : "false"),
            new KeyValuePair<string, string>("workshopInstanceId", workshopInstanceId),
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
            Snapshot(before, workshopInstanceId, jarInstanceId),
            Snapshot(transition.After, workshopInstanceId, jarInstanceId),
            Array.Empty<RuleResourceEffect>());
        resultTransition.Validate();

        var allowed = new RuleEvaluationResult(
            RuleDecisionKind.Allowed, "rule.allowed", null, resultTransition);
        allowed.Validate();
        return allowed;
    }
}

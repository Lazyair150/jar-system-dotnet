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

/// <summary>
/// Pure state transition emitted by a rule evaluator. State payloads are domain-owned immutable
/// values; resource effects are explicit and account-addressed. No external state is mutated here.
/// </summary>
public sealed record RuleTransition(
    string RuleId,
    int RuleVersion,
    string Scope,
    string Target,
    object Before,
    object After,
    IReadOnlyList<RuleResourceEffect> ResourceEffects)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RuleId) || RuleVersion <= 0 ||
            string.IsNullOrWhiteSpace(Scope) || string.IsNullOrWhiteSpace(Target))
            throw new ArgumentException("Rule transition requires exact rule identity, version, scope and target.");
        ArgumentNullException.ThrowIfNull(Before);
        ArgumentNullException.ThrowIfNull(After);
        ArgumentNullException.ThrowIfNull(ResourceEffects);
        foreach (var effect in ResourceEffects) effect.Validate();
    }
}

/// <summary>
/// Adapts the existing authoritative WorkshopRulebook proof into the shared transition contract.
/// This adapter is intentionally limited to the four chemical operations and MATCH.
/// </summary>
public static class WorkshopRuleTransitionAdapter
{
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
            before,
            transition.After,
            Array.AsReadOnly(effects.ToArray()));
        resultTransition.Validate();

        var allowed = new RuleEvaluationResult(
            RuleDecisionKind.Allowed, "rule.allowed", null, resultTransition);
        allowed.Validate();
        return allowed;
    }
}

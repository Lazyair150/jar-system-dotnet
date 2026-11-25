using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JarSystem.Core.Domain;

namespace JarSystem.Core.World.Solving;

/// <summary>Stable identifiers for built-in Jar order candidate operators.</summary>
public static class WorldOrderOperatorIds
{
    public const string Match = "MATCH";
    public const string PourIn = "POUR_IN";
    public const string PourOut = "POUR_OUT";
    public const string ReshapeEmpty = "RESHAPE_EMPTY";
    public const string ReshapeFull = "RESHAPE_FULL";
    public const string PreparePourOut = "PREPARE_POUR_OUT";
}

/// <summary>
/// Trusted Core-side provider for projecting one registered rule operator into structural candidates.
/// Providers are algorithm extensions, not persisted RuleContribution code.
/// </summary>
public interface IWorldOrderRuleCandidateProvider
{
    string ProviderId { get; }
    int Version { get; }
    int Order { get; }
    IEnumerable<WorldOrderRuleCandidate> Enumerate(WorldOrderRuleCandidateRequest request);
}

public sealed class WorldOrderRuleCandidateProviderRegistry
{
    private readonly IReadOnlyList<IWorldOrderRuleCandidateProvider> _providers;

    public static WorldOrderRuleCandidateProviderRegistry Default { get; } = new(
    [
        new MatchWorldOrderRuleCandidateProvider(),
        new PourInWorldOrderRuleCandidateProvider(),
        new PourOutWorldOrderRuleCandidateProvider(),
        new ReshapeEmptyWorldOrderRuleCandidateProvider(),
        new ReshapeFullWorldOrderRuleCandidateProvider(),
        new PreparatoryPourOutWorldOrderRuleCandidateProvider()
    ]);

    public IReadOnlyList<IWorldOrderRuleCandidateProvider> Providers => _providers;
    public string Fingerprint { get; }

    public WorldOrderRuleCandidateProviderRegistry(IEnumerable<IWorldOrderRuleCandidateProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var supplied = providers.ToArray();
        if (supplied.Length == 0)
            throw new ArgumentException("At least one rule candidate provider is required.", nameof(providers));
        if (supplied.Any(x => x == null))
            throw new ArgumentException("Rule candidate providers cannot contain null.", nameof(providers));
        if (supplied.Any(x => string.IsNullOrWhiteSpace(x.ProviderId) || x.Version <= 0 || x.Order < 0))
            throw new ArgumentException("Rule candidate providers require stable identity, positive version and non-negative order.", nameof(providers));
        if (supplied.Select(x => x.ProviderId).Distinct(StringComparer.Ordinal).Count() != supplied.Length)
            throw new ArgumentException("Rule candidate provider IDs must be unique.", nameof(providers));

        _providers = Array.AsReadOnly(supplied
            .OrderBy(x => x.Order)
            .ThenBy(x => x.ProviderId, StringComparer.Ordinal)
            .ToArray());
        Fingerprint = ComputeFingerprint(_providers);
    }

    private static string ComputeFingerprint(IReadOnlyList<IWorldOrderRuleCandidateProvider> providers)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("JarSystem.WorldOrderRuleCandidateProviderRegistry.v1");
            writer.Write(providers.Count);
            foreach (var provider in providers)
            {
                writer.Write(provider.ProviderId);
                writer.Write(provider.Version);
                writer.Write(provider.Order);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

public sealed class WorldOrderRuleCandidateRequest
{
    public RuleContext Rules { get; }
    public IReadOnlyList<string> SourceShapes { get; }
    public int RequirementIndex { get; }
    public string? TargetShape { get; }
    public string? TargetLiquid { get; }
    public bool IsPreparatory { get; }
    public WorldOrderRuleCandidateFactory Factory { get; }

    internal WorldOrderRuleCandidateRequest(
        RuleContext rules,
        IReadOnlyList<string> sourceShapes,
        int requirementIndex,
        string? targetShape,
        string? targetLiquid,
        bool isPreparatory,
        WorldOrderRuleCandidateFactory factory)
    {
        Rules = rules;
        SourceShapes = sourceShapes;
        RequirementIndex = requirementIndex;
        TargetShape = targetShape;
        TargetLiquid = targetLiquid;
        IsPreparatory = isPreparatory;
        Factory = factory;
    }
}

/// <summary>
/// Shared, authoritative rule evaluator bridge used by candidate providers.
/// Providers describe candidate intent; this factory applies the Core rulebook and resource staging semantics.
/// </summary>
public sealed class WorldOrderRuleCandidateFactory
{
    private readonly RuleContext _rules;
    private readonly int _stagingCapacity;
    private readonly IReadOnlyCollection<string> _bannedLiquids;

    internal WorldOrderRuleCandidateFactory(
        RuleContext rules,
        int stagingCapacity,
        IReadOnlyCollection<string> bannedLiquids)
    {
        _rules = rules;
        _stagingCapacity = stagingCapacity;
        _bannedLiquids = bannedLiquids;
    }

    public WorldOrderRuleCandidate? Match(
        int requirementIndex,
        string sourceShape,
        string? sourceLiquid,
        string targetShape,
        string? targetLiquid)
    {
        var facts = new JarFacts(sourceShape, sourceLiquid, false, _rules.ResolvePolarity(sourceLiquid));
        var evaluation = WorkshopRulebook.EvaluateMatchTransition(_rules, facts);
        if (!evaluation.Decision.IsAllowed || evaluation.Transition == null) return null;
        var transition = evaluation.Transition.Value;
        return new WorldOrderRuleCandidate(
            requirementIndex, sourceShape, sourceLiquid, WorldOrderOperatorIds.Match,
            transition.RuleId, transition.RuleVersion,
            transition.After.Shape, transition.After.Liquid,
            null, null, null, null);
    }

    public WorldOrderRuleCandidate? Transition(
        int requirementIndex,
        string sourceShape,
        string? sourceLiquid,
        string targetShape,
        string? targetLiquid,
        WorkshopOperation operation,
        string operatorId,
        IReadOnlyDictionary<string, int> syntheticSupply,
        bool requireProducedLiquidAdmission)
    {
        var staging = new WorkshopLiquidStaging(_stagingCapacity, _bannedLiquids);
        var scope = new OrderLiquidScope(1, staging);
        foreach (var item in syntheticSupply)
        {
            var receipt = staging.Receive(1, item.Key, item.Value);
            if (receipt.AcceptedAmount != item.Value) return null;
        }

        var facts = new JarFacts(sourceShape, sourceLiquid, false, _rules.ResolvePolarity(sourceLiquid));
        var evaluation = WorkshopRulebook.EvaluateTransition(_rules, scope, facts, operation);
        if (!evaluation.Decision.IsAllowed || evaluation.Transition == null) return null;
        var transition = evaluation.Transition.Value;

        if (requireProducedLiquidAdmission && transition.ProducedFreeLiquid is { } produced)
        {
            var admission = staging.Receive(1, produced, 1);
            if (admission.AcceptedAmount != 1) return null;
        }

        return new WorldOrderRuleCandidate(
            requirementIndex,
            sourceShape,
            sourceLiquid,
            operatorId,
            transition.RuleId,
            transition.RuleVersion,
            targetShape,
            targetLiquid,
            operation.CostLiquid,
            transition.ConsumedFreeLiquid,
            transition.ProducedFreeLiquid,
            transition.ConsumedJarLiquid);
    }
}

internal sealed class MatchWorldOrderRuleCandidateProvider : IWorldOrderRuleCandidateProvider
{
    public string ProviderId => "jar.match";
    public int Version => 1;
    public int Order => 10;

    public IEnumerable<WorldOrderRuleCandidate> Enumerate(WorldOrderRuleCandidateRequest request)
    {
        if (request.IsPreparatory || request.TargetShape == null) yield break;
        foreach (var sourceShape in request.SourceShapes)
        {
            if (!StringComparer.Ordinal.Equals(sourceShape, request.TargetShape)) continue;
            var candidate = request.Factory.Match(
                request.RequirementIndex, sourceShape, request.TargetLiquid,
                request.TargetShape, request.TargetLiquid);
            if (candidate != null) yield return candidate;
        }
    }
}

internal sealed class PourInWorldOrderRuleCandidateProvider : IWorldOrderRuleCandidateProvider
{
    public string ProviderId => "jar.pour-in";
    public int Version => 1;
    public int Order => 20;

    public IEnumerable<WorldOrderRuleCandidate> Enumerate(WorldOrderRuleCandidateRequest request)
    {
        if (request.IsPreparatory || request.TargetShape == null || request.TargetLiquid == null) yield break;
        foreach (var sourceShape in request.SourceShapes)
        {
            if (!StringComparer.Ordinal.Equals(sourceShape, request.TargetShape)) continue;
            var supply = new Dictionary<string, int>(StringComparer.Ordinal) { [request.TargetLiquid] = 1 };
            var candidate = request.Factory.Transition(
                request.RequirementIndex, sourceShape, null,
                request.TargetShape, request.TargetLiquid,
                WorkshopOperation.PourIn(request.TargetLiquid),
                WorldOrderOperatorIds.PourIn, supply, requireProducedLiquidAdmission: false);
            if (candidate != null) yield return candidate;
        }
    }
}

internal sealed class PourOutWorldOrderRuleCandidateProvider : IWorldOrderRuleCandidateProvider
{
    public string ProviderId => "jar.pour-out";
    public int Version => 1;
    public int Order => 30;

    public IEnumerable<WorldOrderRuleCandidate> Enumerate(WorldOrderRuleCandidateRequest request)
    {
        if (request.IsPreparatory || request.TargetShape == null || request.TargetLiquid != null) yield break;
        foreach (var sourceShape in request.SourceShapes)
        {
            if (!StringComparer.Ordinal.Equals(sourceShape, request.TargetShape)) continue;
            foreach (var sourceLiquid in request.Rules.Liquids)
            {
                var candidate = request.Factory.Transition(
                    request.RequirementIndex, sourceShape, sourceLiquid,
                    request.TargetShape, null,
                    WorkshopOperation.PourOut(), WorldOrderOperatorIds.PourOut,
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    requireProducedLiquidAdmission: false);
                if (candidate != null) yield return candidate;
            }
        }
    }
}

internal sealed class ReshapeEmptyWorldOrderRuleCandidateProvider : IWorldOrderRuleCandidateProvider
{
    public string ProviderId => "jar.reshape-empty";
    public int Version => 1;
    public int Order => 40;

    public IEnumerable<WorldOrderRuleCandidate> Enumerate(WorldOrderRuleCandidateRequest request)
    {
        if (request.IsPreparatory || request.TargetShape == null || request.TargetLiquid != null) yield break;
        foreach (var sourceShape in request.SourceShapes)
        foreach (var costLiquid in request.Rules.Liquids)
        {
            var supply = new Dictionary<string, int>(StringComparer.Ordinal) { [costLiquid] = 1 };
            var candidate = request.Factory.Transition(
                request.RequirementIndex, sourceShape, null,
                request.TargetShape, null,
                WorkshopOperation.ReshapeEmpty(request.TargetShape, costLiquid),
                WorldOrderOperatorIds.ReshapeEmpty, supply,
                requireProducedLiquidAdmission: false);
            if (candidate != null) yield return candidate;
        }
    }
}

internal sealed class ReshapeFullWorldOrderRuleCandidateProvider : IWorldOrderRuleCandidateProvider
{
    public string ProviderId => "jar.reshape-full";
    public int Version => 1;
    public int Order => 50;

    public IEnumerable<WorldOrderRuleCandidate> Enumerate(WorldOrderRuleCandidateRequest request)
    {
        if (request.IsPreparatory || request.TargetShape == null || request.TargetLiquid != null) yield break;
        foreach (var sourceShape in request.SourceShapes)
        foreach (var sourceLiquid in request.Rules.Liquids)
        {
            var candidate = request.Factory.Transition(
                request.RequirementIndex, sourceShape, sourceLiquid,
                request.TargetShape, null,
                WorkshopOperation.ReshapeFull(request.TargetShape),
                WorldOrderOperatorIds.ReshapeFull,
                new Dictionary<string, int>(StringComparer.Ordinal),
                requireProducedLiquidAdmission: false);
            if (candidate != null) yield return candidate;
        }
    }
}

internal sealed class PreparatoryPourOutWorldOrderRuleCandidateProvider : IWorldOrderRuleCandidateProvider
{
    public string ProviderId => "jar.preparatory-pour-out";
    public int Version => 1;
    public int Order => 60;

    public IEnumerable<WorldOrderRuleCandidate> Enumerate(WorldOrderRuleCandidateRequest request)
    {
        if (!request.IsPreparatory || request.TargetShape == null) yield break;
        foreach (var sourceShape in request.SourceShapes)
        foreach (var sourceLiquid in request.Rules.Liquids)
        {
            var candidate = request.Factory.Transition(
                request.RequirementIndex, sourceShape, sourceLiquid,
                request.TargetShape, null,
                WorkshopOperation.PourOut(), WorldOrderOperatorIds.PreparePourOut,
                new Dictionary<string, int>(StringComparer.Ordinal),
                requireProducedLiquidAdmission: true);
            if (candidate != null) yield return candidate;
        }
    }
}

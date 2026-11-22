using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 一条需求在某个规则/工作台 Profile 下可能采用的单 Jar 规则候选。
/// 不包含 Jar ID、当前库存余额、资源预留或执行游标。
/// </summary>
public sealed record WorldOrderRuleCandidate(
    int RequirementIndex,
    string SourceShape,
    string? SourceLiquid,
    string Operation,
    string RuleId,
    int RuleVersion,
    string TargetShape,
    string? TargetLiquid,
    string? CostLiquid,
    string? ConsumedFreeLiquid,
    string? ProducedFreeLiquid,
    string? ConsumedJarLiquid);

/// <summary>某条订单需求的结构候选集合。</summary>
public sealed class WorldOrderRequirementCandidates
{
    public int RequirementIndex { get; }
    public string TargetShape { get; }
    public string? TargetLiquid { get; }
    public IReadOnlyList<WorldOrderRuleCandidate> Candidates { get; }

    internal WorldOrderRequirementCandidates(
        int requirementIndex,
        string targetShape,
        string? targetLiquid,
        IEnumerable<WorldOrderRuleCandidate> candidates)
    {
        RequirementIndex = requirementIndex;
        TargetShape = targetShape;
        TargetLiquid = targetLiquid;
        Candidates = Array.AsReadOnly(candidates.ToArray());
    }
}

/// <summary>
/// 与当前 Jar/液体库存无关的规则候选目录。
/// 它只回答“什么输入 Jar 状态可通过哪些规则算子满足该需求”，不宣称候选组合已经构成完整订单解。
/// </summary>
public sealed class WorldOrderRuleCandidateProfile
{
    public string WorkshopName { get; }
    public string Role { get; }
    public string RuleSetVersion { get; }
    public string WorkshopProfileVersion { get; }
    public string SpecificationVersion { get; }
    public bool IsUndetermined { get; }
    public string? Reason { get; }
    public IReadOnlyList<WorldOrderRequirementCandidates> Requirements { get; }

    /// <summary>
    /// 若某条需求没有任何合法的单 Jar 输入/算子候选，则该工作台 Profile 无法独立满足它。
    /// 对 B 类订单仍需与另一个岗位的候选集合合并判断。
    /// </summary>
    public bool CanStructurallyCoverEveryRequirement =>
        !IsUndetermined && Requirements.All(x => x.Candidates.Count > 0);

    internal WorldOrderRuleCandidateProfile(
        string workshopName,
        string role,
        string ruleSetVersion,
        string workshopProfileVersion,
        string specificationVersion,
        IEnumerable<WorldOrderRequirementCandidates> requirements,
        bool isUndetermined = false,
        string? reason = null)
    {
        WorkshopName = workshopName;
        Role = role;
        RuleSetVersion = ruleSetVersion;
        WorkshopProfileVersion = workshopProfileVersion;
        SpecificationVersion = specificationVersion;
        Requirements = Array.AsReadOnly(requirements.ToArray());
        IsUndetermined = isUndetermined;
        Reason = reason;
    }
}

/// <summary>
/// 生成可跨资源库存变化复用的单 Jar 规则候选。
/// 资源余额用最小合成供给只用于问“规则是否允许”；候选中只保存资源效果，不保存合成余额。
/// </summary>
public static class WorldOrderRuleCandidateCatalog
{
    public static string Fingerprint(JarOrderSpecification specification)
    {
        if (specification == null) throw new ArgumentNullException(nameof(specification));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("JarSystem.JarOrderSpecification.v1");
            writer.Write(specification.Type);
            writer.Write(specification.Requirements.Count);
            foreach (var requirement in specification.Requirements)
            {
                writer.Write(requirement.Shape);
                writer.Write(requirement.Liquid != null);
                if (requirement.Liquid != null) writer.Write(requirement.Liquid);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    public static WorldOrderRuleCandidateProfile Build(
        WorldSnapshot world,
        WorkshopSolveSnapshot workshop,
        string role,
        JarOrderSpecification specification)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (workshop == null) throw new ArgumentNullException(nameof(workshop));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("岗位名称不能为空", nameof(role));
        if (specification == null) throw new ArgumentNullException(nameof(specification));

        var specificationVersion = Fingerprint(specification);
        if (workshop.HasOpaqueSolverRules)
            return new WorldOrderRuleCandidateProfile(
                workshop.Name, role, world.RuleSetVersion, world.WorkshopProfileVersion,
                specificationVersion, Array.Empty<WorldOrderRequirementCandidates>(),
                isUndetermined: true, reason: "工作台包含无法静态重建的规则");

        var rules = workshop.CreateRuleContext();
        var results = new List<WorldOrderRequirementCandidates>(specification.Requirements.Count);
        for (var requirementIndex = 0; requirementIndex < specification.Requirements.Count; requirementIndex++)
        {
            var requirement = specification.Requirements[requirementIndex];
            var candidates = BuildRequirementCandidates(
                rules, requirementIndex, requirement.Shape, requirement.Liquid);
            results.Add(new WorldOrderRequirementCandidates(
                requirementIndex, requirement.Shape, requirement.Liquid, candidates));
        }

        return new WorldOrderRuleCandidateProfile(
            workshop.Name, role, world.RuleSetVersion, world.WorkshopProfileVersion,
            specificationVersion, results);
    }

    private static List<WorldOrderRuleCandidate> BuildRequirementCandidates(
        RuleContext rules,
        int requirementIndex,
        string targetShape,
        string? targetLiquid)
    {
        var candidates = new List<WorldOrderRuleCandidate>();
        foreach (var sourceShape in Constants.SHAPES)
        {
            // Direct match is a lifecycle/allocation rule, not a Jar chemistry transform.
            if (sourceShape == targetShape)
            {
                if (targetLiquid == null)
                    AddMatch(rules, candidates, requirementIndex, sourceShape, null, targetShape, null);
                else
                    AddMatch(rules, candidates, requirementIndex, sourceShape, targetLiquid, targetShape, targetLiquid);

                // Full target can also be made by pouring into an empty Jar of the target shape.
                if (targetLiquid != null)
                {
                    var syntheticSupply = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        [targetLiquid] = 1
                    };
                    AddTransition(rules, candidates, requirementIndex,
                        sourceShape, null, targetShape, targetLiquid,
                        WorkshopOperation.PourIn(targetLiquid), "POUR_IN", syntheticSupply);
                }

                // Empty target can be made by pouring out any usable liquid from a same-shape Jar.
                if (targetLiquid == null)
                {
                    foreach (var sourceLiquid in Constants.LIQUIDS)
                        AddTransition(rules, candidates, requirementIndex,
                            sourceShape, sourceLiquid, targetShape, null,
                            WorkshopOperation.PourOut(), "POUR_OUT",
                            new Dictionary<string, int>(StringComparer.Ordinal));
                }
            }

            if (targetLiquid != null) continue;

            // Empty-jar reshape consumes one unit from a free-liquid account.
            foreach (var costLiquid in Constants.LIQUIDS)
            {
                var syntheticSupply = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [costLiquid] = 1
                };
                AddTransition(rules, candidates, requirementIndex,
                    sourceShape, null, targetShape, null,
                    WorkshopOperation.ReshapeEmpty(targetShape, costLiquid),
                    "RESHAPE_EMPTY", syntheticSupply);
            }

            // Full-jar reshape self-consumes its internal liquid; it never creates free liquid.
            foreach (var sourceLiquid in Constants.LIQUIDS)
                AddTransition(rules, candidates, requirementIndex,
                    sourceShape, sourceLiquid, targetShape, null,
                    WorkshopOperation.ReshapeFull(targetShape),
                    "RESHAPE_FULL", new Dictionary<string, int>(StringComparer.Ordinal));
        }

        return candidates;
    }

    private static void AddMatch(
        RuleContext rules,
        List<WorldOrderRuleCandidate> candidates,
        int requirementIndex,
        string sourceShape,
        string? sourceLiquid,
        string targetShape,
        string? targetLiquid)
    {
        var facts = new JarFacts(sourceShape, sourceLiquid, false);
        if (!WorkshopRulebook.EvaluateMatch(rules, facts).IsAllowed) return;
        var definition = WorkshopRulebook.Definitions[4];
        candidates.Add(new WorldOrderRuleCandidate(
            requirementIndex, sourceShape, sourceLiquid, "MATCH",
            definition.RuleId, definition.RuleVersion,
            targetShape, targetLiquid, null, null, null, null));
    }

    private static void AddTransition(
        RuleContext rules,
        List<WorldOrderRuleCandidate> candidates,
        int requirementIndex,
        string sourceShape,
        string? sourceLiquid,
        string targetShape,
        string? targetLiquid,
        WorkshopOperation operation,
        string operatorName,
        IReadOnlyDictionary<string, int> syntheticSupply)
    {
        var evaluation = WorkshopRulebook.EvaluateTransition(
            rules, 1, new JarFacts(sourceShape, sourceLiquid, false),
            operation, syntheticSupply);
        if (!evaluation.Decision.IsAllowed || evaluation.Transition == null) return;
        var transition = evaluation.Transition.Value;
        candidates.Add(new WorldOrderRuleCandidate(
            requirementIndex,
            sourceShape,
            sourceLiquid,
            operatorName,
            transition.RuleId,
            transition.RuleVersion,
            targetShape,
            targetLiquid,
            operation.CostLiquid,
            transition.ConsumedFreeLiquid,
            transition.ProducedFreeLiquid,
            transition.ConsumedJarLiquid));
    }
}

/// <summary>
/// 只按规则/工作台 Profile/订单规格缓存规则候选，不把资源余额纳入缓存键。
/// ResourceVersion 改变时同一 Profile 可直接复用；规则、工作台属性或订单规格改变时生成新 Profile。
/// </summary>
public sealed class WorldOrderRuleCandidateBook
{
    private readonly Dictionary<CacheKey, WorldOrderRuleCandidateProfile> _profiles = new();
    private const int MaxEntries = 512;

    public int CacheHits { get; private set; }
    public int CacheMisses { get; private set; }

    public WorldOrderRuleCandidateProfile GetOrBuild(
        WorldSnapshot world,
        WorkshopSolveSnapshot workshop,
        string role,
        JarOrderSpecification specification)
    {
        var specificationVersion = WorldOrderRuleCandidateCatalog.Fingerprint(specification);
        var key = new CacheKey(
            role,
            world.RuleSetVersion,
            world.WorkshopProfileVersion,
            specificationVersion,
            workshop.Name);

        if (_profiles.TryGetValue(key, out var cached))
        {
            CacheHits++;
            return cached;
        }

        CacheMisses++;
        var built = WorldOrderRuleCandidateCatalog.Build(world, workshop, role, specification);
        if (_profiles.Count >= MaxEntries) _profiles.Clear();
        _profiles[key] = built;
        return built;
    }

    public void Clear()
    {
        _profiles.Clear();
        CacheHits = 0;
        CacheMisses = 0;
    }

    private readonly record struct CacheKey(
        string Role,
        string RuleSetVersion,
        string WorkshopProfileVersion,
        string SpecificationVersion,
        string WorkshopName);
}

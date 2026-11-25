using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.World;

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
    /// 可作为订单内前置资源转换的通用倒液操作。它们不直接满足某一需求，
    /// 但可为后续 PourIn / ReshapeEmpty 产生订单游离液体。
    /// </summary>
    public IReadOnlyList<WorldOrderRuleCandidate> PreparatoryOperations { get; }

    /// <summary>
    /// 若某条需求没有任何合法的单 Jar 输入/算子候选，则该工作台 Profile 无法独立满足它。
    /// 对 B 类订单仍需与另一个岗位的候选集合合并判断。
    /// </summary>
    public bool HasDirectCandidateForEveryRequirement =>
        !IsUndetermined && Requirements.All(x => x.Candidates.Count > 0);

    internal WorldOrderRuleCandidateProfile(
        string workshopName,
        string role,
        string ruleSetVersion,
        string workshopProfileVersion,
        string specificationVersion,
        IEnumerable<WorldOrderRequirementCandidates> requirements,
        IEnumerable<WorldOrderRuleCandidate>? preparatoryOperations = null,
        bool isUndetermined = false,
        string? reason = null)
    {
        WorkshopName = workshopName;
        Role = role;
        RuleSetVersion = ruleSetVersion;
        WorkshopProfileVersion = workshopProfileVersion;
        SpecificationVersion = specificationVersion;
        Requirements = Array.AsReadOnly(requirements.ToArray());
        PreparatoryOperations = Array.AsReadOnly((preparatoryOperations ?? Array.Empty<WorldOrderRuleCandidate>()).ToArray());
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
    public static string Fingerprint(JarOrderSpecification specification) =>
        Fingerprint(specification, WorldOrderRuleCandidateProviderRegistry.Default);

    public static string Fingerprint(
        JarOrderSpecification specification,
        WorldOrderRuleCandidateProviderRegistry providerRegistry)
    {
        if (specification == null) throw new ArgumentNullException(nameof(specification));
        if (providerRegistry == null) throw new ArgumentNullException(nameof(providerRegistry));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("JarSystem.JarOrderSpecification.v2");
            writer.Write(providerRegistry.Fingerprint);
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
        JarOrderSpecification specification,
        WorldOrderRuleCandidateProviderRegistry? providerRegistry = null)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (workshop == null) throw new ArgumentNullException(nameof(workshop));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("岗位名称不能为空", nameof(role));
        if (specification == null) throw new ArgumentNullException(nameof(specification));
        var registry = providerRegistry ?? WorldOrderRuleCandidateProviderRegistry.Default;
        var specificationVersion = Fingerprint(specification, registry);

        if (workshop.HasOpaqueSolverRules)
            return new WorldOrderRuleCandidateProfile(
                workshop.Name, role, world.RuleSetVersion, world.WorkshopProfileVersion,
                specificationVersion, Array.Empty<WorldOrderRequirementCandidates>(),
                isUndetermined: true, reason: "工作台包含无法静态重建的规则（无法静态投影）");

        var rules = workshop.CreateRuleContext();
        if (rules.HasUnresolvableRequirements())
            return new WorldOrderRuleCandidateProfile(
                workshop.Name, role, world.RuleSetVersion, world.WorkshopProfileVersion,
                specificationVersion, Array.Empty<WorldOrderRequirementCandidates>(),
                isUndetermined: true, reason: "有效规则 Profile 含有当前求解器无法解释的贡献");

        var sourceShapes = new SortedSet<string>(workshop.Shapes, StringComparer.Ordinal);
        sourceShapes.UnionWith(rules.Capability.BaseMarks);
        sourceShapes.UnionWith(rules.Capability.AdditionalMarks);
        sourceShapes.UnionWith(rules.Stack.GrantedShapes());
        foreach (var requirement in specification.Requirements)
            sourceShapes.Add(requirement.Shape);

        var factory = new WorldOrderRuleCandidateFactory(
            rules, workshop.LiquidStagingCapacity, workshop.Capability.Ban);
        var preparatoryOperations = new List<WorldOrderRuleCandidate>();
        foreach (var sourceShape in sourceShapes)
        {
            var request = new WorldOrderRuleCandidateRequest(
                rules, new[] { sourceShape }, -1, sourceShape, null, true, factory);
            foreach (var provider in registry.Providers)
                preparatoryOperations.AddRange(provider.Enumerate(request));
        }

        var results = new List<WorldOrderRequirementCandidates>(specification.Requirements.Count);
        for (var requirementIndex = 0; requirementIndex < specification.Requirements.Count; requirementIndex++)
        {
            var requirement = specification.Requirements[requirementIndex];
            var candidates = new List<WorldOrderRuleCandidate>();
            foreach (var sourceShape in sourceShapes)
            {
                var request = new WorldOrderRuleCandidateRequest(
                    rules, new[] { sourceShape }, requirementIndex,
                    requirement.Shape, requirement.Liquid, false, factory);
                foreach (var provider in registry.Providers)
                    candidates.AddRange(provider.Enumerate(request));
            }
            results.Add(new WorldOrderRequirementCandidates(
                requirementIndex, requirement.Shape, requirement.Liquid, candidates));
        }

        return new WorldOrderRuleCandidateProfile(
            workshop.Name, role, world.RuleSetVersion, world.WorkshopProfileVersion,
            specificationVersion, results, preparatoryOperations);
    }
}

/// <summary>
/// 只按规则/工作台 Profile/订单规格缓存规则候选，不把资源余额纳入缓存键。
/// ResourceVersion 改变时同一 Profile 可直接复用；规则、工作台属性或订单规格改变时生成新 Profile。
/// </summary>
public sealed class WorldOrderRuleCandidateBook
{
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, WorldOrderRuleCandidateProfile> _profiles = new();
    private readonly Dictionary<SolutionCacheKey, WorldOrderSymbolicSolutionSet> _solutions = new();
    private const int MaxEntries = 512;
    private readonly WorldOrderRuleCandidateProviderRegistry _providerRegistry;

    public WorldOrderRuleCandidateBook(WorldOrderRuleCandidateProviderRegistry? providerRegistry = null)
    {
        _providerRegistry = providerRegistry ?? WorldOrderRuleCandidateProviderRegistry.Default;
    }

    public int CacheHits { get; private set; }
    public int CacheMisses { get; private set; }
    public int SolutionCacheHits { get; private set; }
    public int SolutionCacheMisses { get; private set; }

    public WorldOrderRuleCandidateProfile GetOrBuild(
        WorldSnapshot world,
        WorkshopSolveSnapshot workshop,
        string role,
        JarOrderSpecification specification)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (workshop == null) throw new ArgumentNullException(nameof(workshop));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("岗位名称不能为空", nameof(role));
        if (specification == null) throw new ArgumentNullException(nameof(specification));
        var specificationVersion = WorldOrderRuleCandidateCatalog.Fingerprint(specification, _providerRegistry);
        var key = new CacheKey(
            role,
            world.RuleSetVersion,
            world.WorkshopProfileVersion,
            specificationVersion,
            workshop.Name);

        lock (_gate)
        {
            if (_profiles.TryGetValue(key, out var cached))
            {
                CacheHits++;
                return cached;
            }

            CacheMisses++;
            var built = WorldOrderRuleCandidateCatalog.Build(world, workshop, role, specification, _providerRegistry);
            if (_profiles.Count >= MaxEntries) _profiles.Clear();
            _profiles[key] = built;
            return built;
        }
    }

    public WorldOrderSymbolicSolutionSet GetOrBuildSolutions(
        IReadOnlyList<WorldOrderRuleCandidateProfile> profiles,
        int maxSolutions = 256,
        int maxNodes = 100_000)
    {
        if (profiles == null || profiles.Count == 0)
            throw new ArgumentException("至少需要一个规则候选 Profile", nameof(profiles));
        if (maxSolutions <= 0) throw new ArgumentOutOfRangeException(nameof(maxSolutions));
        if (maxNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (profiles.Any(x => x == null))
            throw new ArgumentException("规则候选 Profile 不能为 null", nameof(profiles));
        var ordered = profiles.OrderBy(x => x.Role, StringComparer.Ordinal).ToArray();
        var first = ordered[0];
        if (ordered.Select(x => x.Role).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("工作台角色不能重复", nameof(profiles));
        if (ordered.Any(x =>
            x.RuleSetVersion != first.RuleSetVersion ||
            x.WorkshopProfileVersion != first.WorkshopProfileVersion ||
            x.SpecificationVersion != first.SpecificationVersion ||
            x.Requirements.Count != first.Requirements.Count))
            throw new ArgumentException("参与组合的规则候选 Profile 必须来自同一求解输入", nameof(profiles));
        var roleKey = string.Concat(ordered.Select(x => x.Role.Length + ":" + x.Role));
        var key = new SolutionCacheKey(
            roleKey,
            first.RuleSetVersion,
            first.WorkshopProfileVersion,
            first.SpecificationVersion,
            maxSolutions,
            maxNodes);

        lock (_gate)
        {
            if (_solutions.TryGetValue(key, out var cached))
            {
                SolutionCacheHits++;
                return cached;
            }

            SolutionCacheMisses++;
            var built = WorldOrderSymbolicSolutionBuilder.Build(ordered, maxSolutions, maxNodes);
            if (_solutions.Count >= MaxEntries) _solutions.Clear();
            _solutions[key] = built;
            return built;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _profiles.Clear();
            _solutions.Clear();
            CacheHits = 0;
            CacheMisses = 0;
            SolutionCacheHits = 0;
            SolutionCacheMisses = 0;
        }
    }

    private readonly record struct CacheKey(
        string Role,
        string RuleSetVersion,
        string WorkshopProfileVersion,
        string SpecificationVersion,
        string WorkshopName);

    private readonly record struct SolutionCacheKey(
        string Roles,
        string RuleSetVersion,
        string WorkshopProfileVersion,
        string SpecificationVersion,
        int MaxSolutions,
        int MaxNodes);
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// A resource that the Master must make available to the Slave at the one B-class
/// handoff boundary. This is a rule-derived condition, not a reservation or live balance.
/// </summary>
public sealed class WorldOrderBoundaryRequirement
{
    public string ProviderRole { get; }
    public string ConsumerRole { get; }
    public WorldOrderBoundaryResourceKind Kind { get; }
    public string? Liquid { get; }
    public int Amount { get; }
    public string? JarShape { get; }
    public string? JarLiquid { get; }
    public string SourceOperationId { get; }
    public int? SourceRequirementIndex { get; }

    private WorldOrderBoundaryRequirement(
        string providerRole,
        string consumerRole,
        WorldOrderBoundaryResourceKind kind,
        string? liquid,
        int amount,
        string? jarShape,
        string? jarLiquid,
        string sourceOperationId,
        int? sourceRequirementIndex)
    {
        if (string.IsNullOrWhiteSpace(providerRole))
            throw new ArgumentException("资源提供岗位不能为空", nameof(providerRole));
        if (string.IsNullOrWhiteSpace(consumerRole))
            throw new ArgumentException("资源使用岗位不能为空", nameof(consumerRole));
        if (providerRole == consumerRole)
            throw new ArgumentException("边界资源的提供岗位与使用岗位必须不同");
        if (string.IsNullOrWhiteSpace(sourceOperationId))
            throw new ArgumentException("边界来源操作不能为空", nameof(sourceOperationId));
        if (sourceRequirementIndex is < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceRequirementIndex));

        switch (kind)
        {
            case WorldOrderBoundaryResourceKind.FreeLiquid:
                if (string.IsNullOrWhiteSpace(liquid))
                    throw new ArgumentException("游离液体边界必须指定液体种类", nameof(liquid));
                if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
                if (jarShape != null || jarLiquid != null)
                    throw new ArgumentException("游离液体边界不能携带 Jar 状态");
                break;
            case WorldOrderBoundaryResourceKind.JarState:
                if (string.IsNullOrWhiteSpace(jarShape))
                    throw new ArgumentException("Jar 状态边界必须指定形状", nameof(jarShape));
                if (liquid != null || amount != 1)
                    throw new ArgumentException("Jar 状态边界不能携带游离液体数量");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ProviderRole = providerRole;
        ConsumerRole = consumerRole;
        Kind = kind;
        Liquid = liquid;
        Amount = amount;
        JarShape = jarShape;
        JarLiquid = jarLiquid;
        SourceOperationId = sourceOperationId;
        SourceRequirementIndex = sourceRequirementIndex;
    }

    public static WorldOrderBoundaryRequirement FreeLiquid(
        string providerRole,
        string consumerRole,
        string liquid,
        int amount,
        string sourceOperationId,
        int? sourceRequirementIndex = null) =>
        new(providerRole, consumerRole, WorldOrderBoundaryResourceKind.FreeLiquid,
            liquid, amount, null, null, sourceOperationId, sourceRequirementIndex);

    public static WorldOrderBoundaryRequirement JarState(
        string providerRole,
        string consumerRole,
        string shape,
        string? liquid,
        string sourceOperationId,
        int? sourceRequirementIndex = null) =>
        new(providerRole, consumerRole, WorldOrderBoundaryResourceKind.JarState,
            null, 1, shape, liquid, sourceOperationId, sourceRequirementIndex);
}

public enum WorldOrderBoundaryResourceKind
{
    FreeLiquid,
    JarState
}

/// <summary>
/// One conjunction: every boundary requirement in this group must be satisfied.
/// Different groups in an alternative set are OR alternatives and must not be merged.
/// </summary>
public sealed class WorldOrderResourceRequirementGroup
{
    public string GroupId { get; }
    public IReadOnlyList<WorldOrderBoundaryRequirement> Requirements { get; }

    public WorldOrderResourceRequirementGroup(
        string groupId,
        IEnumerable<WorldOrderBoundaryRequirement> requirements)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            throw new ArgumentException("资源组身份不能为空", nameof(groupId));
        if (requirements == null) throw new ArgumentNullException(nameof(requirements));

        var copied = requirements.ToArray();
        if (copied.Length == 0)
            throw new ArgumentException("资源组至少需要一个边界条件", nameof(requirements));
        if (copied.Any(x => x == null))
            throw new ArgumentException("资源组不能包含 null 条件", nameof(requirements));

        var rolePairs = copied.Select(x => (x.ProviderRole, x.ConsumerRole)).Distinct().ToArray();
        if (rolePairs.Length != 1)
            throw new ArgumentException("同一资源组必须具有一致的提供岗位和使用岗位", nameof(requirements));

        Requirements = Array.AsReadOnly(copied);
        GroupId = groupId;

        var freeLiquid = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in copied.Where(x => x.Kind == WorldOrderBoundaryResourceKind.FreeLiquid))
            freeLiquid[item.Liquid!] = checked(freeLiquid.GetValueOrDefault(item.Liquid!) + item.Amount);
        RequiredFreeLiquid = new ReadOnlyDictionary<string, int>(freeLiquid);

        var jarStates = copied.Where(x => x.Kind == WorldOrderBoundaryResourceKind.JarState)
            .GroupBy(x => new WorldOrderJarStateKey(x.JarShape!, x.JarLiquid))
            .ToDictionary(x => x.Key, x => x.Count());
        RequiredJarStates = new ReadOnlyDictionary<WorldOrderJarStateKey, int>(jarStates);
    }

    public string ProviderRole => Requirements[0].ProviderRole;
    public string ConsumerRole => Requirements[0].ConsumerRole;

    /// <summary>
    /// Aggregate same-kind free-liquid conditions within this AND group.
    /// The source requirements remain available for provenance, but a monitor must
    /// compare the sum against one live staging balance, not test each atom separately.
    /// </summary>
    public IReadOnlyDictionary<string, int> RequiredFreeLiquid { get; }

    /// <summary>Required Jar counts by exact shape/internal-liquid state.</summary>
    public IReadOnlyDictionary<WorldOrderJarStateKey, int> RequiredJarStates { get; }
}

public sealed record WorldOrderJarStateKey(string Shape, string? Liquid);

public enum WorldOrderResourceRequirementSetStatus
{
    Complete,
    Infeasible,
    Undetermined
}

/// <summary>
/// A set of alternative Master-to-Slave boundary groups derived from Slave-side
/// rule decomposition. Groups are OR alternatives; requirements inside each group
/// are AND conditions. Complete means this decomposition layer was exhausted,
/// not that any group is currently available or physically deliverable.
/// </summary>
public sealed class WorldOrderResourceRequirementAlternativeSet
{
    public WorldOrderResourceRequirementSetStatus Status { get; }
    public string RuleSetVersion { get; }
    public string WorkshopProfileVersion { get; }
    public string SpecificationVersion { get; }
    public string ProviderRole { get; }
    public string ConsumerRole { get; }
    public IReadOnlyCollection<string> ProviderBannedLiquids { get; }
    public IReadOnlyList<WorldOrderResourceRequirementGroup> Groups { get; }
    public string? Reason { get; }

    public bool IsComplete => Status == WorldOrderResourceRequirementSetStatus.Complete;
    public bool IsUndetermined => Status == WorldOrderResourceRequirementSetStatus.Undetermined;

    public WorldOrderResourceRequirementAlternativeSet(
        WorldOrderResourceRequirementSetStatus status,
        string ruleSetVersion,
        string workshopProfileVersion,
        string specificationVersion,
        string providerRole,
        string consumerRole,
        IEnumerable<WorldOrderResourceRequirementGroup> groups,
        string? reason = null,
        IEnumerable<string>? providerBannedLiquids = null)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (string.IsNullOrWhiteSpace(ruleSetVersion))
            throw new ArgumentException("规则版本不能为空", nameof(ruleSetVersion));
        if (string.IsNullOrWhiteSpace(workshopProfileVersion))
            throw new ArgumentException("工作台属性版本不能为空", nameof(workshopProfileVersion));
        if (string.IsNullOrWhiteSpace(specificationVersion))
            throw new ArgumentException("订单规格版本不能为空", nameof(specificationVersion));
        if (string.IsNullOrWhiteSpace(providerRole))
            throw new ArgumentException("边界提供岗位不能为空", nameof(providerRole));
        if (string.IsNullOrWhiteSpace(consumerRole))
            throw new ArgumentException("边界使用岗位不能为空", nameof(consumerRole));
        if (providerRole == consumerRole)
            throw new ArgumentException("边界提供岗位与使用岗位必须不同");
        if (groups == null) throw new ArgumentNullException(nameof(groups));

        var banned = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var liquid in providerBannedLiquids ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(liquid))
                throw new ArgumentException("主台禁传液体名称不能为空", nameof(providerBannedLiquids));
            banned.Add(liquid);
        }

        var copied = groups.ToArray();
        if (copied.Any(x => x == null))
            throw new ArgumentException("资源组集合不能包含 null", nameof(groups));
        if (copied.Select(x => x.GroupId).Distinct(StringComparer.Ordinal).Count() != copied.Length)
            throw new ArgumentException("资源组身份不能重复", nameof(groups));
        if (copied.Any(x => x.ProviderRole != providerRole || x.ConsumerRole != consumerRole))
            throw new ArgumentException("资源组必须匹配集合声明的提供/使用岗位", nameof(groups));
        if (copied.SelectMany(x => x.Requirements).Any(x =>
            (x.Kind == WorldOrderBoundaryResourceKind.FreeLiquid && banned.Contains(x.Liquid!)) ||
            (x.Kind == WorldOrderBoundaryResourceKind.JarState && x.JarLiquid != null && banned.Contains(x.JarLiquid))))
            throw new ArgumentException("主台 ban 的液体不能出现在任何可交接资源组中", nameof(groups));
        if (status == WorldOrderResourceRequirementSetStatus.Complete && copied.Length == 0)
            throw new ArgumentException("完整的可行替代集合至少包含一个资源组；无解应使用 Infeasible", nameof(groups));
        if (status == WorldOrderResourceRequirementSetStatus.Infeasible && copied.Length != 0)
            throw new ArgumentException("Infeasible 结果不能携带可行资源组", nameof(groups));

        Status = status;
        RuleSetVersion = ruleSetVersion;
        WorkshopProfileVersion = workshopProfileVersion;
        SpecificationVersion = specificationVersion;
        ProviderRole = providerRole;
        ConsumerRole = consumerRole;
        ProviderBannedLiquids = Array.AsReadOnly(banned.ToArray());
        Groups = Array.AsReadOnly(copied);
        Reason = reason;
    }
}

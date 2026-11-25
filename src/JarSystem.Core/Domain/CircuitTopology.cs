using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JarSystem.Core.Domain;

/// <summary>Direction of a typed resource boundary exposed by an Area.</summary>
public enum ResourcePortDirection
{
    Input,
    Output
}

/// <summary>
/// Immutable structural identity of an Area. OwnerIndividualId is the exact owner instance,
/// not a slot role or display name. ParentAreaId models nested functional areas without
/// changing the top-level board-area taxonomy.
/// </summary>
public sealed record CircuitAreaDefinition
{
    public string AreaId { get; }
    public string OwnerIndividualId { get; }
    public string? ParentAreaId { get; }
    public string AreaKindId { get; }
    public bool HoldsResources { get; }

    public CircuitAreaDefinition(
        string areaId,
        string ownerIndividualId,
        string areaKindId,
        bool holdsResources,
        string? parentAreaId = null)
    {
        AreaId = RequireIdentity(areaId, nameof(areaId));
        OwnerIndividualId = RequireIdentity(ownerIndividualId, nameof(ownerIndividualId));
        AreaKindId = RequireIdentity(areaKindId, nameof(areaKindId));
        ParentAreaId = parentAreaId is null ? null : RequireIdentity(parentAreaId, nameof(parentAreaId));
        if (string.Equals(AreaId, ParentAreaId, StringComparison.Ordinal))
            throw new ArgumentException("An Area cannot be its own parent.", nameof(parentAreaId));
        HoldsResources = holdsResources;
    }

    internal static string RequireIdentity(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Structural identities cannot be blank.", parameterName)
            : value.Trim();
}

/// <summary>
/// Immutable typed boundary. An empty identity set means unconstrained identity within ResourceKind;
/// a non-empty set is an explicit allow-list for an Input or produced identity set for an Output.
/// </summary>
public sealed class CircuitPortDefinition
{
    private readonly IReadOnlyList<string> _resourceIdentities;

    public string PortId { get; }
    public string AreaId { get; }
    public ResourcePortDirection Direction { get; }
    public RuleResourceKind ResourceKind { get; }
    public IReadOnlyList<string> ResourceIdentities => _resourceIdentities;
    public string PolicyId { get; }
    public int PolicyVersion { get; }

    public CircuitPortDefinition(
        string portId,
        string areaId,
        ResourcePortDirection direction,
        RuleResourceKind resourceKind,
        IEnumerable<string>? resourceIdentities,
        string policyId,
        int policyVersion)
    {
        PortId = CircuitAreaDefinition.RequireIdentity(portId, nameof(portId));
        AreaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (!Enum.IsDefined(resourceKind))
            throw new ArgumentOutOfRangeException(nameof(resourceKind));
        Direction = direction;
        ResourceKind = resourceKind;
        PolicyId = CircuitAreaDefinition.RequireIdentity(policyId, nameof(policyId));
        if (policyVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(policyVersion));
        PolicyVersion = policyVersion;

        var identities = (resourceIdentities ?? Array.Empty<string>())
            .Select(value => CircuitAreaDefinition.RequireIdentity(value, nameof(resourceIdentities)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        _resourceIdentities = Array.AsReadOnly(identities);
    }
}

/// <summary>
/// Explicit directed connection between one Output port and one Input port.
/// A connection is topology only; it does not imply that a resource has moved.
/// </summary>
public sealed record CircuitConnectionDefinition
{
    public string ConnectionId { get; }
    public string SourcePortId { get; }
    public string TargetPortId { get; }

    public CircuitConnectionDefinition(string connectionId, string sourcePortId, string targetPortId)
    {
        ConnectionId = CircuitAreaDefinition.RequireIdentity(connectionId, nameof(connectionId));
        SourcePortId = CircuitAreaDefinition.RequireIdentity(sourcePortId, nameof(sourcePortId));
        TargetPortId = CircuitAreaDefinition.RequireIdentity(targetPortId, nameof(targetPortId));
        if (string.Equals(SourcePortId, TargetPortId, StringComparison.Ordinal))
            throw new ArgumentException("A connection must join two distinct ports.");
    }
}

/// <summary>
/// Frozen, validated Area/Port/Connection topology. This is deliberately metadata-only:
/// authoritative balances remain in existing Workshop, Ship, Warehouse and Jar accounts.
/// </summary>
public sealed class CircuitTopology
{
    private readonly IReadOnlyDictionary<string, CircuitAreaDefinition> _areas;
    private readonly IReadOnlyDictionary<string, CircuitPortDefinition> _ports;
    private readonly IReadOnlyList<CircuitConnectionDefinition> _connections;

    public IReadOnlyDictionary<string, CircuitAreaDefinition> Areas => _areas;
    public IReadOnlyDictionary<string, CircuitPortDefinition> Ports => _ports;
    public IReadOnlyList<CircuitConnectionDefinition> Connections => _connections;

    public CircuitTopology(
        IEnumerable<CircuitAreaDefinition> areas,
        IEnumerable<CircuitPortDefinition> ports,
        IEnumerable<CircuitConnectionDefinition> connections)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(connections);

        var areaMap = ToUniqueMap(areas, item => item.AreaId, "Area");
        var portMap = ToUniqueMap(ports, item => item.PortId, "Port");
        var connectionList = connections.ToArray();
        if (connectionList.Select(item => item.ConnectionId).Distinct(StringComparer.Ordinal).Count() != connectionList.Length)
            throw new ArgumentException("Connection identities must be unique.", nameof(connections));

        ValidateAreaHierarchy(areaMap);
        foreach (var port in portMap.Values)
        {
            if (!areaMap.ContainsKey(port.AreaId))
                throw new ArgumentException("Port references an unknown Area: " + port.PortId, nameof(ports));
        }

        foreach (var connection in connectionList)
            ValidateConnection(connection, portMap);

        _areas = new ReadOnlyDictionary<string, CircuitAreaDefinition>(areaMap);
        _ports = new ReadOnlyDictionary<string, CircuitPortDefinition>(portMap);
        _connections = Array.AsReadOnly(connectionList);
    }

    private static Dictionary<string, T> ToUniqueMap<T>(
        IEnumerable<T> items,
        Func<T, string> keySelector,
        string kind)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!map.TryAdd(keySelector(item), item))
                throw new ArgumentException(kind + " identities must be unique.");
        }
        return map;
    }

    private static void ValidateAreaHierarchy(IReadOnlyDictionary<string, CircuitAreaDefinition> areas)
    {
        foreach (var area in areas.Values)
        {
            if (area.ParentAreaId is null)
                continue;
            if (!areas.TryGetValue(area.ParentAreaId, out var parent))
                throw new ArgumentException("Area references an unknown parent: " + area.AreaId);
            if (!string.Equals(area.OwnerIndividualId, parent.OwnerIndividualId, StringComparison.Ordinal))
                throw new ArgumentException("Nested Areas must share the same owning Individual: " + area.AreaId);

            var visited = new HashSet<string>(StringComparer.Ordinal) { area.AreaId };
            var cursor = parent;
            while (true)
            {
                if (!visited.Add(cursor.AreaId))
                    throw new ArgumentException("Area parent hierarchy cannot contain cycles: " + area.AreaId);
                if (cursor.ParentAreaId is null)
                    break;
                if (!areas.TryGetValue(cursor.ParentAreaId, out var ancestor))
                    throw new ArgumentException("Area references an unknown ancestor: " + area.AreaId);
                cursor = ancestor;
            }
        }
    }

    private static void ValidateConnection(
        CircuitConnectionDefinition connection,
        IReadOnlyDictionary<string, CircuitPortDefinition> ports)
    {
        if (!ports.TryGetValue(connection.SourcePortId, out var source))
            throw new ArgumentException("Connection references an unknown source port: " + connection.ConnectionId);
        if (!ports.TryGetValue(connection.TargetPortId, out var target))
            throw new ArgumentException("Connection references an unknown target port: " + connection.ConnectionId);
        if (source.Direction != ResourcePortDirection.Output || target.Direction != ResourcePortDirection.Input)
            throw new ArgumentException("Connection direction must be Output -> Input: " + connection.ConnectionId);
        if (source.ResourceKind != target.ResourceKind)
            throw new ArgumentException("Connected ports must expose the same resource kind: " + connection.ConnectionId);

        if (source.ResourceIdentities.Count > 0 && target.ResourceIdentities.Count > 0 &&
            !source.ResourceIdentities.Intersect(target.ResourceIdentities, StringComparer.Ordinal).Any())
            throw new ArgumentException("Connected port identity constraints do not overlap: " + connection.ConnectionId);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using JarSystem.Core.Domain;

namespace JarSystem.Core.World;

/// <summary>
/// Immutable, host-supplied identity directory for the resource-owning individuals in one World.
/// Keys are exact local instance IDs, never display names, definitions, list positions or role labels.
/// </summary>
public sealed class WorldV2OwnerIdentityMap
{
    public WorldInstanceId WorldInstanceId { get; }
    public OwnerIdentity Ship { get; }
    public OwnerIdentity CircuitBoard { get; }
    public OwnerIdentity ProductionArea { get; }
    public OwnerIdentity Warehouse { get; }
    public IReadOnlyDictionary<string, OwnerIdentity> Producers { get; }
    public IReadOnlyDictionary<string, OwnerIdentity> Workshops { get; }

    public WorldV2OwnerIdentityMap(
        WorldInstanceId worldInstanceId,
        string shipOwnedInstanceId,
        string circuitBoardInstanceId,
        string productionAreaOwnedInstanceId,
        string warehouseOwnedInstanceId,
        IEnumerable<string> producerInstanceIds,
        IEnumerable<string> workshopOwnedInstanceIds)
    {
        if (!worldInstanceId.IsValid)
            throw new ArgumentException("Owner identity map requires a valid WorldInstanceId.", nameof(worldInstanceId));
        ArgumentNullException.ThrowIfNull(producerInstanceIds);
        ArgumentNullException.ThrowIfNull(workshopOwnedInstanceIds);

        WorldInstanceId = worldInstanceId;
        Ship = new OwnerIdentity(worldInstanceId, ResourceOwnerKind.Ship, shipOwnedInstanceId);
        CircuitBoard = new OwnerIdentity(worldInstanceId, ResourceOwnerKind.CircuitBoard, circuitBoardInstanceId);
        ProductionArea = new OwnerIdentity(worldInstanceId, ResourceOwnerKind.ProductionArea, productionAreaOwnedInstanceId);
        Warehouse = new OwnerIdentity(worldInstanceId, ResourceOwnerKind.Warehouse, warehouseOwnedInstanceId);
        Producers = BuildOwners(worldInstanceId, ResourceOwnerKind.Producer, producerInstanceIds, nameof(producerInstanceIds));
        Workshops = BuildOwners(worldInstanceId, ResourceOwnerKind.Workshop, workshopOwnedInstanceIds, nameof(workshopOwnedInstanceIds));
    }

    public void ValidateForWorld(WorldInstanceId worldInstanceId)
    {
        if (!worldInstanceId.IsValid || WorldInstanceId != worldInstanceId)
            throw new ArgumentException("Owner identity map belongs to a different WorldInstanceId.", nameof(worldInstanceId));

        ValidateOwner(Ship, ResourceOwnerKind.Ship, WorldInstanceId);
        ValidateOwner(CircuitBoard, ResourceOwnerKind.CircuitBoard, WorldInstanceId);
        ValidateOwner(ProductionArea, ResourceOwnerKind.ProductionArea, WorldInstanceId);
        ValidateOwner(Warehouse, ResourceOwnerKind.Warehouse, WorldInstanceId);
        foreach (var owner in Producers.Values) ValidateOwner(owner, ResourceOwnerKind.Producer, WorldInstanceId);
        foreach (var owner in Workshops.Values) ValidateOwner(owner, ResourceOwnerKind.Workshop, WorldInstanceId);
    }

    public bool IsEquivalentTo(WorldV2OwnerIdentityMap? other) =>
        other != null &&
        WorldInstanceId == other.WorldInstanceId &&
        Ship == other.Ship &&
        CircuitBoard == other.CircuitBoard &&
        ProductionArea == other.ProductionArea &&
        Warehouse == other.Warehouse &&
        DictionaryEquals(Producers, other.Producers) &&
        DictionaryEquals(Workshops, other.Workshops);

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, OwnerIdentity> left,
        IReadOnlyDictionary<string, OwnerIdentity> right) =>
        left.Count == right.Count &&
        left.All(item => right.TryGetValue(item.Key, out var owner) && owner == item.Value);

    public OwnerIdentity GetProducer(string producerInstanceId) =>
        Producers.TryGetValue(producerInstanceId, out var owner)
            ? owner
            : throw new KeyNotFoundException("No Producer OwnerIdentity is registered for the exact ProducerInstanceId.");

    public OwnerIdentity GetWorkshop(string ownedInstanceId) =>
        Workshops.TryGetValue(ownedInstanceId, out var owner)
            ? owner
            : throw new KeyNotFoundException("No Workshop OwnerIdentity is registered for the exact OwnedInstanceId.");

    private static IReadOnlyDictionary<string, OwnerIdentity> BuildOwners(
        WorldInstanceId worldInstanceId,
        ResourceOwnerKind kind,
        IEnumerable<string> localIds,
        string parameterName)
    {
        var owners = new Dictionary<string, OwnerIdentity>(StringComparer.Ordinal);
        foreach (var localId in localIds)
        {
            var owner = new OwnerIdentity(worldInstanceId, kind, localId);
            if (!owners.TryAdd(localId, owner))
                throw new ArgumentException("Owner identity map contains a duplicate local instance ID: " + localId, parameterName);
        }
        return new ReadOnlyDictionary<string, OwnerIdentity>(owners);
    }

    private static void ValidateOwner(
        OwnerIdentity owner, ResourceOwnerKind expectedKind, WorldInstanceId expectedWorldInstanceId)
    {
        if (!owner.IsValid || owner.WorldInstanceId != expectedWorldInstanceId || owner.OwnerKind != expectedKind)
            throw new InvalidOperationException("Owner identity map contains an invalid or cross-World owner.");
    }
}

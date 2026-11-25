using System;
using System.Collections.Generic;
using System.Linq;

namespace JarSystem.Core.World;

/// <summary>Wire-only local owner ID mapping; WorldInstanceId is carried once at the checkpoint root.</summary>
public sealed class WorldV2OwnerIdentityMapWireDto
{
    public string? ShipOwnedInstanceId { get; set; }
    public string? CircuitBoardInstanceId { get; set; }
    public string? ProductionAreaOwnedInstanceId { get; set; }
    public string? WarehouseOwnedInstanceId { get; set; }
    public List<string> ProducerInstanceIds { get; set; } = new();
    public List<string> WorkshopOwnedInstanceIds { get; set; } = new();

    public static WorldV2OwnerIdentityMapWireDto FromMap(WorldV2OwnerIdentityMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new WorldV2OwnerIdentityMapWireDto
        {
            ShipOwnedInstanceId = map.Ship.LocalOwnerId,
            CircuitBoardInstanceId = map.CircuitBoard.LocalOwnerId,
            ProductionAreaOwnedInstanceId = map.ProductionArea.LocalOwnerId,
            WarehouseOwnedInstanceId = map.Warehouse.LocalOwnerId,
            ProducerInstanceIds = map.Producers.Keys.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            WorkshopOwnedInstanceIds = map.Workshops.Keys.OrderBy(value => value, StringComparer.Ordinal).ToList()
        };
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ShipOwnedInstanceId) ||
            string.IsNullOrWhiteSpace(CircuitBoardInstanceId) ||
            string.IsNullOrWhiteSpace(ProductionAreaOwnedInstanceId) ||
            string.IsNullOrWhiteSpace(WarehouseOwnedInstanceId) ||
            ProducerInstanceIds == null || WorkshopOwnedInstanceIds == null ||
            ProducerInstanceIds.Any(string.IsNullOrWhiteSpace) ||
            WorkshopOwnedInstanceIds.Any(string.IsNullOrWhiteSpace) ||
            ProducerInstanceIds.Distinct(StringComparer.Ordinal).Count() != ProducerInstanceIds.Count ||
            WorkshopOwnedInstanceIds.Distinct(StringComparer.Ordinal).Count() != WorkshopOwnedInstanceIds.Count)
            throw new InvalidOperationException("检查点 OwnerIdentity 映射字段缺失、重复或无效。");
    }

    public WorldV2OwnerIdentityMap ToMap(JarSystem.Core.Domain.WorldInstanceId worldInstanceId)
    {
        Validate();
        return new WorldV2OwnerIdentityMap(
            worldInstanceId,
            ShipOwnedInstanceId!,
            CircuitBoardInstanceId!,
            ProductionAreaOwnedInstanceId!,
            WarehouseOwnedInstanceId!,
            ProducerInstanceIds,
            WorkshopOwnedInstanceIds);
    }
}

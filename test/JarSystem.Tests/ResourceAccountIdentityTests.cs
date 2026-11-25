using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

public sealed class ResourceAccountIdentityTests
{
    [Fact]
    public void AccountIdentityV2IsDeterministicForTheSameWorldOwnerAndAccountKind()
    {
        var owner = new OwnerIdentity(
            new WorldInstanceId("world-save-17"),
            ResourceOwnerKind.Warehouse,
            "warehouse-instance-3");

        var first = new AccountIdentity(owner, "warehouse.jar-storage");
        var second = new AccountIdentity(owner, "warehouse.jar-storage");

        Assert.Equal(first.ToCanonicalString(), second.ToCanonicalString());
        Assert.StartsWith("account:v2:", first.ToCanonicalString());
    }

    [Fact]
    public void AccountIdentityV2SeparatesWorldsEvenWhenLocalOwnerTupleMatches()
    {
        var first = new AccountIdentity(
            new OwnerIdentity(new WorldInstanceId("world-a"), ResourceOwnerKind.Ship, "ship-1"),
            "ship.liquid-inventory");
        var second = new AccountIdentity(
            new OwnerIdentity(new WorldInstanceId("world-b"), ResourceOwnerKind.Ship, "ship-1"),
            "ship.liquid-inventory");

        Assert.NotEqual(first.ToCanonicalString(), second.ToCanonicalString());
    }

    [Fact]
    public void AccountIdentityV2SeparatesOwnerKindLocalOwnerAndAccountKind()
    {
        var world = new WorldInstanceId("world-a");
        var ship = new OwnerIdentity(world, ResourceOwnerKind.Ship, "instance-1");
        var warehouse = new OwnerIdentity(world, ResourceOwnerKind.Warehouse, "instance-1");

        var shipAccount = new AccountIdentity(ship, "liquid");
        var otherOwnerKind = new AccountIdentity(warehouse, "liquid");
        var otherAccountKind = new AccountIdentity(ship, "jar");

        Assert.NotEqual(shipAccount.ToCanonicalString(), otherOwnerKind.ToCanonicalString());
        Assert.NotEqual(shipAccount.ToCanonicalString(), otherAccountKind.ToCanonicalString());
    }

    [Fact]
    public void AccountIdentityV2UsesUtf8ByteLengthPrefixesForUnicodeAndSeparators()
    {
        var identity = new AccountIdentity(
            new OwnerIdentity(new WorldInstanceId("世界:α"), ResourceOwnerKind.Producer, "炉:1"),
            "生产:输出");

        var canonical = identity.ToCanonicalString();

        Assert.StartsWith("account:v2:", canonical);
        Assert.Contains("5:炉:1", canonical);
        Assert.Contains("13:生产:输出", canonical);
        Assert.Equal(identity, AccountIdentity.ParseV2(canonical));
    }

    [Theory]
    [InlineData("account:v2:01:w4:ship1:s1:a")]
    [InlineData("account:v2:1:w99:ship1:s1:a")]
    [InlineData("account:v2:1:w4:ship1:s1:a:trailing")]
    [InlineData("account:v1:4:ship1:s")]
    public void AccountIdentityV2ParserRejectsMalformedOrLegacyValues(string value)
    {
        Assert.Throws<System.FormatException>(() => AccountIdentity.ParseV2(value));
    }

    [Fact]
    public void OwnerAndAccountIdentityRejectInvalidDefaultAndMalformedValues()
    {
        Assert.False(default(OwnerIdentity).IsValid);
        Assert.False(default(AccountIdentity).IsValid);

        Assert.Throws<System.ArgumentException>(() =>
            new OwnerIdentity(default, ResourceOwnerKind.Ship, "ship-1"));
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            new OwnerIdentity(new WorldInstanceId("world-a"), (ResourceOwnerKind)999, "ship-1"));
        Assert.Throws<System.ArgumentException>(() =>
            new OwnerIdentity(new WorldInstanceId("world-a"), ResourceOwnerKind.Ship, " ship-1"));
        Assert.Throws<System.ArgumentException>(() =>
            new AccountIdentity(
                new OwnerIdentity(new WorldInstanceId("world-a"), ResourceOwnerKind.Ship, "ship-1"),
                " "));
    }

    [Fact]
    public void AccountIdentityV2DoesNotSilentlyReinterpretLegacyV1AccountIds()
    {
        var legacy = new RuleResourceAccount("ship.liquid-inventory", "ship-1");
        var owner = new OwnerIdentity(
            new WorldInstanceId("world-a"), ResourceOwnerKind.Ship, "ship-1");
        var currentIdentity = new AccountIdentity(owner, "ship.liquid-inventory");
        var currentAccount = new RuleResourceAccount(owner, "ship.liquid-inventory");

        Assert.StartsWith("account:v1:", legacy.AccountId);
        Assert.StartsWith("account:v2:", currentIdentity.ToCanonicalString());
        Assert.Equal(currentIdentity.ToCanonicalString(), currentAccount.AccountId);
        Assert.NotEqual(legacy.AccountId, currentAccount.AccountId);
    }

    [Fact]
    public void ResourcePortAdaptersCanUseWorldScopedOwnersWithoutChangingLegacyConstructors()
    {
        var worldA = new WorldInstanceId("world-a");
        var worldB = new WorldInstanceId("world-b");
        var shipA = new OwnerIdentity(worldA, ResourceOwnerKind.Ship, "ship-owned-1");
        var shipB = new OwnerIdentity(worldB, ResourceOwnerKind.Ship, "ship-owned-1");
        var shipAdapterA = new ShipLiquidInventoryPortAdapter(
            new ShipLiquidInventory(20), shipA, "area.ship");
        var shipAdapterB = new ShipLiquidInventoryPortAdapter(
            new ShipLiquidInventory(20), shipB, "area.ship");
        Assert.StartsWith("account:v2:", shipAdapterA.Account.AccountId);
        Assert.NotEqual(shipAdapterA.Account.AccountId, shipAdapterB.Account.AccountId);

        var workshop = new Workshop(
            "Workshop display",
            new List<string> { "圆罐" },
            new List<string>(),
            new List<string>(),
            10,
            10,
            ownedInstanceId: "workshop-owned-1");
        var workshopOwner = new OwnerIdentity(
            worldA, ResourceOwnerKind.Workshop, "workshop-owned-1");
        var freeLiquid = new WorkshopFreeLiquidPortAdapter(
            workshop, "area.workshop", workshopOwner);
        var jarStorage = new WorkshopJarStoragePortAdapter(
            workshop, "area.workshop", workshopOwner);
        Assert.StartsWith("account:v2:", freeLiquid.Account.AccountId);
        Assert.StartsWith("account:v2:", jarStorage.Account.AccountId);

        var stagingA = new WorkshopOrderLiquidPortAdapter(
            new WorkshopLiquidStaging(20), workshopOwner, "area.workshop", 41);
        var stagingB = new WorkshopOrderLiquidPortAdapter(
            new WorkshopLiquidStaging(20), workshopOwner, "area.workshop", 42);
        Assert.NotEqual(stagingA.Account.AccountId, stagingB.Account.AccountId);

        var prefill = new WorkshopPrefillLiquidPortAdapter(
            new WorkshopLiquidStaging(20), workshopOwner, "area.workshop");
        Assert.StartsWith("account:v2:", prefill.Account.AccountId);

        var warehouseOwner = new OwnerIdentity(
            worldA, ResourceOwnerKind.Warehouse, "warehouse-owned-1");
        var warehouse = new Warehouse(ownerIdentity: warehouseOwner);
        var warehouseJars = new WarehouseJarStoragePortAdapter(
            warehouse, warehouseOwner, "area.production");
        var warehouseLiquids = new WarehouseLiquidStoragePortAdapter(
            warehouse, warehouseOwner, "area.production");
        Assert.StartsWith("account:v2:", warehouseJars.Account.AccountId);
        Assert.StartsWith("account:v2:", warehouseLiquids.Account.AccountId);

        Assert.Throws<System.ArgumentException>(() =>
            new ShipLiquidInventoryPortAdapter(
                new ShipLiquidInventory(20), workshopOwner, "area.ship"));
        Assert.Throws<System.ArgumentException>(() =>
            new WorkshopFreeLiquidPortAdapter(
                workshop, "area.workshop",
                new OwnerIdentity(worldA, ResourceOwnerKind.Workshop, "different-workshop")));
    }


    [Fact]
    public void WorldScopedAccountTransferCannotCrossWorldOrMixLegacyEndpoint()
    {
        var source = new RuleResourceAccount(
            new OwnerIdentity(new WorldInstanceId("world-a"), ResourceOwnerKind.Ship, "ship-1"),
            "ship.liquid-inventory");
        var sameWorldTarget = new RuleResourceAccount(
            new OwnerIdentity(new WorldInstanceId("world-a"), ResourceOwnerKind.Workshop, "workshop-1"),
            "workshop.free-liquid-staging");
        var otherWorldTarget = new RuleResourceAccount(
            new OwnerIdentity(new WorldInstanceId("world-b"), ResourceOwnerKind.Workshop, "workshop-1"),
            "workshop.free-liquid-staging");
        var legacyTarget = new RuleResourceAccount(
            "workshop.free-liquid-staging", "workshop-1");

        RuleResourceAccount.ValidateTransferScope(source, sameWorldTarget);
        Assert.Throws<System.ArgumentException>(() =>
            RuleResourceAccount.ValidateTransferScope(source, otherWorldTarget));
        Assert.Throws<System.ArgumentException>(() =>
            RuleResourceAccount.ValidateTransferScope(source, legacyTarget));
    }


    [Fact]
    public void WorldOwnerIdentityMapRequiresUniqueExactLocalInstanceIdsAndOneWorld()
    {
        var worldId = new WorldInstanceId("world-map");
        var map = new WorldV2OwnerIdentityMap(
            worldId, "ship-1", "board-1", "production-1", "warehouse-1",
            new[] { "producer-1" }, new[] { "workshop-1", "workshop-2" });

        map.ValidateForWorld(worldId);
        Assert.Equal(ResourceOwnerKind.Producer, map.GetProducer("producer-1").OwnerKind);
        Assert.Equal(ResourceOwnerKind.Workshop, map.GetWorkshop("workshop-2").OwnerKind);
        Assert.Throws<System.ArgumentException>(() =>
            map.ValidateForWorld(new WorldInstanceId("another-world")));
        Assert.Throws<System.ArgumentException>(() =>
            new WorldV2OwnerIdentityMap(
                worldId, "ship-1", "board-1", "production-1", "warehouse-1",
                new[] { "producer-1", "producer-1" }, new[] { "workshop-1" }));
    }

}

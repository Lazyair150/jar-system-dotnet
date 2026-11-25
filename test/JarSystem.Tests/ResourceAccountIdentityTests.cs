using JarSystem.Core.Domain;
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
}

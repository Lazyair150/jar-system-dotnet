using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class ResourcePortAdapterTests
{
    private static CircuitPortDefinition Port(
        string id, string area, ResourcePortDirection direction, string policy,
        IEnumerable<string>? identities = null, int? maxTransfer = null) =>
        new(id, area, direction, RuleResourceKind.Liquid, identities, policy, 1, maxTransfer);

    [Fact]
    public void WorkshopFreeLiquidAdapterUsesOnlyTheFreeLiquidAccountAndBan()
    {
        var workshop = new Workshop("master", ban: new List<string> { "蜜" },
            maxLiquids: 3, ownedInstanceId: "workshop.master.01");
        var adapter = new WorkshopFreeLiquidPortAdapter(workshop, "area.workshop");
        var input = Port("workshop.liquid.in", "area.workshop", ResourcePortDirection.Input,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId);
        var output = Port("workshop.liquid.out", "area.workshop", ResourcePortDirection.Output,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId);

        var allowed = adapter.Preflight(input, "酒", 2);
        var banned = adapter.Preflight(input, "蜜", 1);
        var unavailable = adapter.Preflight(output, "酒", 1);

        Assert.Equal(RuleDecisionKind.Allowed, allowed.Decision);
        Assert.Equal("allowed", allowed.ReasonCode);
        Assert.Equal(RuleDecisionKind.Rejected, banned.Decision);
        Assert.Equal("banned", banned.ReasonCode);
        Assert.Equal(RuleDecisionKind.Rejected, unavailable.Decision);
        Assert.Equal("insufficient", unavailable.ReasonCode);
        Assert.Equal("workshop.free-liquid-staging", allowed.Account.AccountKind);
        Assert.Equal("workshop.master.01", allowed.Account.OwnerId);
    }

    [Fact]
    public void WorkshopFreeLiquidAdapterVersionChangesOnlyWithAccountRelevantState()
    {
        var workshop = new Workshop("master", maxLiquids: 4, ownedInstanceId: "workshop.master.02");
        var adapter = new WorkshopFreeLiquidPortAdapter(workshop, "area.workshop");
        var input = Port("workshop.liquid.in", "area.workshop", ResourcePortDirection.Input,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId);

        var before = adapter.Preflight(input, "酒", 1).ObservedVersionToken;
        Assert.True(workshop.AddLiquid("酒", 1));
        var after = adapter.Preflight(input, "酒", 1).ObservedVersionToken;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void OrderStagingAdapterKeepsOrderAccountsAndSharedCapacityDistinct()
    {
        var staging = new WorkshopLiquidStaging(3, new[] { "蜜" });
        staging.Receive(101, "酒", 2);
        var adapter = new WorkshopOrderLiquidPortAdapter(staging, "workshop.01", "area.workshop", 101);
        var input = Port("order.liquid.in", "area.workshop", ResourcePortDirection.Input,
            WorkshopOrderLiquidPortAdapter.SupportedPolicyId);
        var output = Port("order.liquid.out", "area.workshop", ResourcePortDirection.Output,
            WorkshopOrderLiquidPortAdapter.SupportedPolicyId);

        var full = adapter.Preflight(input, "盐", 2);
        var available = adapter.Preflight(output, "酒", 1);
        var banned = adapter.Preflight(output, "蜜", 1);

        Assert.Equal(RuleDecisionKind.Rejected, full.Decision);
        Assert.Equal("over-capacity", full.ReasonCode);
        Assert.Equal(RuleDecisionKind.Allowed, available.Decision);
        Assert.Equal(RuleDecisionKind.Rejected, banned.Decision);
        Assert.Equal("banned", banned.ReasonCode);
        Assert.Equal("workshop.order-liquid-staging", available.Account.AccountKind);
        Assert.Equal("workshop.01:order:101", available.Account.OwnerId);
        Assert.Equal(0, staging.GetAmount(102, "酒"));
    }

    [Fact]
    public void ShipInventoryAdapterUsesAggregateCapacityAndExactOwner()
    {
        var ship = new ShipLiquidInventory(3, new Dictionary<string, int> { ["酒"] = 2 });
        var adapter = new ShipLiquidInventoryPortAdapter(ship, "ship.01", "area.ship.liquids");
        var input = Port("ship.liquid.in", "area.ship.liquids", ResourcePortDirection.Input,
            ShipLiquidInventoryPortAdapter.SupportedPolicyId);
        var output = Port("ship.liquid.out", "area.ship.liquids", ResourcePortDirection.Output,
            ShipLiquidInventoryPortAdapter.SupportedPolicyId);

        Assert.Equal(RuleDecisionKind.Allowed, adapter.Preflight(input, "蜜", 1).Decision);
        Assert.Equal(RuleDecisionKind.Rejected, adapter.Preflight(input, "蜜", 2).Decision);
        Assert.Equal(RuleDecisionKind.Allowed, adapter.Preflight(output, "酒", 2).Decision);
        Assert.Equal(RuleDecisionKind.Rejected, adapter.Preflight(output, "蜜", 1).Decision);
        Assert.Equal("ship.01", adapter.Account.OwnerId);
    }

    [Fact]
    public void PortIdentityLimitAndUnsupportedThroughputFailClosed()
    {
        var workshop = new Workshop("master", ownedInstanceId: "workshop.03");
        var adapter = new WorkshopFreeLiquidPortAdapter(workshop, "area.workshop");
        var identityLimited = Port("limited.in", "area.workshop", ResourcePortDirection.Input,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId, new[] { "酒" }, maxTransfer: 2);
        var throughputLimited = new CircuitPortDefinition("throughput.in", "area.workshop",
            ResourcePortDirection.Input, RuleResourceKind.Liquid, null,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId, 1,
            maxAmountPerLogicalTimeMilli: 1000);

        Assert.Equal("port.resource-identity.mismatch",
            adapter.Preflight(identityLimited, "蜜", 1).ReasonCode);
        Assert.Equal("port.transfer-limit.exceeded",
            adapter.Preflight(identityLimited, "酒", 3).ReasonCode);
        var throughput = adapter.Preflight(throughputLimited, "酒", 1);
        Assert.Equal(RuleDecisionKind.Undetermined, throughput.Decision);
        Assert.Equal("port.throughput.scheduler-required", throughput.ReasonCode);
    }

    [Fact]
    public void WrongAreaOrPolicyNeverFallsBackToAnAllowedDecision()
    {
        var workshop = new Workshop("master", ownedInstanceId: "workshop.04");
        var adapter = new WorkshopFreeLiquidPortAdapter(workshop, "area.workshop");
        var wrongArea = Port("wrong.area", "area.ship", ResourcePortDirection.Input,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId);
        var wrongPolicy = Port("wrong.policy", "area.workshop", ResourcePortDirection.Input, "unknown.policy");

        Assert.Equal(RuleDecisionKind.Undetermined, adapter.Preflight(wrongArea, "酒", 1).Decision);
        Assert.Equal("port.area.mismatch", adapter.Preflight(wrongArea, "酒", 1).ReasonCode);
        Assert.Equal(RuleDecisionKind.Undetermined, adapter.Preflight(wrongPolicy, "酒", 1).Decision);
        Assert.Equal("port.policy.unsupported", adapter.Preflight(wrongPolicy, "酒", 1).ReasonCode);
    }

    [Fact]
    public void PreflightIsReadOnlyAcrossAllAdapters()
    {
        var workshop = new Workshop("master", ownedInstanceId: "workshop.05");
        var staging = new WorkshopLiquidStaging(5);
        var ship = new ShipLiquidInventory(5, new Dictionary<string, int> { ["酒"] = 2 });
        var workshopAdapter = new WorkshopFreeLiquidPortAdapter(workshop, "area.workshop");
        var orderAdapter = new WorkshopOrderLiquidPortAdapter(staging, "workshop.05", "area.workshop", 101);
        var shipAdapter = new ShipLiquidInventoryPortAdapter(ship, "ship.05", "area.ship");
        var workshopPort = Port("workshop.in", "area.workshop", ResourcePortDirection.Input,
            WorkshopFreeLiquidPortAdapter.SupportedPolicyId);
        var orderPort = Port("order.in", "area.workshop", ResourcePortDirection.Input,
            WorkshopOrderLiquidPortAdapter.SupportedPolicyId);
        var shipPort = Port("ship.out", "area.ship", ResourcePortDirection.Output,
            ShipLiquidInventoryPortAdapter.SupportedPolicyId);

        var workshopBefore = workshop.LiquidStorage["酒"];
        var stagingBefore = staging.UsedCapacity;
        var shipBefore = ship.GetAmount("酒");

        workshopAdapter.Preflight(workshopPort, "酒", 1);
        orderAdapter.Preflight(orderPort, "酒", 1);
        shipAdapter.Preflight(shipPort, "酒", 1);

        Assert.Equal(workshopBefore, workshop.LiquidStorage["酒"]);
        Assert.Equal(stagingBefore, staging.UsedCapacity);
        Assert.Equal(shipBefore, ship.GetAmount("酒"));
    }
}

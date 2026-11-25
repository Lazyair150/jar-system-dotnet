using System;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class ResourcePortTransferContractTests
{
    private static readonly RuleResourceAccount Source = new("ship-liquid-inventory", "ship.01");
    private static readonly RuleResourceAccount Target = new("workshop-liquid-staging", "workshop.01");

    [Fact]
    public void TransferRequestRequiresDistinctPortsAndAccounts()
    {
        var request = new ResourceTransferRequest(
            "op.1", "connection.1", "ship.liquid.out", "workshop.liquid.in",
            RuleResourceKind.Liquid, "fuel", 3, Source, Target, 4, 9);
        request.Validate();

        Assert.Throws<ArgumentException>(() => (request with { TargetAccount = Source }).Validate());
    }

    [Fact]
    public void RejectedTransferReceiptExplicitlyRetainsTheWholeAmount()
    {
        var receipt = new ResourceTransferReceipt(
            "op.2", "connection.1", "ship.liquid.out", "workshop.liquid.in",
            RuleResourceKind.Liquid, "fuel", Source, Target,
            5, 0, 5, 0, ResourceTransferOutcome.Rejected,
            7, 7, 11, 11, "target.banned");
        receipt.Validate();
    }

    [Fact]
    public void PartialTransferMustAccountForEveryUnit()
    {
        var receipt = new ResourceTransferReceipt(
            "op.3", "connection.1", "ship.liquid.out", "workshop.liquid.in",
            RuleResourceKind.Liquid, "fuel", Source, Target,
            5, 2, 2, 1, ResourceTransferOutcome.PartiallyTransferred,
            7, 8, 11, 12, "target.partial-capacity");
        receipt.Validate();

        Assert.Throws<ArgumentException>(() => (receipt with { RetainedAtSourceAmount = 1 }).Validate());
    }

    [Fact]
    public void UndeterminedPreflightCannotMasqueradeAsAllowed()
    {
        var preflight = new ResourcePortPreflight(
            RuleDecisionKind.Undetermined, "workshop.jar.in",
            new RuleResourceAccount("workshop-jar-storage", "workshop.01"),
            RuleResourceKind.Jar, "圆罐", 1, 3, "policy.unsupported");
        preflight.Validate();

        Assert.Throws<ArgumentException>(() => (preflight with { ReasonCode = "allowed" }).Validate());
    }
}

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
            RuleResourceKind.Liquid, "fuel", 3, Source, Target, "src-v4", "dst-v9");
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
            "src-v7", "src-v7", "dst-v11", "dst-v11", "target.banned");
        receipt.Validate();
    }

    [Fact]
    public void PartialTransferMustAccountForEveryUnit()
    {
        var receipt = new ResourceTransferReceipt(
            "op.3", "connection.1", "ship.liquid.out", "workshop.liquid.in",
            RuleResourceKind.Liquid, "fuel", Source, Target,
            5, 2, 2, 1, ResourceTransferOutcome.PartiallyTransferred,
            "src-v7", "src-v8", "dst-v11", "dst-v12", "target.partial-capacity");
        receipt.Validate();

        Assert.Throws<ArgumentException>(() => (receipt with { RetainedAtSourceAmount = 1 }).Validate());
    }

    [Fact]
    public void UndeterminedPreflightCannotMasqueradeAsAllowed()
    {
        var preflight = new ResourcePortPreflight(
            RuleDecisionKind.Undetermined, "workshop.jar.in",
            new RuleResourceAccount("workshop-jar-storage", "workshop.01"),
            RuleResourceKind.Jar, "圆罐", 1, "area-v3", "policy.unsupported");
        preflight.Validate();

        Assert.Throws<ArgumentException>(() => (preflight with { ReasonCode = "allowed" }).Validate());
    }
}

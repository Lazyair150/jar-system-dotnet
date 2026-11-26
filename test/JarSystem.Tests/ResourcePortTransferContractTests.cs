using System;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
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
        Assert.Throws<ArgumentException>(() => (receipt with { TargetVersionAfter = receipt.TargetVersionBefore }).Validate());
    }

    [Fact]
    public void FinalizationRejectsPostMutationPreflightForDifferentEndpoint()
    {
        var request = new ResourceTransferRequest(
            "op.final-endpoint", "connection.final-endpoint",
            "source.out", "target.in", RuleResourceKind.Liquid, "酒", 2,
            Source, Target, "source-before", "target-before");
        var sourceBefore = new ResourcePortPreflight(
            RuleDecisionKind.Allowed, "source.out", Source, RuleResourceKind.Liquid,
            "酒", 2, "source-before", "allowed");
        var targetBefore = new ResourcePortPreflight(
            RuleDecisionKind.Allowed, "target.in", Target, RuleResourceKind.Liquid,
            "酒", 2, "target-before", "allowed");
        var sourceAfter = sourceBefore with
        {
            ObservedVersionToken = "source-after"
        };
        var targetAfter = targetBefore with
        {
            ObservedVersionToken = "target-after",
            Decision = RuleDecisionKind.Allowed
        };

        Assert.Throws<DomainValidationException>(() =>
            ResourcePortTransferCommitCoordinator.Finalize(
                request, sourceBefore, targetBefore,
                () => sourceAfter,
                () => targetAfter with { Account = new RuleResourceAccount("workshop-liquid-staging", "wrong-owner") },
                null, 0, Array.Empty<ResourceLedgerMutation>()));
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

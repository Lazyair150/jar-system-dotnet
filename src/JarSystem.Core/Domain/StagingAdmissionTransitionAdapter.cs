using System;
using System.Collections.Generic;
using System.Globalization;

namespace JarSystem.Core.Domain;

/// <summary>
/// Adapts the authoritative WorkshopLiquidStaging.Receive receipt into an auditable resource transition.
/// The staging mutation has already been applied by the owning transaction; this adapter never mutates accounts.
/// </summary>
public static class StagingAdmissionTransitionAdapter
{
    public static RuleTransition FromReceipt(
        string workshopInstanceId,
        int orderId,
        string liquid,
        LiquidInventoryReceipt receipt,
        int usedCapacityBefore,
        int usedCapacityAfter,
        int orderBalanceBefore,
        int orderBalanceAfter)
    {
        if (string.IsNullOrWhiteSpace(workshopInstanceId))
            throw new ArgumentException("Exact workshop instance identity is required.", nameof(workshopInstanceId));
        if (orderId <= 0)
            throw new ArgumentOutOfRangeException(nameof(orderId));
        ShipLiquidInventory.ValidateLiquid(liquid);
        ArgumentNullException.ThrowIfNull(receipt);

        if (receipt.RequestedAmount < 0 || receipt.AcceptedAmount < 0 || receipt.DiscardedAmount < 0 ||
            receipt.AcceptedAmount > receipt.RequestedAmount ||
            receipt.DiscardedAmount != receipt.RequestedAmount - receipt.AcceptedAmount)
            throw new ArgumentException("Staging receipt amounts are inconsistent.", nameof(receipt));
        if (usedCapacityBefore < 0 || usedCapacityAfter < 0 ||
            orderBalanceBefore < 0 || orderBalanceAfter < 0 ||
            (long)usedCapacityAfter - usedCapacityBefore != receipt.AcceptedAmount ||
            (long)orderBalanceAfter - orderBalanceBefore != receipt.AcceptedAmount)
            throw new ArgumentException("Staging receipt does not match the authoritative account balance delta.");

        var reasonCode = receipt.RejectionReason switch
        {
            null => receipt.DiscardedAmount == 0 ? "staging.accepted" : "staging.capacity-overflow",
            { } reason when reason.StartsWith("工作台禁", StringComparison.Ordinal) => "staging.liquid-banned",
            _ => "staging.rejected"
        };
        var outputBoundary = new RuleResourceAccount(
            "workshop.order-output-boundary",
            workshopInstanceId + "/order/" + orderId.ToString(CultureInfo.InvariantCulture));
        var stagingAccount = new RuleResourceAccount(
            "workshop.free-liquid-staging", workshopInstanceId);
        var effects = new List<RuleResourceEffect>(2);

        // The producing operation has already transferred the full amount into this boundary.
        // Admission settles that exact output: accepted liquid enters staging, the rest is discarded.
        if (receipt.AcceptedAmount > 0)
            effects.Add(new RuleResourceEffect(
                RuleResourceEffectKind.Transfer, RuleResourceKind.Liquid, liquid,
                receipt.AcceptedAmount, outputBoundary, stagingAccount));
        if (receipt.DiscardedAmount > 0)
            effects.Add(new RuleResourceEffect(
                RuleResourceEffectKind.Discard, RuleResourceKind.Liquid, liquid,
                receipt.DiscardedAmount, outputBoundary, null));

        var target = workshopInstanceId + "/order/" + orderId.ToString(CultureInfo.InvariantCulture);
        var before = new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("workshopInstanceId", workshopInstanceId),
            new KeyValuePair<string, string>("orderId", orderId.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("liquid", liquid),
            new KeyValuePair<string, string>("usedCapacity", usedCapacityBefore.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("orderBalance", orderBalanceBefore.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("outputBoundaryAmount", receipt.RequestedAmount.ToString(CultureInfo.InvariantCulture))
        });
        var after = new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("workshopInstanceId", workshopInstanceId),
            new KeyValuePair<string, string>("orderId", orderId.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("liquid", liquid),
            new KeyValuePair<string, string>("usedCapacity", usedCapacityAfter.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("orderBalance", orderBalanceAfter.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("outputBoundaryAmount", "0"),
            new KeyValuePair<string, string>("requestedAmount", receipt.RequestedAmount.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("acceptedAmount", receipt.AcceptedAmount.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("discardedAmount", receipt.DiscardedAmount.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("reasonCode", reasonCode)
        });

        var transition = new RuleTransition(
            "workshop.staging.admission", 1, "workshop.staging", target, before, after, effects);
        transition.Validate();
        return transition;
    }
}

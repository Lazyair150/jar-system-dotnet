using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JarSystem.Core.Domain;

/// <summary>
/// Builds the explicit one-way Master/Slave resource handoff proof after a read-only preflight.
/// It enforces source staging Ban and destination staging Ban/capacity. Jar ownership transfer is polarity-independent.
/// It does not move resources; the owning transfer transaction remains responsible for commit/rollback.
/// </summary>
public static class WorkshopRoleTransferTransitionAdapter
{
    public static RuleTransition Create(
        string sourceWorkshopInstanceId,
        string destinationWorkshopInstanceId,
        IEnumerable<int> sourceJarIds,
        IEnumerable<int> transferJarIds,
        IReadOnlyDictionary<string, int> sourceStagingBalances,
        IReadOnlyDictionary<string, int> transferLiquidAmounts,
        IEnumerable<string> sourceBannedLiquids,
        IEnumerable<string> destinationBannedLiquids,
        int destinationJarCount,
        int destinationJarCapacity,
        int destinationStagingUsedCapacity,
        int destinationStagingCapacity)
    {
        if (string.IsNullOrWhiteSpace(sourceWorkshopInstanceId))
            throw new ArgumentException("Exact source workshop instance identity is required.", nameof(sourceWorkshopInstanceId));
        if (string.IsNullOrWhiteSpace(destinationWorkshopInstanceId))
            throw new ArgumentException("Exact destination workshop instance identity is required.", nameof(destinationWorkshopInstanceId));
        if (string.Equals(sourceWorkshopInstanceId, destinationWorkshopInstanceId, StringComparison.Ordinal))
            throw new ArgumentException("A workshop role transfer must change the owning instance.");
        ArgumentNullException.ThrowIfNull(sourceJarIds);
        ArgumentNullException.ThrowIfNull(transferJarIds);
        ArgumentNullException.ThrowIfNull(sourceStagingBalances);
        ArgumentNullException.ThrowIfNull(transferLiquidAmounts);
        ArgumentNullException.ThrowIfNull(sourceBannedLiquids);
        ArgumentNullException.ThrowIfNull(destinationBannedLiquids);

        var sourceInventory = sourceJarIds.ToArray();
        var sourceIds = sourceInventory.ToHashSet();
        var jarIds = transferJarIds.ToArray();
        if (sourceInventory.Any(id => id <= 0) ||
            sourceIds.Count != sourceInventory.Length ||
            jarIds.Any(id => id <= 0) ||
            jarIds.Distinct().Count() != jarIds.Length ||
            jarIds.Any(id => !sourceIds.Contains(id)))
            throw new ArgumentException(
                "Source inventory and transfer Jar identities must be unique positive IDs, and every transferred Jar must belong to the exact source workshop.");

        var sourceBans = sourceBannedLiquids.ToHashSet(StringComparer.Ordinal);
        var destinationBans = destinationBannedLiquids.ToHashSet(StringComparer.Ordinal);
        if (destinationJarCount < 0 || destinationJarCapacity < destinationJarCount ||
            jarIds.Length > destinationJarCapacity - destinationJarCount)
            throw new ArgumentException("Destination workshop does not have enough Jar capacity.");
        if (destinationStagingUsedCapacity < 0 ||
            destinationStagingCapacity < destinationStagingUsedCapacity)
            throw new ArgumentException("Destination staging capacity snapshot is invalid.");

        var banned = destinationBans;
        var normalizedLiquids = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in transferLiquidAmounts)
        {
            ShipLiquidInventory.ValidateLiquid(item.Key);
            ShipLiquidInventory.ValidateAmount(item.Value, allowZero: true);
            if (item.Value == 0) continue;
            if (sourceBans.Contains(item.Key))
                throw new ArgumentException("Source Workshop cannot transfer its banned staging liquid: " + item.Key);
            if (banned.Contains(item.Key))
                throw new ArgumentException("Destination staging bans transfer liquid: " + item.Key);
            if (sourceStagingBalances.GetValueOrDefault(item.Key) < item.Value)
                throw new ArgumentException("Source staging balance is insufficient for transfer liquid: " + item.Key);
            normalizedLiquids.Add(item.Key, item.Value);
        }

        foreach (var item in sourceStagingBalances)
        {
            ShipLiquidInventory.ValidateLiquid(item.Key);
            ShipLiquidInventory.ValidateAmount(item.Value, allowZero: true);
            if (item.Value > 0 && sourceBans.Contains(item.Key))
                throw new ArgumentException("Source staging snapshot contains a banned liquid: " + item.Key);
        }

        var liquidTotal = ShipLiquidInventory.TotalAmount(normalizedLiquids);
        if (liquidTotal > destinationStagingCapacity - destinationStagingUsedCapacity)
            throw new ArgumentException("Destination staging capacity is insufficient for the transfer batch.");

        var sourceJarAccount = new RuleResourceAccount("workshop.jar-inventory", sourceWorkshopInstanceId);
        var destinationJarAccount = new RuleResourceAccount("workshop.jar-inventory", destinationWorkshopInstanceId);
        var sourceStagingAccount = new RuleResourceAccount("workshop.free-liquid-staging", sourceWorkshopInstanceId);
        var destinationStagingAccount = new RuleResourceAccount("workshop.free-liquid-staging", destinationWorkshopInstanceId);
        var effects = new List<RuleResourceEffect>(jarIds.Length + normalizedLiquids.Count);

        foreach (var jarId in jarIds.OrderBy(id => id))
            effects.Add(new RuleResourceEffect(
                RuleResourceEffectKind.Transfer, RuleResourceKind.Jar,
                jarId.ToString(CultureInfo.InvariantCulture), 1,
                sourceJarAccount, destinationJarAccount));
        foreach (var item in normalizedLiquids)
            effects.Add(new RuleResourceEffect(
                RuleResourceEffectKind.Transfer, RuleResourceKind.Liquid,
                item.Key, item.Value, sourceStagingAccount, destinationStagingAccount));

        var target = sourceWorkshopInstanceId + "->" + destinationWorkshopInstanceId;
        var beforeValues = new List<KeyValuePair<string, string>>
        {
            new("sourceWorkshopInstanceId", sourceWorkshopInstanceId),
            new("destinationWorkshopInstanceId", destinationWorkshopInstanceId),
            new("sourceJarCount", sourceIds.Count.ToString(CultureInfo.InvariantCulture)),
            new("destinationJarCount", destinationJarCount.ToString(CultureInfo.InvariantCulture)),
            new("destinationStagingUsedCapacity", destinationStagingUsedCapacity.ToString(CultureInfo.InvariantCulture))
        };
        var afterValues = beforeValues
            .Where(item => item.Key != "sourceJarCount" &&
                           item.Key != "destinationJarCount" &&
                           item.Key != "destinationStagingUsedCapacity")
            .ToList();
        afterValues.AddRange(new[]
        {
            new KeyValuePair<string, string>("sourceJarCount", (sourceIds.Count - jarIds.Length).ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("destinationJarCount", (destinationJarCount + jarIds.Length).ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("destinationStagingUsedCapacity", (destinationStagingUsedCapacity + liquidTotal).ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("transferredJarCount", jarIds.Length.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("transferredLiquidAmount", liquidTotal.ToString(CultureInfo.InvariantCulture))
        });
        var before = new RuleStateSnapshot(beforeValues);
        var after = new RuleStateSnapshot(afterValues);

        var transition = new RuleTransition(
            "workshop.role-transfer", 1, "workshop.transfer", target, before, after, effects);
        transition.Validate();
        return transition;
    }
}

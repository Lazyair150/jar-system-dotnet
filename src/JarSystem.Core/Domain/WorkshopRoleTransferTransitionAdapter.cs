using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JarSystem.Core.Domain;

/// <summary>
/// Builds the explicit one-way Master/Slave resource handoff proof after a read-only preflight.
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
        int destinationStagingCapacity,
        IReadOnlyDictionary<int, JarFacts>? transferJarFacts = null)
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

        var sourceIds = sourceJarIds.ToHashSet();
        var jarIds = transferJarIds.ToArray();
        if (sourceIds.Any(id => id <= 0) || jarIds.Any(id => id <= 0) ||
            jarIds.Distinct().Count() != jarIds.Length || jarIds.Any(id => !sourceIds.Contains(id)))
            throw new ArgumentException("Transfer Jar identities must be unique positive IDs owned by the exact source workshop.");

        if (jarIds.Length > 0 && transferJarFacts == null)
            throw new ArgumentException("Exact Jar facts are required to validate destination polarity compatibility.", nameof(transferJarFacts));
        if (transferJarFacts != null &&
            (!transferJarFacts.Keys.OrderBy(x => x).SequenceEqual(jarIds.OrderBy(x => x)) ||
             transferJarFacts.Keys.Any(id => id <= 0)))
            throw new ArgumentException("Transfer Jar facts must map exactly the transferred Jar identities.", nameof(transferJarFacts));

        var destinationBans = destinationBannedLiquids.ToHashSet(StringComparer.Ordinal);
        var destinationCapability = new WorkshopCapability(ban: destinationBans);
        if (transferJarFacts != null)
        {
            foreach (var item in transferJarFacts)
            {
                if (!destinationCapability.CanOperateJar(item.Value.Polarity))
                    throw new ArgumentException("Destination Workshop cannot operate transferred Jar polarity: " + item.Key);
            }
        }

        var sourceBans = sourceBannedLiquids.ToHashSet(StringComparer.Ordinal);
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
        var before = new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("sourceWorkshopInstanceId", sourceWorkshopInstanceId),
            new KeyValuePair<string, string>("destinationWorkshopInstanceId", destinationWorkshopInstanceId),
            new KeyValuePair<string, string>("sourceJarCount", sourceIds.Count.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("destinationJarCount", destinationJarCount.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("destinationStagingUsedCapacity", destinationStagingUsedCapacity.ToString(CultureInfo.InvariantCulture))
        });
        var after = new RuleStateSnapshot(new[]
        {
            new KeyValuePair<string, string>("sourceWorkshopInstanceId", sourceWorkshopInstanceId),
            new KeyValuePair<string, string>("destinationWorkshopInstanceId", destinationWorkshopInstanceId),
            new KeyValuePair<string, string>("sourceJarCount", (sourceIds.Count - jarIds.Length).ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("destinationJarCount", (destinationJarCount + jarIds.Length).ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("destinationStagingUsedCapacity", (destinationStagingUsedCapacity + liquidTotal).ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("transferredJarCount", jarIds.Length.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("transferredLiquidAmount", liquidTotal.ToString(CultureInfo.InvariantCulture))
        });

        var transition = new RuleTransition(
            "workshop.role-transfer", 1, "workshop.transfer", target, before, after, effects);
        transition.Validate();
        return transition;
    }
}

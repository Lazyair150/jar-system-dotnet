using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace JarSystem.Core.Domain;

/// <summary>
/// Read-only adapter boundary for one authoritative account. Preflight never mutates the account;
/// committing a transfer remains the responsibility of a later transaction coordinator.
/// </summary>
public interface IResourcePortAdapter
{
    RuleResourceAccount Account { get; }
    ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount);
}

internal static class ResourcePortVersionToken
{
    public static string Create(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var canonical = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Value is null || !canonical.TryAdd(item.Key, item.Value))
                throw new ArgumentException("Account version facts must have unique non-empty keys and non-null values.");
        }

        var payload = string.Join("\n", canonical.Select(item =>
            item.Key.Length.ToString(CultureInfo.InvariantCulture) + ":" + item.Key +
            item.Value.Length.ToString(CultureInfo.InvariantCulture) + ":" + item.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public static string Amounts(string prefix, IReadOnlyDictionary<string, int> amounts) =>
        string.Join("|", amounts.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => prefix + ":" + item.Key + "=" + item.Value.ToString(CultureInfo.InvariantCulture)));
}

internal static class ResourcePortPreflightFactory
{
    public static void ValidateRequest(string resourceIdentity, int amount)
    {
        if (string.IsNullOrWhiteSpace(resourceIdentity))
            throw new ArgumentException("Resource identity is required.", nameof(resourceIdentity));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Resource amount must be positive.");
    }

    public static ResourcePortPreflight Evaluate(
        IResourcePortAdapter adapter,
        CircuitPortDefinition port,
        string resourceIdentity,
        int amount,
        string versionToken,
        Func<LiquidGrant> evaluate)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (string.IsNullOrWhiteSpace(resourceIdentity))
            throw new ArgumentException("Resource identity is required.", nameof(resourceIdentity));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Resource amount must be positive.");
        if (port.ResourceKind != RuleResourceKind.Liquid)
            return Result(RuleDecisionKind.Undetermined, "port.resource-kind.unsupported", "This adapter only supports liquid resources.");
        if (port.Direction is not (ResourcePortDirection.Input or ResourcePortDirection.Output))
            return Result(RuleDecisionKind.Undetermined, "port.direction.unsupported", "Port direction is unsupported.");
        if (port.ResourceIdentities.Count > 0 && !port.ResourceIdentities.Contains(resourceIdentity, StringComparer.Ordinal))
            return Result(RuleDecisionKind.Rejected, "port.resource-identity.mismatch", "Resource identity is outside the port allow-list.");
        if (port.MaxAmountPerTransfer is { } limit && amount > limit)
            return Result(RuleDecisionKind.Rejected, "port.transfer-limit.exceeded", "Requested amount exceeds the port transfer limit.");
        if (port.MaxAmountPerLogicalTimeMilli is not null)
            return Result(RuleDecisionKind.Undetermined, "port.throughput.scheduler-required", "Logical-time throughput requires a scheduler window.");
        if (string.IsNullOrWhiteSpace(versionToken))
            return Result(RuleDecisionKind.Undetermined, "account.version.unavailable", "Authoritative account version is unavailable.");

        try
        {
            var grant = evaluate();
            return grant.Granted
                ? Result(RuleDecisionKind.Allowed, "allowed", null)
                : Result(RuleDecisionKind.Rejected, grant.Code ?? "account.policy.rejected", grant.Reason);
        }
        catch (InvalidOperationException exception)
        {
            return Result(RuleDecisionKind.Undetermined, "account.busy-or-unavailable", exception.Message);
        }

        ResourcePortPreflight Result(RuleDecisionKind decision, string reasonCode, string? diagnostic) =>
            new(decision, port.PortId, adapter.Account, RuleResourceKind.Liquid,
                resourceIdentity, amount, versionToken, reasonCode, diagnostic);
    }
}

/// <summary>Preflight adapter for a Workshop's legacy free-liquid pool; Jar-internal liquid is deliberately outside this account.</summary>
public sealed class WorkshopFreeLiquidPortAdapter : IResourcePortAdapter
{
    public const string SupportedPolicyId = "workshop.free-liquid-staging";
    public const int SupportedPolicyVersion = 1;

    private readonly Workshop _workshop;
    private readonly string _areaId;
    public RuleResourceAccount Account { get; }

    public WorkshopFreeLiquidPortAdapter(Workshop workshop, string areaId)
    {
        _workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        if (string.IsNullOrWhiteSpace(workshop.OwnedInstanceId))
            throw new ArgumentException("An exact Board-owned Workshop instance identity is required.", nameof(workshop));
        Account = new RuleResourceAccount("workshop.free-liquid-staging", workshop.OwnedInstanceId);
        Account.Validate();
    }

    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.OwnerId),
            new KeyValuePair<string, string>("capacity", _workshop.MaxLiquids.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("balances", ResourcePortVersionToken.Amounts("liquid", _workshop.LiquidStorage)),
            new KeyValuePair<string, string>("banned", string.Join("|", _workshop.Ban.OrderBy(item => item, StringComparer.Ordinal)))
        });

        if (port.AreaId != _areaId)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, Math.Max(1, amount), token,
                "port.area.mismatch", "Port does not belong to this Workshop Area.");
        if (port.PolicyId != SupportedPolicyId || port.PolicyVersion != SupportedPolicyVersion)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, Math.Max(1, amount), token,
                "port.policy.unsupported", "Workshop free-liquid policy identity/version is unsupported.");

        return ResourcePortPreflightFactory.Evaluate(this, port, resourceIdentity, amount, token,
            () => port.Direction == ResourcePortDirection.Input
                ? _workshop.CanStoreLiquid(resourceIdentity, amount)
                : _workshop.CanUseLiquid(resourceIdentity, amount));
    }
}

/// <summary>Preflight adapter for one exact order sub-account in a Workshop's shared liquid staging area.</summary>
public sealed class WorkshopOrderLiquidPortAdapter : IResourcePortAdapter
{
    public const string SupportedPolicyId = "workshop.order-liquid-staging";
    public const int SupportedPolicyVersion = 1;

    private readonly WorkshopLiquidStaging _staging;
    private readonly string _areaId;
    private readonly int _orderId;
    public RuleResourceAccount Account { get; }

    public WorkshopOrderLiquidPortAdapter(
        WorkshopLiquidStaging staging, string workshopInstanceId, string areaId, int orderId)
    {
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        if (string.IsNullOrWhiteSpace(workshopInstanceId))
            throw new ArgumentException("Exact Workshop instance identity is required.", nameof(workshopInstanceId));
        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));
        _orderId = orderId;
        Account = new RuleResourceAccount(
            "workshop.order-liquid-staging",
            workshopInstanceId + ":order:" + orderId.ToString(CultureInfo.InvariantCulture));
        Account.Validate();
    }

    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var balances = _staging.GetBalance(_orderId);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.OwnerId),
            new KeyValuePair<string, string>("capacity", _staging.Capacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("used", _staging.UsedCapacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("occupied", _staging.OccupiedCapacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("reserved", _staging.ReservedCapacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("balances", ResourcePortVersionToken.Amounts("liquid", balances)),
            new KeyValuePair<string, string>("banned", string.Join("|", _staging.BannedLiquids.OrderBy(item => item, StringComparer.Ordinal)))
        });

        if (port.AreaId != _areaId)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, Math.Max(1, amount), token,
                "port.area.mismatch", "Port does not belong to this Workshop Area.");
        if (port.PolicyId != SupportedPolicyId || port.PolicyVersion != SupportedPolicyVersion)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, Math.Max(1, amount), token,
                "port.policy.unsupported", "Order staging policy identity/version is unsupported.");

        return ResourcePortPreflightFactory.Evaluate(this, port, resourceIdentity, amount, token,
            () => port.Direction == ResourcePortDirection.Input
                ? _staging.CanAcceptOperation(_orderId, resourceIdentity, amount)
                : _staging.CanConsume(_orderId, resourceIdentity, amount));
    }
}

/// <summary>Preflight adapter for the self-ship liquid inventory; all liquids share one aggregate capacity.</summary>
public sealed class ShipLiquidInventoryPortAdapter : IResourcePortAdapter
{
    public const string SupportedPolicyId = "ship.liquid-inventory";
    public const int SupportedPolicyVersion = 1;

    private readonly ShipLiquidInventory _inventory;
    private readonly string _areaId;
    public RuleResourceAccount Account { get; }

    public ShipLiquidInventoryPortAdapter(ShipLiquidInventory inventory, string shipIndividualId, string areaId)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        Account = new RuleResourceAccount(
            "ship.liquid-inventory",
            CircuitAreaDefinition.RequireIdentity(shipIndividualId, nameof(shipIndividualId)));
        Account.Validate();
    }

    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.OwnerId),
            new KeyValuePair<string, string>("capacity", _inventory.Capacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("used", _inventory.UsedCapacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("reserved-return", _inventory.ReservedReturnCapacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("balances", ResourcePortVersionToken.Amounts("liquid", _inventory.Balances))
        });

        if (port.AreaId != _areaId)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, Math.Max(1, amount), token,
                "port.area.mismatch", "Port does not belong to this Ship Area.");
        if (port.PolicyId != SupportedPolicyId || port.PolicyVersion != SupportedPolicyVersion)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, Math.Max(1, amount), token,
                "port.policy.unsupported", "Ship liquid inventory policy identity/version is unsupported.");

        return ResourcePortPreflightFactory.Evaluate(this, port, resourceIdentity, amount, token, () =>
        {
            if (port.Direction == ResourcePortDirection.Output)
                return _inventory.GetAmount(resourceIdentity) >= amount
                    ? LiquidGrant.Yes()
                    : LiquidGrant.Insufficient(resourceIdentity);
            return _inventory.AvailableCapacity >= amount
                ? LiquidGrant.Yes()
                : LiquidGrant.OverCapacity(resourceIdentity);
        });
    }
}

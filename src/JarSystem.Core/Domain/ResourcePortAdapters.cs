using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// Read-only adapter boundary for one authoritative account. Preflight never mutates the account;
/// committing a transfer remains the responsibility of a later transaction coordinator.
/// </summary>
public interface IResourcePortAdapter
{
    RuleResourceAccount Account { get; }
    ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount);
}

/// <summary>
/// Internal runtime binding for a capacity domain. The object reference is never
/// persisted or hashed; it proves concrete binding consistency within one World transaction.
/// </summary>
internal sealed record ResourceCapacityDomainBinding(
    ResourceCapacityDomainId Id,
    object RuntimeStateReference)
{
    public static ResourceCapacityDomainBinding Create(
        OwnerIdentity owner, string domainKind, string localDomainId, object runtimeStateReference)
    {
        ArgumentNullException.ThrowIfNull(runtimeStateReference);
        return new ResourceCapacityDomainBinding(
            new ResourceCapacityDomainId(owner, domainKind, localDomainId),
            runtimeStateReference);
    }

    public bool IsSameRuntimeDomain(ResourceCapacityDomainBinding other) =>
        other != null && Id == other.Id &&
        ReferenceEquals(RuntimeStateReference, other.RuntimeStateReference);
}

internal interface IResourceCapacityDomainBoundAdapter
{
    ResourceCapacityDomainBinding? CapacityDomainBinding { get; }
}

internal interface IResourceAccountRuntimeBoundAdapter
{
    object AccountRuntimeStateReference { get; }
}

internal sealed record ResourceBatchAccountRuntimeBinding(
    string AccountId,
    object RuntimeStateReference);

internal sealed class ResourceBatchBindingSet
{
    public IReadOnlyList<ResourceBatchAccountRuntimeBinding> Accounts { get; }
    public IReadOnlyList<ResourceCapacityDomainBinding> CapacityDomains { get; }

    private ResourceBatchBindingSet(
        IReadOnlyList<ResourceBatchAccountRuntimeBinding> accounts,
        IReadOnlyList<ResourceCapacityDomainBinding> capacityDomains)
    {
        Accounts = accounts;
        CapacityDomains = capacityDomains;
    }

    public static ResourceBatchBindingSet Create(IEnumerable<IResourcePortAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var accountBindings = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var domainBindings = new SortedDictionary<string, ResourceCapacityDomainBinding>(StringComparer.Ordinal);
        var count = 0;

        foreach (var adapter in adapters)
        {
            count++;
            if (adapter == null ||
                adapter is not IResourceCapacityDomainBoundAdapter capacityBound ||
                adapter is not IResourceAccountRuntimeBoundAdapter accountBound ||
                capacityBound.CapacityDomainBinding is not { } capacityBinding)
                throw new DomainValidationException(
                    "复合资源批次只接受具有 World-scoped Account 与 CapacityDomain 绑定的 Adapter。");

            if (string.IsNullOrWhiteSpace(adapter.Account.AccountId) ||
                !adapter.Account.AccountId.StartsWith("account:v2:", StringComparison.Ordinal))
                throw new DomainValidationException(
                    "复合资源批次不能使用 legacy account:v1 或未知账户身份。");

            AccountIdentity accountIdentity;
            try
            {
                accountIdentity = AccountIdentity.ParseV2(adapter.Account.AccountId);
            }
            catch (FormatException exception)
            {
                throw new DomainValidationException(
                    "复合资源批次包含非规范 AccountId v2：" + exception.Message);
            }

            if (!accountIdentity.IsValid ||
                accountIdentity.Owner.WorldInstanceId != capacityBinding.Id.Owner.WorldInstanceId ||
                accountIdentity.Owner.OwnerKind != capacityBinding.Id.Owner.OwnerKind)
                throw new DomainValidationException(
                    "资源账户与容量域不属于同一个 World/OwnerKind 作用域。");

            if (accountBindings.TryGetValue(adapter.Account.AccountId, out var existingAccount))
            {
                if (!ReferenceEquals(existingAccount, accountBound.AccountRuntimeStateReference))
                    throw new DomainValidationException(
                        "同一 AccountId 在一个批次中绑定到了不同的权威运行时对象。");
            }
            else
            {
                accountBindings.Add(adapter.Account.AccountId, accountBound.AccountRuntimeStateReference);
            }

            var canonicalDomainId = capacityBinding.Id.ToCanonicalString();
            if (domainBindings.TryGetValue(canonicalDomainId, out var existingDomain))
            {
                if (!existingDomain.IsSameRuntimeDomain(capacityBinding))
                    throw new DomainValidationException(
                        "同一 CapacityDomainId 在一个批次中绑定到了不同的权威容量域对象。");
            }
            else
            {
                domainBindings.Add(canonicalDomainId, capacityBinding);
            }
        }

        if (count == 0)
            throw new DomainValidationException("复合资源批次至少需要一个已绑定的资源账户。");

        return new ResourceBatchBindingSet(
            Array.AsReadOnly(accountBindings.Select(item =>
                new ResourceBatchAccountRuntimeBinding(item.Key, item.Value)).ToArray()),
            Array.AsReadOnly(domainBindings.Values.ToArray()));
    }
}

internal static class ResourceOwnerIdentityGuard
{
    public static void Require(
        OwnerIdentity owner,
        ResourceOwnerKind expectedKind,
        string? expectedLocalOwnerId = null)
    {
        if (!owner.IsValid || owner.OwnerKind != expectedKind ||
            (expectedLocalOwnerId != null &&
             !string.Equals(owner.LocalOwnerId, expectedLocalOwnerId, StringComparison.Ordinal)))
            throw new ArgumentException("Resource adapter requires the exact expected World-scoped owner identity.", nameof(owner));
    }

    public static OwnerIdentity ForOrderStaging(OwnerIdentity workshopOwner, int orderId)
    {
        Require(workshopOwner, ResourceOwnerKind.Workshop);
        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));
        var scopedLocalId = "order-staging:" +
            OwnerIdentity.EncodeUtf8Field(workshopOwner.LocalOwnerId) +
            OwnerIdentity.EncodeUtf8Field(orderId.ToString(CultureInfo.InvariantCulture));
        return new OwnerIdentity(
            workshopOwner.WorldInstanceId, ResourceOwnerKind.Workshop, scopedLocalId);
    }
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
        string.Concat(amounts.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => prefix.Length.ToString(CultureInfo.InvariantCulture) + ":" + prefix +
                item.Key.Length.ToString(CultureInfo.InvariantCulture) + ":" + item.Key +
                item.Value.ToString(CultureInfo.InvariantCulture) + ";"));

    public static string Identities(IEnumerable<string> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        return string.Concat(identities.OrderBy(item => item, StringComparer.Ordinal)
            .Select(item => item.Length.ToString(CultureInfo.InvariantCulture) + ":" + item));
    }
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
public sealed class WorkshopFreeLiquidPortAdapter : IResourcePortAdapter, IResourceCapacityDomainBoundAdapter, IResourceAccountRuntimeBoundAdapter
{
    public const string SupportedPolicyId = "workshop.free-liquid-staging";
    public const int SupportedPolicyVersion = 1;

    private readonly Workshop _workshop;
    private readonly string _areaId;
    public RuleResourceAccount Account { get; }
    internal ResourceCapacityDomainBinding? CapacityDomainBinding { get; private set; }
    ResourceCapacityDomainBinding? IResourceCapacityDomainBoundAdapter.CapacityDomainBinding => CapacityDomainBinding;
    object AccountRuntimeStateReference => _workshop;
    internal bool IsBoundTo(Workshop workshop) => ReferenceEquals(_workshop, workshop);

    public WorkshopFreeLiquidPortAdapter(Workshop workshop, string areaId)
    {
        _workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        if (string.IsNullOrWhiteSpace(workshop.OwnedInstanceId))
            throw new ArgumentException("An exact Board-owned Workshop instance identity is required.", nameof(workshop));
        Account = new RuleResourceAccount("workshop.free-liquid-staging", workshop.OwnedInstanceId);
        Account.Validate();
    }

    public WorkshopFreeLiquidPortAdapter(
        Workshop workshop, string areaId, OwnerIdentity workshopOwner)
    {
        _workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        if (string.IsNullOrWhiteSpace(workshop.OwnedInstanceId))
            throw new ArgumentException("An exact Board-owned Workshop instance identity is required.", nameof(workshop));
        ResourceOwnerIdentityGuard.Require(workshopOwner, ResourceOwnerKind.Workshop, workshop.OwnedInstanceId);
        Account = new RuleResourceAccount(workshopOwner, "workshop.free-liquid-staging");
        Account.Validate();
        CapacityDomainBinding = ResourceCapacityDomainBinding.Create(workshopOwner, "workshop.free-liquid", _areaId, workshop);
    }


    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.AccountId),
            new KeyValuePair<string, string>("capacity", _workshop.MaxLiquids.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("balances", ResourcePortVersionToken.Amounts("liquid", _workshop.LiquidStorage)),
            new KeyValuePair<string, string>("banned", ResourcePortVersionToken.Identities(_workshop.Ban))
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
public sealed class WorkshopOrderLiquidPortAdapter : IResourcePortAdapter, IResourceCapacityDomainBoundAdapter, IResourceAccountRuntimeBoundAdapter
{
    public const string SupportedPolicyId = "workshop.order-liquid-staging";
    public const int SupportedPolicyVersion = 1;

    private readonly WorkshopLiquidStaging _staging;
    private readonly string _areaId;
    private readonly int _orderId;
    public RuleResourceAccount Account { get; }
    internal ResourceCapacityDomainBinding? CapacityDomainBinding { get; private set; }
    ResourceCapacityDomainBinding? IResourceCapacityDomainBoundAdapter.CapacityDomainBinding => CapacityDomainBinding;
    object AccountRuntimeStateReference => _staging;
    public int OrderId => _orderId;
    internal bool IsBoundTo(WorkshopLiquidStaging staging) => ReferenceEquals(_staging, staging);

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

    public WorkshopOrderLiquidPortAdapter(
        WorkshopLiquidStaging staging, OwnerIdentity workshopOwner, string areaId, int orderId)
    {
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));
        _orderId = orderId;
        var stagingOwner = ResourceOwnerIdentityGuard.ForOrderStaging(workshopOwner, orderId);
        Account = new RuleResourceAccount(stagingOwner, "workshop.order-liquid-staging");
        Account.Validate();
        CapacityDomainBinding = ResourceCapacityDomainBinding.Create(workshopOwner, "workshop.liquid-staging", _areaId, _staging);
    }


    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var balances = _staging.GetBalance(_orderId);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.AccountId),
            new KeyValuePair<string, string>("capacity", _staging.Capacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("used", _staging.UsedCapacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("occupied", _staging.OccupiedCapacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("reserved", _staging.ReservedCapacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("balances", ResourcePortVersionToken.Amounts("liquid", balances)),
            new KeyValuePair<string, string>("banned", ResourcePortVersionToken.Identities(_staging.BannedLiquids))
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
public sealed class ShipLiquidInventoryPortAdapter : IResourcePortAdapter, IResourceCapacityDomainBoundAdapter, IResourceAccountRuntimeBoundAdapter
{
    public const string SupportedPolicyId = "ship.liquid-inventory";
    public const int SupportedPolicyVersion = 1;

    private readonly ShipLiquidInventory _inventory;
    private readonly string _areaId;
    public RuleResourceAccount Account { get; }
    internal ResourceCapacityDomainBinding? CapacityDomainBinding { get; private set; }
    ResourceCapacityDomainBinding? IResourceCapacityDomainBoundAdapter.CapacityDomainBinding => CapacityDomainBinding;
    object AccountRuntimeStateReference => _inventory;
    internal bool IsBoundTo(ShipLiquidInventory inventory) => ReferenceEquals(_inventory, inventory);

    public ShipLiquidInventoryPortAdapter(ShipLiquidInventory inventory, string shipIndividualId, string areaId)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        Account = new RuleResourceAccount(
            "ship.liquid-inventory",
            CircuitAreaDefinition.RequireIdentity(shipIndividualId, nameof(shipIndividualId)));
        Account.Validate();
    }

    public ShipLiquidInventoryPortAdapter(
        ShipLiquidInventory inventory, OwnerIdentity shipOwner, string areaId)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        ResourceOwnerIdentityGuard.Require(shipOwner, ResourceOwnerKind.Ship);
        Account = new RuleResourceAccount(shipOwner, "ship.liquid-inventory");
        Account.Validate();
        CapacityDomainBinding = ResourceCapacityDomainBinding.Create(shipOwner, "ship.liquid-inventory", _areaId, _inventory);
    }


    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.AccountId),
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


/// <summary>Preflight adapter for one Workshop's independent prefill account, never merged with order sub-accounts.</summary>
public sealed class WorkshopPrefillLiquidPortAdapter : IResourcePortAdapter, IResourceCapacityDomainBoundAdapter, IResourceAccountRuntimeBoundAdapter
{
    public const string SupportedPolicyId = "workshop.prefill-liquid-staging";
    public const int SupportedPolicyVersion = 1;

    private readonly WorkshopLiquidStaging _staging;
    private readonly string _areaId;
    public RuleResourceAccount Account { get; }
    internal ResourceCapacityDomainBinding? CapacityDomainBinding { get; private set; }
    ResourceCapacityDomainBinding? IResourceCapacityDomainBoundAdapter.CapacityDomainBinding => CapacityDomainBinding;
    object AccountRuntimeStateReference => _staging;
    internal bool IsBoundTo(WorkshopLiquidStaging staging) => ReferenceEquals(_staging, staging);

    public WorkshopPrefillLiquidPortAdapter(
        WorkshopLiquidStaging staging, string workshopInstanceId, string areaId)
    {
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        Account = new RuleResourceAccount(
            "workshop.prefill-liquid-staging",
            CircuitAreaDefinition.RequireIdentity(workshopInstanceId, nameof(workshopInstanceId)));
        Account.Validate();
    }

    public WorkshopPrefillLiquidPortAdapter(
        WorkshopLiquidStaging staging, OwnerIdentity workshopOwner, string areaId)
    {
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        ResourceOwnerIdentityGuard.Require(workshopOwner, ResourceOwnerKind.Workshop);
        Account = new RuleResourceAccount(workshopOwner, "workshop.prefill-liquid-staging");
        Account.Validate();
        CapacityDomainBinding = ResourceCapacityDomainBinding.Create(workshopOwner, "workshop.liquid-staging", _areaId, _staging);
    }


    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.AccountId),
            new KeyValuePair<string, string>("capacity", _staging.Capacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("occupied", _staging.OccupiedCapacity.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("prefill", ResourcePortVersionToken.Amounts("liquid", _staging.PrefillBalances)),
            new KeyValuePair<string, string>("banned", ResourcePortVersionToken.Identities(_staging.BannedLiquids))
        });

        if (port.AreaId != _areaId)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, amount, token,
                "port.area.mismatch", "Port does not belong to this Workshop Area.");
        if (port.PolicyId != SupportedPolicyId || port.PolicyVersion != SupportedPolicyVersion)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, amount, token,
                "port.policy.unsupported", "Workshop prefill policy identity/version is unsupported.");

        return ResourcePortPreflightFactory.Evaluate(this, port, resourceIdentity, amount, token, () =>
        {
            if (port.Direction == ResourcePortDirection.Input)
                return _staging.CanAcceptOperation(resourceIdentity, amount);
            if (_staging.BannedLiquids.Contains(resourceIdentity, StringComparer.Ordinal))
                return LiquidGrant.Banned(resourceIdentity);
            return _staging.PrefillBalances.GetValueOrDefault(resourceIdentity) >= amount
                ? LiquidGrant.Yes()
                : LiquidGrant.Insufficient(resourceIdentity);
        });
    }
}

/// <summary>Preflight adapter for the Workshop's discrete Jar storage; Jar-internal liquid is not checked against Workshop Ban.</summary>
public sealed class WorkshopJarStoragePortAdapter : IResourcePortAdapter, IResourceCapacityDomainBoundAdapter, IResourceAccountRuntimeBoundAdapter
{
    public const string SupportedPolicyId = "workshop.jar-storage";
    public const int SupportedPolicyVersion = 1;

    private readonly Workshop _workshop;
    private readonly string _areaId;
    public RuleResourceAccount Account { get; }
    internal ResourceCapacityDomainBinding? CapacityDomainBinding { get; private set; }
    ResourceCapacityDomainBinding? IResourceCapacityDomainBoundAdapter.CapacityDomainBinding => CapacityDomainBinding;
    object AccountRuntimeStateReference => _workshop;
    internal bool IsBoundTo(Workshop workshop) => ReferenceEquals(_workshop, workshop);

    public WorkshopJarStoragePortAdapter(Workshop workshop, string areaId)
    {
        _workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        if (string.IsNullOrWhiteSpace(workshop.OwnedInstanceId))
            throw new ArgumentException("An exact Board-owned Workshop instance identity is required.", nameof(workshop));
        Account = new RuleResourceAccount("workshop.jar-storage", workshop.OwnedInstanceId);
        Account.Validate();
    }

    public WorkshopJarStoragePortAdapter(
        Workshop workshop, string areaId, OwnerIdentity workshopOwner)
    {
        _workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        if (string.IsNullOrWhiteSpace(workshop.OwnedInstanceId))
            throw new ArgumentException("An exact Board-owned Workshop instance identity is required.", nameof(workshop));
        ResourceOwnerIdentityGuard.Require(workshopOwner, ResourceOwnerKind.Workshop, workshop.OwnedInstanceId);
        Account = new RuleResourceAccount(workshopOwner, "workshop.jar-storage");
        Account.Validate();
        CapacityDomainBinding = ResourceCapacityDomainBinding.Create(workshopOwner, "workshop.jar-storage", _areaId, _workshop);
    }


    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.AccountId),
            new KeyValuePair<string, string>("capacity", _workshop.MaxJars.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("jar-ids", string.Join(",", _workshop.Jars
                .Select(jar => jar.Id.GetValueOrDefault())
                .OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture))))
        });

        if (port.AreaId != _areaId)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.area.mismatch", "Port does not belong to this Workshop Area.");
        if (port.PolicyId != SupportedPolicyId || port.PolicyVersion != SupportedPolicyVersion)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.policy.unsupported", "Workshop Jar storage policy identity/version is unsupported.");
        if (port.ResourceKind != RuleResourceKind.Jar)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.resource-kind.unsupported", "Workshop Jar storage only supports Jar resources.");
        if (port.MaxAmountPerTransfer is { } limit && amount > limit)
            return new ResourcePortPreflight(RuleDecisionKind.Rejected, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.transfer-limit.exceeded", "Requested Jar count exceeds the port transfer limit.");
        if (port.MaxAmountPerLogicalTimeMilli is not null)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.throughput.scheduler-required", "Logical-time throughput requires a scheduler window.");
        if (amount != 1)
            return new ResourcePortPreflight(RuleDecisionKind.Rejected, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "jar.identity.amount.invalid", "A Jar resource identity represents exactly one discrete Jar.");
        if (!int.TryParse(resourceIdentity, NumberStyles.None, CultureInfo.InvariantCulture, out var jarId) || jarId <= 0)
            return new ResourcePortPreflight(RuleDecisionKind.Rejected, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "jar.identity.invalid", "Jar resource identity must be a positive invariant-culture integer.");
        if (port.ResourceIdentities.Count > 0 && !port.ResourceIdentities.Contains(resourceIdentity, StringComparer.Ordinal))
            return new ResourcePortPreflight(RuleDecisionKind.Rejected, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.resource-identity.mismatch", "Jar identity is outside the port allow-list.");

        var exists = _workshop.Jars.Any(jar => jar.Id == jarId);
        var allowed = port.Direction == ResourcePortDirection.Input
            ? _workshop.HasJarCapacity() && !exists
            : exists;
        return new ResourcePortPreflight(
            allowed ? RuleDecisionKind.Allowed : RuleDecisionKind.Rejected,
            port.PortId, Account, RuleResourceKind.Jar, resourceIdentity, amount, token,
            allowed ? "allowed" : port.Direction == ResourcePortDirection.Input
                ? exists ? "jar.identity.already-owned" : "jar.storage.capacity"
                : "jar.identity.not-owned",
            allowed ? null : "Jar storage preflight rejected the requested ownership change.");
    }
}


/// <summary>Preflight adapter for the Production Area's central discrete Jar warehouse.</summary>
public sealed class WarehouseJarStoragePortAdapter : IResourcePortAdapter, IResourceCapacityDomainBoundAdapter, IResourceAccountRuntimeBoundAdapter
{
    public const string SupportedPolicyId = "production.warehouse.jar-storage";
    public const int SupportedPolicyVersion = 1;

    private readonly Warehouse _warehouse;
    private readonly string _areaId;
    public RuleResourceAccount Account { get; }
    internal ResourceCapacityDomainBinding? CapacityDomainBinding { get; private set; }
    ResourceCapacityDomainBinding? IResourceCapacityDomainBoundAdapter.CapacityDomainBinding => CapacityDomainBinding;
    object AccountRuntimeStateReference => _warehouse;
    internal bool IsBoundTo(Warehouse warehouse) => ReferenceEquals(_warehouse, warehouse);

    public WarehouseJarStoragePortAdapter(Warehouse warehouse, string productionIndividualId, string areaId)
    {
        _warehouse = warehouse ?? throw new ArgumentNullException(nameof(warehouse));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        Account = new RuleResourceAccount(
            "production.warehouse.jar-storage",
            CircuitAreaDefinition.RequireIdentity(productionIndividualId, nameof(productionIndividualId)));
        Account.Validate();
    }

    public WarehouseJarStoragePortAdapter(
        Warehouse warehouse, OwnerIdentity warehouseOwner, string areaId)
    {
        _warehouse = warehouse ?? throw new ArgumentNullException(nameof(warehouse));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        ResourceOwnerIdentityGuard.Require(warehouseOwner, ResourceOwnerKind.Warehouse);
        Account = new RuleResourceAccount(warehouseOwner, "production.warehouse.jar-storage");
        Account.Validate();
        CapacityDomainBinding = ResourceCapacityDomainBinding.Create(warehouseOwner, "production.warehouse.jar-storage", _areaId, _warehouse);
    }


    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.AccountId),
            new KeyValuePair<string, string>("capacity", Constants.WAREHOUSE_MAX_JARS.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("jar-count", _warehouse.JarCount.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("jar-ids", string.Join(",", _warehouse.Jars
                .Select(jar => jar.Id.GetValueOrDefault()).OrderBy(id => id)
                .Select(id => id.ToString(CultureInfo.InvariantCulture))))
        });

        if (port.AreaId != _areaId)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.area.mismatch", "Port does not belong to this Production Area.");
        if (port.PolicyId != SupportedPolicyId || port.PolicyVersion != SupportedPolicyVersion)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.policy.unsupported", "Warehouse Jar storage policy identity/version is unsupported.");
        if (port.ResourceKind != RuleResourceKind.Jar)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.resource-kind.unsupported", "Warehouse Jar storage only supports Jar resources.");
        if (port.MaxAmountPerTransfer is { } limit && amount > limit)
            return new ResourcePortPreflight(RuleDecisionKind.Rejected, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.transfer-limit.exceeded", "Requested Jar count exceeds the port transfer limit.");
        if (port.MaxAmountPerLogicalTimeMilli is not null)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.throughput.scheduler-required", "Logical-time throughput requires a scheduler window.");
        if (amount != 1)
            return new ResourcePortPreflight(RuleDecisionKind.Rejected, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "jar.identity.amount.invalid", "A Jar resource identity represents exactly one discrete Jar.");
        if (!int.TryParse(resourceIdentity, NumberStyles.None, CultureInfo.InvariantCulture, out var jarId) ||
            jarId <= 0 || jarId.ToString(CultureInfo.InvariantCulture) != resourceIdentity)
            return new ResourcePortPreflight(RuleDecisionKind.Rejected, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "jar.identity.invalid", "Jar resource identity must be a canonical positive integer.");
        if (port.ResourceIdentities.Count > 0 && !port.ResourceIdentities.Contains(resourceIdentity, StringComparer.Ordinal))
            return new ResourcePortPreflight(RuleDecisionKind.Rejected, port.PortId, Account,
                RuleResourceKind.Jar, resourceIdentity, amount, token,
                "port.resource-identity.mismatch", "Jar identity is outside the port allow-list.");

        var exists = _warehouse.Jars.Any(jar => jar.Id == jarId);
        var allowed = port.Direction == ResourcePortDirection.Input
            ? _warehouse.JarCount < Constants.WAREHOUSE_MAX_JARS && !exists
            : exists;
        return new ResourcePortPreflight(
            allowed ? RuleDecisionKind.Allowed : RuleDecisionKind.Rejected,
            port.PortId, Account, RuleResourceKind.Jar, resourceIdentity, amount, token,
            allowed ? "allowed" : port.Direction == ResourcePortDirection.Input
                ? exists ? "jar.identity.already-owned" : "warehouse.jar-capacity"
                : "jar.identity.not-owned",
            allowed ? null : "Warehouse Jar storage preflight rejected the requested ownership change.");
    }
}

/// <summary>Preflight adapter for the legacy Production Area warehouse liquid account; it has no Workshop Ban.</summary>
public sealed class WarehouseLiquidStoragePortAdapter : IResourcePortAdapter, IResourceCapacityDomainBoundAdapter, IResourceAccountRuntimeBoundAdapter
{
    public const string SupportedPolicyId = "production.warehouse.liquid-storage";
    public const int SupportedPolicyVersion = 1;

    private readonly Warehouse _warehouse;
    private readonly string _areaId;
    public RuleResourceAccount Account { get; }
    internal ResourceCapacityDomainBinding? CapacityDomainBinding { get; private set; }
    ResourceCapacityDomainBinding? IResourceCapacityDomainBoundAdapter.CapacityDomainBinding => CapacityDomainBinding;
    object AccountRuntimeStateReference => _warehouse;
    internal bool IsBoundTo(Warehouse warehouse) => ReferenceEquals(_warehouse, warehouse);

    public WarehouseLiquidStoragePortAdapter(Warehouse warehouse, string productionIndividualId, string areaId)
    {
        _warehouse = warehouse ?? throw new ArgumentNullException(nameof(warehouse));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        Account = new RuleResourceAccount(
            "production.warehouse.liquid-storage",
            CircuitAreaDefinition.RequireIdentity(productionIndividualId, nameof(productionIndividualId)));
        Account.Validate();
    }

    public WarehouseLiquidStoragePortAdapter(
        Warehouse warehouse, OwnerIdentity warehouseOwner, string areaId)
    {
        _warehouse = warehouse ?? throw new ArgumentNullException(nameof(warehouse));
        _areaId = CircuitAreaDefinition.RequireIdentity(areaId, nameof(areaId));
        ResourceOwnerIdentityGuard.Require(warehouseOwner, ResourceOwnerKind.Warehouse);
        Account = new RuleResourceAccount(warehouseOwner, "production.warehouse.liquid-storage");
        Account.Validate();
        CapacityDomainBinding = ResourceCapacityDomainBinding.Create(warehouseOwner, "production.warehouse.liquid-storage", _areaId, _warehouse);
    }


    public ResourcePortPreflight Preflight(CircuitPortDefinition port, string resourceIdentity, int amount)
    {
        ResourcePortPreflightFactory.ValidateRequest(resourceIdentity, amount);
        var token = ResourcePortVersionToken.Create(new[]
        {
            new KeyValuePair<string, string>("owner", Account.AccountId),
            new KeyValuePair<string, string>("capacity", Constants.WAREHOUSE_MAX_LIQUIDS.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("balances", ResourcePortVersionToken.Amounts("liquid", _warehouse.LiquidWarehouse))
        });

        if (port.AreaId != _areaId)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, amount, token,
                "port.area.mismatch", "Port does not belong to this Production Area.");
        if (port.PolicyId != SupportedPolicyId || port.PolicyVersion != SupportedPolicyVersion)
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, amount, token,
                "port.policy.unsupported", "Warehouse liquid storage policy identity/version is unsupported.");
        if (! _warehouse.LiquidWarehouse.ContainsKey(resourceIdentity))
            return new ResourcePortPreflight(RuleDecisionKind.Undetermined, port.PortId, Account,
                RuleResourceKind.Liquid, resourceIdentity, amount, token,
                "resource.identity.unknown", "Liquid identity is not part of the Warehouse vocabulary.");

        return ResourcePortPreflightFactory.Evaluate(this, port, resourceIdentity, amount, token, () =>
        {
            if (port.Direction == ResourcePortDirection.Output)
                return _warehouse.LiquidCount(resourceIdentity) >= amount
                    ? LiquidGrant.Yes()
                    : LiquidGrant.Insufficient(resourceIdentity);
            var remaining = Constants.WAREHOUSE_MAX_LIQUIDS - _warehouse.LiquidCount(resourceIdentity);
            return remaining >= amount ? LiquidGrant.Yes() : LiquidGrant.OverCapacity(resourceIdentity);
        });
    }
}

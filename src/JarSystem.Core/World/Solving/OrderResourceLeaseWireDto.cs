using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.World;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// JSON 可直接构造的订单资源租约 wire contract。
/// 仅保存资源所有权事实与结算收据，不保存 ReservationBook 的罐子/从台派生索引，也不保存运行时 Scope 引用。
/// </summary>
public sealed class OrderResourceLeaseWireDto
{
    public int DemandId { get; set; }
    public string WorldVersion { get; set; } = "";
    public List<int> LockedJarIds { get; set; } = new();
    public Dictionary<string, int> InitialLiquidSupply { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> TransferLiquidAmounts { get; set; } = new(StringComparer.Ordinal);
    public int MasterStagingPeakCapacity { get; set; }
    public int SlaveStagingPeakCapacity { get; set; }
    public bool UsesSlave { get; set; }
    public OrderResourceLeaseStage Stage { get; set; }
    public OrderResourceSettlementStage SettlementStage { get; set; }
    public string SettlementOperationId { get; set; } = "";
    public Dictionary<string, int>? SettlementRequested { get; set; }
    public Dictionary<string, int>? SettlementAccepted { get; set; }
    public Dictionary<string, int>? SettlementDiscarded { get; set; }
    public List<LiquidBatchSourceAllocationWireDto>? SettlementSourceAllocations { get; set; }

    public static OrderResourceLeaseWireDto FromLease(OrderResourceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var receipt = lease.SettlementReceipt;
        return new OrderResourceLeaseWireDto
        {
            DemandId = lease.DemandId,
            WorldVersion = lease.WorldVersion,
            LockedJarIds = lease.LockedJarIds.ToList(),
            InitialLiquidSupply = Copy(lease.InitialLiquidSupply),
            TransferLiquidAmounts = Copy(lease.TransferLiquidAmounts),
            MasterStagingPeakCapacity = lease.MasterStagingPeakCapacity,
            SlaveStagingPeakCapacity = lease.SlaveStagingPeakCapacity,
            UsesSlave = lease.UsesSlave,
            Stage = lease.Stage,
            SettlementStage = lease.SettlementStage,
            SettlementOperationId = lease.SettlementOperationId,
            SettlementRequested = receipt == null ? null : Copy(receipt.Requested),
            SettlementAccepted = receipt == null ? null : Copy(receipt.Accepted),
            SettlementDiscarded = receipt == null ? null : Copy(receipt.Discarded),
            SettlementSourceAllocations = receipt == null ? null :
                receipt.SourceAllocations.Select(LiquidBatchSourceAllocationWireDto.FromAllocation).ToList()
        };
    }

    /// <summary>深拷贝租约的纯 Wire 值，避免根级检查点重复维护租约字段映射。</summary>
    public OrderResourceLeaseWireDto DeepCopy() => new()
    {
        DemandId = DemandId,
        WorldVersion = WorldVersion,
        LockedJarIds = LockedJarIds.ToList(),
        InitialLiquidSupply = Copy(InitialLiquidSupply),
        TransferLiquidAmounts = Copy(TransferLiquidAmounts),
        MasterStagingPeakCapacity = MasterStagingPeakCapacity,
        SlaveStagingPeakCapacity = SlaveStagingPeakCapacity,
        UsesSlave = UsesSlave,
        Stage = Stage,
        SettlementStage = SettlementStage,
        SettlementOperationId = SettlementOperationId,
        SettlementRequested = SettlementRequested == null ? null : Copy(SettlementRequested),
        SettlementAccepted = SettlementAccepted == null ? null : Copy(SettlementAccepted),
        SettlementDiscarded = SettlementDiscarded == null ? null : Copy(SettlementDiscarded),
        SettlementSourceAllocations = SettlementSourceAllocations?.Select(item => item.DeepCopy()).ToList()
    };

    internal WorldResourceReservation ToReservation()
    {
        ValidateIdentityAndShape();
        return new WorldResourceReservation(
            DemandId, WorldVersion, LockedJarIds, InitialLiquidSupply,
            UsesSlave, Stage, TransferLiquidAmounts,
            MasterStagingPeakCapacity, SlaveStagingPeakCapacity);
    }

    internal LiquidBatchReceipt? ToReceipt()
    {
        ValidateIdentityAndShape();
        if (SettlementStage == OrderResourceSettlementStage.NotStarted)
            return null;
        return new LiquidBatchReceipt(
            SettlementRequested!, SettlementAccepted!, SettlementDiscarded!,
            SettlementSourceAllocations?.Select(item => item.ToAllocation()));
    }

    public void Validate()
    {
        _ = ToReservation();
        _ = ToReceipt();
    }

    private void ValidateIdentityAndShape()
    {
        if (DemandId <= 0 || string.IsNullOrWhiteSpace(WorldVersion))
            throw new InvalidOperationException("Wire 租约身份无效。");
        if (!Enum.IsDefined(Stage) || Stage == OrderResourceLeaseStage.Settled)
            throw new InvalidOperationException("Wire 租约执行阶段无效或已终结。");
        if (!Enum.IsDefined(SettlementStage))
            throw new InvalidOperationException("Wire 租约结算阶段无效。");
        if (MasterStagingPeakCapacity < 0 || SlaveStagingPeakCapacity < 0)
            throw new InvalidOperationException("Wire 租约暂存容量不能为负数。");
        if (LockedJarIds == null || InitialLiquidSupply == null || TransferLiquidAmounts == null)
            throw new InvalidOperationException("Wire 租约缺少资源集合。");
        if (LockedJarIds.Any(id => id <= 0) ||
            LockedJarIds.Distinct().Count() != LockedJarIds.Count)
            throw new InvalidOperationException("Wire 租约罐子 ID 无效或重复。");
        ShipLiquidInventory.ValidateAmounts(InitialLiquidSupply);
        ShipLiquidInventory.ValidateAmounts(TransferLiquidAmounts);
        if (!UsesSlave &&
            (TransferLiquidAmounts.Count != 0 || SlaveStagingPeakCapacity != 0 ||
             Stage is OrderResourceLeaseStage.TransferPending or
                 OrderResourceLeaseStage.TransferCommitted or OrderResourceLeaseStage.SlaveExecuting))
            throw new InvalidOperationException("A 类 Wire 租约包含从台资源或执行阶段。");
        if (UsesSlave && SlaveStagingPeakCapacity < 0)
            throw new InvalidOperationException("B 类 Wire 租约从台容量无效。");

        var expectedOperationId = WorldVersion + ":order:" + DemandId + ":settlement";
        if (!string.Equals(SettlementOperationId, expectedOperationId, StringComparison.Ordinal))
            throw new InvalidOperationException("Wire 租约结算 OperationId 与订单身份不匹配。");

        var hasReceipt = SettlementRequested != null ||
            SettlementAccepted != null || SettlementDiscarded != null ||
            SettlementSourceAllocations != null;
        if (SettlementStage == OrderResourceSettlementStage.NotStarted)
        {
            if (hasReceipt)
                throw new InvalidOperationException("未开始结算的 Wire 租约不能包含结算收据。");
            return;
        }
        if (SettlementRequested == null || SettlementAccepted == null || SettlementDiscarded == null)
            throw new InvalidOperationException("已开始结算的 Wire 租约必须包含完整结算收据。");
        ShipLiquidInventory.ValidateAmounts(SettlementRequested);
        ShipLiquidInventory.ValidateAmounts(SettlementAccepted);
        ShipLiquidInventory.ValidateAmounts(SettlementDiscarded);
        var keys = SettlementRequested.Keys.Concat(SettlementAccepted.Keys)
            .Concat(SettlementDiscarded.Keys).Distinct(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (SettlementRequested.GetValueOrDefault(key) != checked(
                SettlementAccepted.GetValueOrDefault(key) + SettlementDiscarded.GetValueOrDefault(key)))
                throw new InvalidOperationException("Wire 租约结算收据数量不守恒。");
        }
        if (SettlementSourceAllocations != null)
        {
            foreach (var allocation in SettlementSourceAllocations)
                allocation.Validate();
            _ = new LiquidBatchReceipt(
                SettlementRequested, SettlementAccepted, SettlementDiscarded,
                SettlementSourceAllocations.Select(item => item.ToAllocation()));
        }
    }

    private static Dictionary<string, int> Copy(IReadOnlyDictionary<string, int> source) =>
        source.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
}

/// <summary>包含全部活动订单资源租约的 wire DTO；所有权索引由恢复过程重新生成。</summary>
public sealed class WorldResourceReservationBookWireDto
{
    public List<OrderResourceLeaseWireDto> Leases { get; set; } = new();

    public WorldResourceReservationBookWireDto() { }

    public WorldResourceReservationBookWireDto(IEnumerable<OrderResourceLeaseWireDto> leases)
    {
        Leases = leases?.OrderBy(item => item.DemandId).ToList()
            ?? throw new ArgumentNullException(nameof(leases));
        Validate();
    }

    /// <summary>深拷贝全部租约值，并在构造边界重新校验身份与资源约束。</summary>
    public WorldResourceReservationBookWireDto DeepCopy()
    {
        if (Leases == null)
            throw new InvalidOperationException("Wire 预留簿缺少租约集合。");
        return new WorldResourceReservationBookWireDto(Leases.Select(item =>
            item?.DeepCopy() ?? throw new InvalidOperationException("Wire 预留簿包含空租约。")));
    }

    public void Validate()
    {
        if (Leases == null)
            throw new InvalidOperationException("Wire 预留簿缺少租约集合。");
        var ids = new HashSet<int>();
        foreach (var lease in Leases)
        {
            if (lease == null)
                throw new InvalidOperationException("Wire 预留簿包含空租约。");
            lease.Validate();
            if (!ids.Add(lease.DemandId))
                throw new InvalidOperationException("Wire 预留簿包含重复订单租约：" + lease.DemandId);
        }
        if (Leases.Count(item => item.UsesSlave) > 1)
            throw new InvalidOperationException("Wire 预留簿包含多个从台所有者。");
        var jarIds = Leases.SelectMany(item => item.LockedJarIds).ToArray();
        if (jarIds.Distinct().Count() != jarIds.Length)
            throw new InvalidOperationException("Wire 预留簿中的罐子被多个订单同时锁定。");
    }
}

/// <summary>Wire DTO for the exact contribution of one source account to a multi-account settlement.</summary>
public sealed class LiquidBatchSourceAllocationWireDto
{
    public ResourceEndpointWireDto SourceEndpoint { get; set; } = new();
    public ResourceEndpointWireDto TargetEndpoint { get; set; } = new();
    public ResourceEndpointWireDto DiscardEndpoint { get; set; } = new();
    public Dictionary<string, int> Requested { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Accepted { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Discarded { get; set; } = new(StringComparer.Ordinal);

    public static LiquidBatchSourceAllocationWireDto FromAllocation(LiquidBatchSourceAllocation value) => new()
    {
        SourceEndpoint = ResourceEndpointWireDto.FromEndpoint(value.SourceEndpoint),
        TargetEndpoint = ResourceEndpointWireDto.FromEndpoint(value.TargetEndpoint),
        DiscardEndpoint = ResourceEndpointWireDto.FromEndpoint(value.DiscardEndpoint),
        Requested = Copy(value.Requested),
        Accepted = Copy(value.Accepted),
        Discarded = Copy(value.Discarded)
    };

    public LiquidBatchSourceAllocationWireDto DeepCopy() => new()
    {
        SourceEndpoint = new ResourceEndpointWireDto
        {
            Kind = SourceEndpoint.Kind,
            AccountId = SourceEndpoint.AccountId,
            ExternalIdentity = SourceEndpoint.ExternalIdentity
        },
        TargetEndpoint = new ResourceEndpointWireDto
        {
            Kind = TargetEndpoint.Kind,
            AccountId = TargetEndpoint.AccountId,
            ExternalIdentity = TargetEndpoint.ExternalIdentity
        },
        DiscardEndpoint = new ResourceEndpointWireDto
        {
            Kind = DiscardEndpoint.Kind,
            AccountId = DiscardEndpoint.AccountId,
            ExternalIdentity = DiscardEndpoint.ExternalIdentity
        },
        Requested = Copy(Requested),
        Accepted = Copy(Accepted),
        Discarded = Copy(Discarded)
    };

    public void Validate()
    {
        if (SourceEndpoint == null || TargetEndpoint == null || DiscardEndpoint == null ||
            !SourceEndpoint.IsValid || !TargetEndpoint.IsValid || !DiscardEndpoint.IsValid ||
            Requested == null || Accepted == null || Discarded == null)
            throw new InvalidOperationException("Per-source settlement Wire allocation is invalid.");
        _ = ToAllocation();
    }

    public LiquidBatchSourceAllocation ToAllocation()
    {
        ValidateEndpoints();
        return new LiquidBatchSourceAllocation(
            SourceEndpoint.ToEndpoint(), TargetEndpoint.ToEndpoint(), DiscardEndpoint.ToEndpoint(),
            Requested, Accepted, Discarded);
    }

    private void ValidateEndpoints()
    {
        if (SourceEndpoint == null || TargetEndpoint == null || DiscardEndpoint == null ||
            !SourceEndpoint.IsValid || !TargetEndpoint.IsValid || !DiscardEndpoint.IsValid)
            throw new InvalidOperationException("Per-source settlement endpoint is invalid.");
    }

    private static Dictionary<string, int> Copy(IReadOnlyDictionary<string, int> source) =>
        source.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
}


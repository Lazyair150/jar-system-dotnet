using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World.Execution;

/// <summary>
/// 单只 Jar 的账户迁移端口。TransferService 负责包围调用并在异常时恢复未归属的 Jar；
/// 该端口只封装实体移动，不决定极性、Ban 或订单语义。
/// </summary>
internal interface IOrderJarTransferPort
{
    bool Move(Workshop source, Workshop destination, Jar jar);
}
internal interface IOrderTransferFaultPort
{
    void AfterJarMoves();
    void AfterLiquidTransfer();
    void BeforeLedgerPublication();
}

internal sealed class NoopOrderTransferFaultPort : IOrderTransferFaultPort
{
    public static NoopOrderTransferFaultPort Instance { get; } = new();
    private NoopOrderTransferFaultPort() { }
    public void AfterJarMoves() { }
    public void AfterLiquidTransfer() { }
    public void BeforeLedgerPublication() { }
}

internal sealed class WorkshopJarTransferPort : IOrderJarTransferPort
{
    public bool Move(Workshop source, Workshop destination, Jar jar)
    {
        if (!ReferenceEquals(source.TransferOut(jar), jar)) return false;
        return destination.TransferIn(jar);
    }
}
/// <summary>
/// B 类订单唯一的主台→从台交接服务。
/// 预检阶段只读取；CommitStarted 回调发生后，调用方必须允许失败清理回收本次从台资源。
/// </summary>
internal sealed class OrderTransferService
{
    private readonly IOrderJarTransferPort _jarTransferPort;
    private readonly IOrderTransferFaultPort _faults;

    public OrderTransferService(
        IOrderJarTransferPort? jarTransferPort = null,
        IOrderTransferFaultPort? faults = null)
    {
        _jarTransferPort = jarTransferPort ?? new WorkshopJarTransferPort();
        _faults = faults ?? NoopOrderTransferFaultPort.Instance;
    }

    public RuleTransition? CommittedTransition { get; private set; }
    /// <summary>True only after all physical postconditions and the optional ledger batch have committed.</summary>
    public bool IsCommitted { get; private set; }

    /// <summary>Restores the committed handoff boundary from an authoritative SlaveSteps session checkpoint.</summary>
    public void RestoreCommittedState() => IsCommitted = true;

    public string? Transfer(
        int demandId,
        OrderResourceLease lease,
        WorldOrderResourceManifest manifest,
        Workshop master,
        Workshop slave,
        WorldOrderExecutionAdmission admission,
        OrderLiquidScope? masterScope,
        OrderLiquidScope? slaveScope,
        ResourceLedger? ledger,
        Func<int>? ledgerTickProvider,
        Action commitStarted,
        Action commitRolledBack)
    {
        CommittedTransition = null;
        IsCommitted = false;
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(slave);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(commitStarted);
        ArgumentNullException.ThrowIfNull(commitRolledBack);
        if (!manifest.IsPartitioned || manifest.DemandId != demandId ||
            lease.DemandId != demandId || !lease.UsesSlave ||
            lease.Stage != OrderResourceLeaseStage.TransferPending ||
            !lease.LockedJarIds.ToHashSet().SetEquals(manifest.LockedJarIds) ||
            !LiquidBalancesEqual(lease.TransferLiquidAmounts, manifest.TransferLiquids) ||
            lease.MasterStagingPeakCapacity != manifest.MasterStagingPeakCapacity ||
            lease.SlaveStagingPeakCapacity != manifest.SlaveStagingPeakCapacity)
            throw new InvalidOperationException("TransferService 只接受处于交接待提交阶段且资源承诺与 B 类 Manifest 一致的租约。");

        if (slave.HasResources)
            return "B 类资源转移前，从工作台的旧版资源池必须为空";

        // Jar handoff is a pure ownership/account transfer, not a Workshop operation.
        // Do NOT gate this loop by source/target polarity or liquid Ban: an opposite-polarity
        // Jar is still a valid physical resource on either side. Operation permission is
        // evaluated later by the Workshop that actually executes the operation.
        // Validate the complete source inventory and requested set before building the move plan;
        // otherwise duplicate manifest IDs can escape as Dictionary.Add exceptions mid-preflight.
        var sourceJarIds = master.Jars.Select(jar => jar.Id ?? 0).ToArray();
        if (sourceJarIds.Any(id => id <= 0) || sourceJarIds.Distinct().Count() != sourceJarIds.Length)
            return "主台 Jar 库存包含无效或重复身份，拒绝交接";
        if (manifest.TransferJarIds.Any(id => id <= 0) ||
            manifest.TransferJarIds.Distinct().Count() != manifest.TransferJarIds.Count)
            return "B 类交接清单包含无效或重复 Jar 身份";

        var jars = new Dictionary<int, Jar>();
        foreach (var id in manifest.TransferJarIds)
        {
            var matches = master.Jars.Where(jar => jar.Id == id).ToArray();
            if (matches.Length != 1)
                return matches.Length == 0
                    ? $"转移前主台找不到罐子 #{id}"
                    : $"转移前主台存在重复罐子 ID #{id}";
            if (slave.Jars.Any(jar => jar.Id == id))
                return $"从台已存在待转移罐子 #{id}";
            jars.Add(id, matches[0]);
        }

        if (manifest.TransferJarIds.Count > slave.MaxJars - slave.Jars.Count)
            return "从台罐位不足，无法完整接收转移罐子清单";

        // Both ends of a free-liquid handoff are Workshop staging accounts.
        // A source Ban blocks extraction from Master; a destination Ban blocks admission to Slave.
        foreach (var item in manifest.TransferLiquids)
        {
            if (item.Value < 0)
                return $"转移液体「{item.Key}」数量无效";
            if (item.Value == 0) continue;
            if (master.IsBanned(item.Key))
                return $"主台禁用液体「{item.Key}」，不能将其转移给从台";
            if (slave.IsBanned(item.Key))
                return $"从台禁用液体「{item.Key}」，不能接收执行所需液体";
        }

        Dictionary<string, int>? requiredTransfer = null;
        if (masterScope == null)
        {
            foreach (var item in manifest.TransferLiquids)
            {
                if (item.Value < 0)
                    return $"转移液体「{item.Key}」数量无效";
                if (item.Value == 0) continue;
                var available = master.LiquidStorage.GetValueOrDefault(item.Key);
                if (available < item.Value)
                    return $"主台液体「{item.Key}」不足：需要 {item.Value}，当前 {available}";
                var storage = slave.CanStoreLiquid(item.Key, item.Value);
                if (!storage.Granted)
                    return storage.Reason ?? $"从台液体「{item.Key}」容量不足";
            }
        }
        else
        {
            if (slaveScope == null)
                throw new InvalidOperationException("新液体资源路径的 B 类会话缺少从台订单范围");
            var masterFinalBalances = masterScope.Balance;
            foreach (var item in masterFinalBalances)
                if (masterScope.GetAmount(item.Key) != item.Value)
                    throw new InvalidOperationException("主台订单液体快照读取不一致");
            if (!LiquidBalancesEqual(masterFinalBalances, manifest.MasterFinalLiquidBalances))
                return "主台订单液体余额与资源清单预测不一致，拒绝执行跨台泵送";
            requiredTransfer = manifest.TransferLiquids
                .Where(item => item.Value > 0)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            if (!slaveScope.Staging.CanReceiveTransferBatch(demandId, requiredTransfer))
                return "从台订单暂存区不能接收从台路径所需液体（禁令或总容量不足）";
        }

        RuleTransition? transferTransition = null;
        if (master.OwnedInstanceId != null || slave.OwnedInstanceId != null)
        {
            if (master.OwnedInstanceId == null || slave.OwnedInstanceId == null)
                return "Board-owned 主从交接必须同时提供精确的来源与目标 OwnedInstanceId";
            if (masterScope != null &&
                !string.Equals(masterScope.WorkshopInstanceId, master.OwnedInstanceId, StringComparison.Ordinal))
                return "主台订单暂存范围的 OwnedInstanceId 与当前工作台不一致";
            if (slaveScope != null &&
                !string.Equals(slaveScope.WorkshopInstanceId, slave.OwnedInstanceId, StringComparison.Ordinal))
                return "从台订单暂存范围的 OwnedInstanceId 与当前工作台不一致";

            var sourceBalances = masterScope?.Balance ?? master.LiquidStorage;
            var destinationUsedCapacity = slaveScope?.Staging.UsedCapacity ??
                slave.LiquidStorage.Values.Sum();
            var destinationCapacity = slaveScope?.Staging.Capacity ?? slave.LiquidStagingCapacity;
            try
            {
                transferTransition = WorkshopRoleTransferTransitionAdapter.Create(
                    master.OwnedInstanceId,
                    slave.OwnedInstanceId,
                    master.Jars.Select(jar => jar.Id ?? 0),
                    manifest.TransferJarIds,
                    sourceBalances,
                    manifest.TransferLiquids,
                    master.KnownLiquids.Where(master.IsBanned),
                    slave.KnownLiquids.Where(slave.IsBanned),
                    slave.Jars.Count,
                    slave.MaxJars,
                    destinationUsedCapacity,
                    destinationCapacity);
            }
            catch (ArgumentException exception)
            {
                return "主从资源交接契约预检拒绝：" + exception.Message;
            }
        }

        // tick provider 在提交前取得；它抛错时仍没有罐子、液体或租约所有权变化。
        var ledgerTick = ledger == null ? 0 :
            (ledgerTickProvider ?? throw new InvalidOperationException("资源账本缺少逻辑 tick provider。"))();
        if (ledgerTick < 0)
            return "资源账本逻辑 tick 不能为负数";
        var ledgerOperationId = lease.WorldVersion + ":order:" + demandId + ":transfer";
        var hasLedgerFacts = manifest.TransferJarIds.Count > 0 ||
            manifest.TransferLiquids.Any(item => item.Value > 0);
        // A replayed operation must be rejected before changing physical ownership.
        // Checking this after commit would force rollback against a ledger that already
        // contains the previous transfer event batch.
        if (ledger != null && hasLedgerFacts && ledger.HasRecordedOperation(ledgerOperationId))
            return "该订单交接账本操作已记录，拒绝重复提交资源转移";
        // Collect facts while moving resources, but publish them as one idempotent batch
        // only after exact ownership and RuleTransition postconditions have succeeded.
        var ledgerMutations = new List<ResourceLedgerMutation>();

        // Snapshot every authoritative participant before opening the physical commit boundary.
        // Workshop snapshots retain original Jar references; staging snapshots retain every order
        // sub-account, prefill balance and capacity reservation. The ledger is published last.
        var masterBefore = master.CreateSnapshot();
        var slaveBefore = slave.CreateSnapshot();
        var stagingBefore = admission.CaptureTransferSnapshot();

        try
        {
            // From this point the session treats the handoff as in-flight. If compensation succeeds,
            // commitRolledBack returns the session to its pre-transfer cleanup path.
            commitStarted();

            foreach (var id in manifest.TransferJarIds)
        {
            var jar = jars[id];
            try
            {
                if (!_jarTransferPort.Move(master, slave, jar))
                    throw new InvalidOperationException($"罐子 #{id} 在转移提交阶段违反预检结果");
            }
            catch (Exception transferError)
            {
                // A port may fail after source removal but before destination ownership is
                // established. Never leave a Jar in an unowned limbo between the two accounts.
                var sourceOwnsJar = master.Jars.Any(item => ReferenceEquals(item, jar));
                var destinationOwnsJar = slave.Jars.Any(item => ReferenceEquals(item, jar));
                if (sourceOwnsJar && destinationOwnsJar &&
                    !ReferenceEquals(slave.TransferOut(jar), jar))
                    throw new InvalidOperationException(
                        $"罐子 #{id} 转移失败且无法撤销重复的双端归属", transferError);
                if (!sourceOwnsJar && !destinationOwnsJar && !master.TransferIn(jar))
                    throw new InvalidOperationException(
                        $"罐子 #{id} 转移失败且无法恢复主台归属", transferError);
                throw;
            }
            if (ledger != null)
                ledgerMutations.Add(new ResourceLedgerMutation(
                    ResourceCauses.Transfer, ResourceKinds.Jar, jar.Shape, 1,
                    From: master.Name, To: slave.Name,
                    ResourceIdentity: id.ToString(CultureInfo.InvariantCulture),
                    FromAccountId: WorkshopAccountId(master, "workshop.jar-storage"),
                    ToAccountId: WorkshopAccountId(slave, "workshop.jar-storage"),
                    FromEndpoint: WorkshopAccountEndpoint(master, "workshop.jar-storage"),
                    ToEndpoint: WorkshopAccountEndpoint(slave, "workshop.jar-storage"),
                    DisplayName: jar.Shape));
        }

        _faults.AfterJarMoves();

        if (masterScope == null)
        {
            LegacyWorkshopLiquidTransfer.Execute(master, slave, manifest.TransferLiquids);
            if (ledger != null)
            {
                foreach (var item in manifest.TransferLiquids.Where(item => item.Value > 0))
                    ledgerMutations.Add(new ResourceLedgerMutation(
                        ResourceCauses.Transfer, ResourceKinds.Liquid, item.Key, item.Value,
                        From: master.Name, To: slave.Name, ResourceIdentity: item.Key,
                        FromAccountId: WorkshopAccountId(master, "workshop.free-liquid-staging"),
                        ToAccountId: WorkshopAccountId(slave, "workshop.free-liquid-staging"),
                        FromEndpoint: WorkshopAccountEndpoint(master, "workshop.free-liquid-staging"),
                        ToEndpoint: WorkshopAccountEndpoint(slave, "workshop.free-liquid-staging"),
                        DisplayName: item.Key));
            }
        }
        else
        {
            if (!OrderLiquidTransfer.TryExecute(
                demandId, masterScope.Staging, slaveScope!.Staging,
                requiredTransfer!, out var transferReason))
                throw new InvalidOperationException(
                    transferReason ?? "主从订单液体交接在提交阶段违反预检结果");
            foreach (var item in requiredTransfer!)
                if (ledger != null)
                    ledgerMutations.Add(new ResourceLedgerMutation(
                        ResourceCauses.Transfer, ResourceKinds.Liquid, item.Key, item.Value,
                        From: master.Name, To: slave.Name, ResourceIdentity: item.Key,
                        FromAccountId: WorkshopOrderAccountId(master, demandId),
                        ToAccountId: WorkshopOrderAccountId(slave, demandId),
                        FromEndpoint: WorkshopOrderAccountEndpoint(master, demandId),
                        ToEndpoint: WorkshopOrderAccountEndpoint(slave, demandId),
                        DisplayName: item.Key));
            masterScope.Staging.ReleaseOrderCapacity(demandId);
        }

        _faults.AfterLiquidTransfer();

        // Verify exact physical ownership after commit, not merely aggregate counts.
        // Equal counts can conceal a wrong Jar being moved or an unexpected substitution.
        var requestedJarIds = manifest.TransferJarIds.ToHashSet();
        var destinationJarIds = slave.Jars.Select(jar => jar.Id ?? 0).ToArray();
        if (master.Jars.Any(jar => jar.Id is { } id && requestedJarIds.Contains(id)) ||
            destinationJarIds.Any(id => id <= 0) ||
            destinationJarIds.Distinct().Count() != destinationJarIds.Length ||
            !destinationJarIds.ToHashSet().SetEquals(requestedJarIds))
            throw new InvalidOperationException("Jar 交接提交后的主从精确身份归属与 Manifest 不一致。");

        if (transferTransition != null)
        {
            var expected = transferTransition.After.Values;
            var destinationUsedCapacity = slaveScope?.Staging.UsedCapacity ??
                slave.LiquidStorage.Values.Sum();
            if (expected["sourceJarCount"] != master.Jars.Count.ToString(CultureInfo.InvariantCulture) ||
                expected["destinationJarCount"] != slave.Jars.Count.ToString(CultureInfo.InvariantCulture) ||
                expected["destinationStagingUsedCapacity"] != destinationUsedCapacity.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("主从交接提交后的真实账户状态与同一事务构造的 RuleTransition 不一致。");
        }

        // Lease state is part of this physical transaction. Publish it before the ledger so
        // the idempotent batch is the final durable write; compensation restores TransferPending.
        lease.MarkTransferCommitted();
        lease.MarkSlaveExecuting();
        _faults.BeforeLedgerPublication();

        if (ledger != null && ledgerMutations.Count > 0 &&
            !ledger.RecordBatchOnce(ledgerOperationId, ledgerTick, ledgerMutations))
            throw new InvalidOperationException("该订单交接账本操作已记录，拒绝重复提交资源转移事实。");

        CommittedTransition = transferTransition;
        IsCommitted = true;
        return null;
        }
        catch (Exception transferError)
        {
            try
            {
                master.RestoreFromSnapshot(masterBefore);
                slave.RestoreFromSnapshot(slaveBefore);
                admission.RestoreTransferSnapshot(stagingBefore);
                lease.RestoreTransferPendingAfterCompensation();
                commitRolledBack();
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "B 类主从交接失败，且工作台/订单暂存/租约状态全量补偿失败；必须冻结世界并保留故障现场。",
                    transferError, rollbackError);
            }

            return "B 类主从交接失败，全部物理账户已补偿恢复：" + transferError.Message;
        }
    }

    private static string? WorkshopAccountId(Workshop workshop, string accountKind)
    {
        if (workshop.OwnerIdentity is { } owner)
            return new RuleResourceAccount(owner, accountKind).AccountId;
        if (string.IsNullOrWhiteSpace(workshop.OwnedInstanceId)) return null;
        return new RuleResourceAccount(accountKind, workshop.OwnedInstanceId).AccountId;
    }

    private static ResourceEndpoint WorkshopAccountEndpoint(Workshop workshop, string accountKind)
    {
        var accountId = WorkshopAccountId(workshop, accountKind);
        return accountId == null
            ? ResourceEndpoint.LegacyUnknown
            : ResourceEndpoint.ForAccount(accountId);
    }

    private static string? WorkshopOrderAccountId(Workshop workshop, int demandId)
    {
        if (workshop.OwnerIdentity is { } owner)
        {
            var orderOwner = ResourceOwnerIdentityGuard.ForOrderStaging(owner, demandId);
            return new RuleResourceAccount(orderOwner, "workshop.order-liquid-staging").AccountId;
        }
        if (string.IsNullOrWhiteSpace(workshop.OwnedInstanceId)) return null;
        var ownerId = workshop.OwnedInstanceId + ":order:" +
            demandId.ToString(CultureInfo.InvariantCulture);
        return new RuleResourceAccount("workshop.order-liquid-staging", ownerId).AccountId;
    }

    private static ResourceEndpoint WorkshopOrderAccountEndpoint(Workshop workshop, int demandId)
    {
        var accountId = WorkshopOrderAccountId(workshop, demandId);
        return accountId == null
            ? ResourceEndpoint.LegacyUnknown
            : ResourceEndpoint.ForAccount(accountId);
    }

    private static bool LiquidBalancesEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        if (left.Count != right.Count) return false;
        return left.All(item => right.TryGetValue(item.Key, out var value) && value == item.Value);
    }
}

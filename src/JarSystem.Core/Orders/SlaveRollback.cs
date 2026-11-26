using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;

namespace JarSystem.Core.Orders;

/// <summary>
/// **从台 → 主台的资源回落**（原 <c>OrderEvent.RollbackSlaveToMaster</c>，2026-09 抽出）。
///
/// 从台是 B 类订单临时升起的工具位，不是第二仓库。订单结束后，从台必须无资源残留：
/// <list type="bullet">
/// <item>交付罐先移除，其余罐子全部回落；包括订单未交付的工具罐；</item>
/// <item>从台自由液体全部回落；包括工具罐倒出后未消耗的液体，不能只按“转入液体”账目返还；</item>
/// <item>**禁令边界**：满罐中的液体不受主台禁令影响，原样随罐返回；游离液若主台禁收则丢弃；</item>
/// <item>**容量与丢弃**：主台接不下的罐子/液体**直接丢弃**（仓库单向流动，不回收），并留下日志。</item>
/// </list>
/// 该操作对成功、失败、取消三条收尾路径一致，并由过程互斥保证从台只属于一个 B 类订单。
/// </summary>
internal interface ISlaveRollbackFaultPort
{
    void AfterJarMoves();
    void AfterLiquidMoves();
    void BeforeLedgerPublication();
    void BeforeCompensation();
    void AfterLedgerPublication();
}

internal sealed class NoopSlaveRollbackFaultPort : ISlaveRollbackFaultPort
{
    public static NoopSlaveRollbackFaultPort Instance { get; } = new();
    private NoopSlaveRollbackFaultPort() { }
    public void AfterJarMoves() { }
    public void AfterLiquidMoves() { }
    public void BeforeLedgerPublication() { }
    public void BeforeCompensation() { }
    public void AfterLedgerPublication() { }
}

/// <summary>
/// Rollback could not finish as one committed physical + ledger operation.
/// The caller must preserve the session/lease and freeze the owning runtime.
/// </summary>
internal sealed class SlaveRollbackTransactionException : AggregateException
{
    public SlaveRollbackTransactionException(string message, Exception cause)
        : base(message, cause) { }

    public SlaveRollbackTransactionException(string message, Exception cause, Exception compensationError)
        : base(message, cause, compensationError) { }
}

/// <summary>
/// **从台 → 主台的资源回落**。Jar、游离液体、账本批次作为一个同步事务提交。
/// 任何提交前异常都会恢复主从工作台原快照；无法恢复或提交后的投影回调失败时，
/// 抛出 SlaveRollbackTransactionException，由宿主冻结世界并保留会话现场。
/// </summary>
internal sealed class SlaveRollback
{
    private static ResourceEndpoint WorkshopAccountEndpoint(Workshop workshop, string accountKind)
    {
        if (workshop.OwnerIdentity is { } owner)
            return ResourceEndpoint.ForAccount(new RuleResourceAccount(owner, accountKind).AccountId);
        if (string.IsNullOrWhiteSpace(workshop.OwnedInstanceId))
            return ResourceEndpoint.LegacyUnknown;
        return ResourceEndpoint.ForAccount(new RuleResourceAccount(accountKind, workshop.OwnedInstanceId).AccountId);
    }

    private static string? AccountId(ResourceEndpoint endpoint) =>
        endpoint.Kind == ResourceEndpointKind.Account ? endpoint.AccountId : null;

    public void Execute(
        Workshop master,
        Workshop slave,
        Action<ExecutionLogEntry> appendLog,
        Action<int>? countDiscardedJars = null,
        Action<string, int>? countDiscardedLiquids = null,
        ResourceLedger? ledger = null,
        Func<int>? tickOf = null,
        int? ledgerTickOverride = null,
        string? operationId = null,
        ISlaveRollbackFaultPort? faults = null,
        IReadOnlyCollection<int>? deliveredJarIds = null)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(slave);
        ArgumentNullException.ThrowIfNull(appendLog);
        if (ReferenceEquals(master, slave)) return;

        var slaveHasResources = slave.HasResources;
        if (ledger != null)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException(
                    "带资源账本的从台回落必须提供持久化 OperationId。",
                    nameof(operationId));
            if (ledger.HasRecordedOperation(operationId))
            {
                if (!slaveHasResources) return;
                throw new SlaveRollbackTransactionException(
                    "从台仍持有资源，但对应的回落 OperationId 已在账本提交；禁止重复扣动或猜测补偿。",
                    new InvalidOperationException("Rollback OperationId already exists while slave still owns resources."));
            }
        }
        if (!slaveHasResources) return;

        var ledgerTick = ledger == null
            ? 0
            : ledgerTickOverride ?? tickOf?.Invoke()
                ?? throw new InvalidOperationException("从台回落账本缺少逻辑 tick。");
        if (ledgerTick < 0)
            throw new ArgumentOutOfRangeException(nameof(ledgerTick));

        var slaveJars = slave.Jars.ToArray();
        var sourceJarIds = slaveJars.Select(jar => jar.Id ?? 0).ToArray();
        var targetJarIdsBefore = master.Jars.Select(jar => jar.Id ?? 0).ToArray();
        var deliveryIds = (deliveredJarIds ?? Array.Empty<int>()).ToArray();
        var deliveryIdSet = deliveryIds.ToHashSet();
        if (sourceJarIds.Any(id => id <= 0) ||
            sourceJarIds.Distinct().Count() != sourceJarIds.Length ||
            targetJarIdsBefore.Any(id => id <= 0) ||
            targetJarIdsBefore.Distinct().Count() != targetJarIdsBefore.Length ||
            sourceJarIds.Intersect(targetJarIdsBefore).Any() ||
            deliveryIds.Any(id => id <= 0) ||
            deliveryIdSet.Count != deliveryIds.Length ||
            !deliveryIdSet.IsSubsetOf(sourceJarIds))
            throw new DomainValidationException("从台回落前主从 Jar 身份或交付 Jar 清单无效、重复或不属于从台。");

        var masterBefore = master.CreateSnapshot();
        var slaveBefore = slave.CreateSnapshot();
        var expectedMasterJarIds = targetJarIdsBefore.ToHashSet();
        var expectedMasterLiquids = new Dictionary<string, int>(master.LiquidStorage, StringComparer.Ordinal);
        var ledgerMutations = new List<ResourceLedgerMutation>();
        var pendingLogs = new List<ExecutionLogEntry>();
        var discardedJarCount = 0;
        var discardedLiquidCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var faultPort = faults ?? NoopSlaveRollbackFaultPort.Instance;

        try
        {
            foreach (var jar in slaveJars)
            {
                var jarId = jar.Id!.Value;
                var source = WorkshopAccountEndpoint(slave, "workshop.jar-storage");

                if (deliveryIdSet.Contains(jarId))
                {
                    var clear = slave.ClearMark(jar);
                    if (!clear.Success)
                        throw new InvalidOperationException(
                            clear.Reason ?? $"无法清理交付罐 #{jarId} 的操作标记");
                    if (!ReferenceEquals(slave.TransferOut(jar), jar))
                        throw new InvalidOperationException($"无法从从台精确移出交付罐 #{jarId}。");
                    ledgerMutations.Add(new ResourceLedgerMutation(
                        ResourceCauses.Transfer, ResourceKinds.Jar, jar.Shape, 1,
                        From: slave.Name, To: "订单交付",
                        ResourceIdentity: jarId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        FromAccountId: AccountId(source), FromEndpoint: source,
                        ToEndpoint: ResourceEndpoint.ExternalSink("order.delivery"),
                        DisplayName: jar.Shape));
                    continue;
                }

                if (!ReferenceEquals(slave.TransferOut(jar), jar))
                    throw new InvalidOperationException($"无法从从台精确移出罐子 #{jarId}。");

                if (master.TransferIn(jar))
                {
                    expectedMasterJarIds.Add(jarId);
                    var target = WorkshopAccountEndpoint(master, "workshop.jar-storage");
                    ledgerMutations.Add(new ResourceLedgerMutation(
                        ResourceCauses.Return, ResourceKinds.Jar, jar.Shape, 1,
                        From: slave.Name, To: master.Name,
                        ResourceIdentity: jarId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        FromAccountId: AccountId(source), ToAccountId: AccountId(target),
                        FromEndpoint: source, ToEndpoint: target,
                        DisplayName: jar.Shape));
                }
                else
                {
                    discardedJarCount++;
                    ledgerMutations.Add(new ResourceLedgerMutation(
                        ResourceCauses.Discard, ResourceKinds.Jar, jar.Shape, 1,
                        From: slave.Name, To: null, Reason: "主台罐子容量满",
                        ResourceIdentity: jarId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        FromAccountId: AccountId(source), FromEndpoint: source,
                        ToEndpoint: ResourceEndpoint.ExternalSink("workshop.jar-storage.capacity-overflow"),
                        DisplayName: jar.Shape));
                    pendingLogs.Add(new ExecutionLogEntry
                    {
                        Phase = "rollback",
                        Command = "discard_jar",
                        Jar = jar.GetDescription(),
                        Reason = "主台罐子容量满，丢弃（仓库单向流动，不回收）"
                    });
                }
            }

            faultPort.AfterJarMoves();

            foreach (var item in slave.LiquidStorage.Where(item => item.Value > 0).ToArray())
            {
                if (!slave.RemoveLiquid(item.Key, item.Value))
                    throw new InvalidOperationException("从台游离液体库存变化与回落计划不一致：" + item.Key);

                var source = WorkshopAccountEndpoint(slave, "workshop.free-liquid-staging");
                var target = WorkshopAccountEndpoint(master, "workshop.free-liquid-staging");
                var accepted = master.IsBanned(item.Key)
                    ? 0
                    : master.TryReceiveLiquid(item.Key, item.Value);
                var remainder = item.Value - accepted;

                if (accepted > 0)
                    ledgerMutations.Add(new ResourceLedgerMutation(
                        ResourceCauses.Return, ResourceKinds.Liquid, item.Key, accepted,
                        From: slave.Name, To: master.Name,
                        ResourceIdentity: item.Key,
                        FromAccountId: AccountId(source), ToAccountId: AccountId(target),
                        FromEndpoint: source, ToEndpoint: target,
                        DisplayName: item.Key));

                if (remainder > 0)
                {
                    discardedLiquidCounts[item.Key] = checked(
                        discardedLiquidCounts.GetValueOrDefault(item.Key) + remainder);
                    var rejectedByBan = master.IsBanned(item.Key);
                    ledgerMutations.Add(new ResourceLedgerMutation(
                        ResourceCauses.Discard, ResourceKinds.Liquid, item.Key, remainder,
                        From: slave.Name, To: null,
                        Reason: rejectedByBan ? "主台禁" + item.Key : "主台液体容量满",
                        ResourceIdentity: item.Key,
                        FromAccountId: AccountId(source), FromEndpoint: source,
                        ToEndpoint: ResourceEndpoint.ExternalSink(rejectedByBan
                            ? "workshop.free-liquid-staging.ban-rejection"
                            : "workshop.free-liquid-staging.capacity-overflow"),
                        DisplayName: item.Key));
                    pendingLogs.Add(new ExecutionLogEntry
                    {
                        Phase = "rollback",
                        Command = "discard_liquid",
                        Reason = (rejectedByBan ? "主台禁" + item.Key : "主台液体容量满") +
                            "，" + item.Key + "×" + remainder + " 丢弃（仓库单向流动，不回收）"
                    });
                }

                if (accepted > 0)
                    expectedMasterLiquids[item.Key] = checked(
                        expectedMasterLiquids.GetValueOrDefault(item.Key) + accepted);
            }

            faultPort.AfterLiquidMoves();

            var actualMasterJarIds = master.Jars.Select(jar => jar.Id ?? 0).ToArray();
            if (slave.HasResources || actualMasterJarIds.Any(id => id <= 0) ||
                actualMasterJarIds.Distinct().Count() != actualMasterJarIds.Length ||
                !actualMasterJarIds.ToHashSet().SetEquals(expectedMasterJarIds) ||
                expectedMasterLiquids.Any(item => master.LiquidStorage.GetValueOrDefault(item.Key) != item.Value) ||
                master.LiquidStorage.Any(item => !expectedMasterLiquids.ContainsKey(item.Key) && item.Value > 0))
                throw new InvalidOperationException("从台回落提交后的主从物理账户状态与资源计划不一致。");

            faultPort.BeforeLedgerPublication();
            if (ledger != null && ledgerMutations.Count > 0 &&
                !ledger.RecordBatchOnce(operationId!, ledgerTick, ledgerMutations))
                throw new InvalidOperationException("从台回落账本 OperationId 已被占用，拒绝重复提交。");
        }
        catch (Exception rollbackError)
        {
            try
            {
                faultPort.BeforeCompensation();
                master.RestoreFromSnapshot(masterBefore);
                slave.RestoreFromSnapshot(slaveBefore);
            }
            catch (Exception compensationError)
            {
                throw new SlaveRollbackTransactionException(
                    "从台回落失败，且主从工作台物理账户全量补偿失败；必须冻结世界并保留故障现场。",
                    rollbackError, compensationError);
            }

            throw new SlaveRollbackTransactionException(
                "从台回落失败；主从工作台已完整恢复，账本批次未提交，必须保留订单会话以供重试或人工处理。",
                rollbackError);
        }

        try
        {
            faultPort.AfterLedgerPublication();
            foreach (var entry in pendingLogs) appendLog(entry);
            if (discardedJarCount > 0) countDiscardedJars?.Invoke(discardedJarCount);
            foreach (var item in discardedLiquidCounts)
                countDiscardedLiquids?.Invoke(item.Key, item.Value);
        }
        catch (Exception publicationError)
        {
            throw new SlaveRollbackTransactionException(
                "从台回落物理账户与账本批次已提交，但日志/统计投影发布失败；必须冻结世界并保留提交现场。",
                publicationError);
        }
    }
}

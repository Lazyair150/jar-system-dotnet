using System;
using System.Collections.Generic;
using JarSystem.Core.Production;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 从台 → 主台的回滚（<see cref="SlaveRollback"/>）的契约测试。
///
/// 为什么要有这一组：`规则.txt:26-28` 写着「B 类：主台预处理 → 选择性转移（只转移交付罐 + 从台阶段
/// 计划使用的工具罐）→ 从台继续 → 从台交付」「未交付的转移罐子与未用液体在订单结束后回流主台」，
/// `:22-23` 又写着「**禁液令不影响满罐转移**」。而 2026-11 之前
/// `grep Deliver|RollbackSlaveToMaster test/` **零命中** —— 这一层同样只有文档和实现，没有守门人。
///
/// 注意：`SlaveRollback` 是 internal，本组测试靠 `InternalsVisibleTo` 直接调它。
/// 从台是临时工具位，收尾必须排空；这里既覆盖普通转移物，也覆盖路径执行后
/// 才产生的游离液体，防止实现误把“转入账目”当成“从台剩余物全集”。
/// </summary>
public class DeliveryRollbackTests
{
    private static Workshop Bench(string name, int maxJars = 6, int maxLiquids = 30, params string[] ban) =>
        new(name, new List<string>(), new List<string>(), ban.ToList(), maxJars: maxJars, maxLiquids: maxLiquids);

    private static Jar MakeJar(int id, string shape, string? liquid = null) => new(shape, liquid, id);

    private static List<ExecutionLogEntry> Run(
        Workshop master, Workshop slave,
        System.Action<int>? countDiscardedJars = null,
        System.Action<string, int>? countDiscardedLiquids = null)
    {
        var log = new List<ExecutionLogEntry>();
        new SlaveRollback().Execute(master, slave, log.Add,
            countDiscardedJars, countDiscardedLiquids);
        return log;
    }

    private sealed class ThrowAfterLiquidMoves : ISlaveRollbackFaultPort
    {
        public void AfterJarMoves() { }
        public void AfterLiquidMoves() =>
            throw new InvalidOperationException("Injected SlaveRollback failure after liquid movement");
        public void BeforeLedgerPublication() { }
        public void BeforeCompensation() { }
        public void AfterLedgerPublication() { }
    }


    [Fact]
    public void RollbackRestoresBothWorkshopAccountsAndLeavesLedgerUntouchedWhenPrecommitFails()
    {
        var worldId = new WorldInstanceId("rollback-atomic-world");
        var masterOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "rollback-master");
        var slaveOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "rollback-slave");
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 3, maxLiquids: 4, ownedInstanceId: "rollback-master",
            ownerIdentity: masterOwner);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 3, maxLiquids: 4, ownedInstanceId: "rollback-slave",
            ownerIdentity: slaveOwner);
        var originalMasterJar = MakeJar(21, "方罐");
        var returningJar = MakeJar(22, "圆罐", "酒");
        var deliveredJar = MakeJar(23, "方罐", "蜜");
        Assert.True(master.AddJar(originalMasterJar));
        Assert.True(master.AddLiquid("蜜", 1));
        Assert.True(slave.AddJar(returningJar));
        Assert.True(slave.AddJar(deliveredJar));
        Assert.True(slave.AddLiquid("酒", 2));

        var ledger = new ResourceLedger();
        var log = new List<ExecutionLogEntry>();
        var discardedJars = 0;
        var discardedLiquids = 0;

        var error = Assert.Throws<SlaveRollbackTransactionException>(() =>
            new SlaveRollback().Execute(
                master, slave, log.Add,
                count => discardedJars += count,
                (_, amount) => discardedLiquids += amount,
                ledger, () => 23,
                operationId: "rollback:atomic:21",
                faults: new ThrowAfterLiquidMoves(),
                deliveredJarIds: new[] { deliveredJar.Id!.Value }));

        Assert.Contains("Injected SlaveRollback failure", error.InnerException!.Message);
        Assert.Same(originalMasterJar, Assert.Single(master.Jars));
        Assert.Contains(slave.Jars, jar => ReferenceEquals(jar, returningJar));
        Assert.Contains(slave.Jars, jar => ReferenceEquals(jar, deliveredJar));
        Assert.Equal(2, slave.Jars.Count);
        Assert.Equal(1, master.LiquidStorage["蜜"]);
        Assert.Equal(0, master.LiquidStorage["酒"]);
        Assert.Equal(2, slave.LiquidStorage["酒"]);
        Assert.Empty(log);
        Assert.Equal(0, discardedJars);
        Assert.Equal(0, discardedLiquids);
        Assert.Empty(ledger.Entries);
        Assert.False(ledger.HasRecordedOperation("rollback:atomic:21"));
    }

    [Fact]
    public void RollbackPublishesOneIdempotentBatchForJarLiquidReturnAndDiscard()
    {
        var worldId = new WorldInstanceId("rollback-ledger-world");
        var masterOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "ledger-master");
        var slaveOwner = new OwnerIdentity(worldId, ResourceOwnerKind.Workshop, "ledger-slave");
        var master = new Workshop("主台", new List<string> { "圆罐", "方罐" },
            maxJars: 2, maxLiquids: 2, ownedInstanceId: "ledger-master",
            ownerIdentity: masterOwner);
        var slave = new Workshop("从台", new List<string> { "圆罐", "方罐" },
            maxJars: 3, maxLiquids: 4, ownedInstanceId: "ledger-slave",
            ownerIdentity: slaveOwner);
        Assert.True(master.AddJar(MakeJar(31, "圆罐")));
        var returnedJar = MakeJar(32, "方罐");
        var discardedJar = MakeJar(33, "方罐");
        Assert.True(slave.AddJar(returnedJar));
        Assert.True(slave.AddJar(discardedJar));
        Assert.True(slave.AddLiquid("蜜", 3));

        var ledger = new ResourceLedger();
        var log = new List<ExecutionLogEntry>();
        var discardedJarCount = 0;
        var discardedLiquidCount = 0;
        const string operationId = "rollback:ledger:31";

        new SlaveRollback().Execute(
            master, slave, log.Add,
            count => discardedJarCount += count,
            (_, amount) => discardedLiquidCount += amount,
            ledger, () => 29,
            operationId: operationId);

        Assert.Empty(slave.Jars);
        Assert.DoesNotContain(slave.LiquidStorage, item => item.Value > 0);
        Assert.Equal(new[] { 31, 32 }, master.Jars.Select(item => item.Id!.Value).OrderBy(id => id));
        Assert.Equal(2, master.LiquidStorage["蜜"]);
        Assert.Equal(1, discardedJarCount);
        Assert.Equal(1, discardedLiquidCount);
        Assert.Equal(2, log.Count);

        var batch = ledger.Entries;
        Assert.Equal(4, batch.Count);
        Assert.All(batch, entry => Assert.Equal(operationId, entry.OperationId));
        Assert.All(batch, entry => Assert.Equal(29, entry.Tick));
        Assert.Equal(2, batch.Count(entry => entry.Cause == ResourceCauses.Return));
        Assert.Equal(2, batch.Count(entry => entry.Cause == ResourceCauses.Discard));

        var jarReturn = Assert.Single(batch, entry =>
            entry.Cause == ResourceCauses.Return && entry.Kind == ResourceKinds.Jar);
        var jarSource = AccountIdentity.ParseV2(jarReturn.FromEndpoint.AccountId!);
        var jarTarget = AccountIdentity.ParseV2(jarReturn.ToEndpoint.AccountId!);
        Assert.Equal(slaveOwner, jarSource.Owner);
        Assert.Equal(masterOwner, jarTarget.Owner);
        Assert.Equal("workshop.jar-storage", jarSource.AccountKind);
        Assert.Equal("workshop.jar-storage", jarTarget.AccountKind);

        var liquidReturn = Assert.Single(batch, entry =>
            entry.Cause == ResourceCauses.Return && entry.Kind == ResourceKinds.Liquid);
        var liquidSource = AccountIdentity.ParseV2(liquidReturn.FromEndpoint.AccountId!);
        var liquidTarget = AccountIdentity.ParseV2(liquidReturn.ToEndpoint.AccountId!);
        Assert.Equal(slaveOwner, liquidSource.Owner);
        Assert.Equal(masterOwner, liquidTarget.Owner);
        Assert.Equal("workshop.free-liquid-staging", liquidSource.AccountKind);
        Assert.Equal("workshop.free-liquid-staging", liquidTarget.AccountKind);

        var seqAfterCommit = ledger.LastSeq;
        var logCountAfterCommit = log.Count;
        new SlaveRollback().Execute(
            master, slave, log.Add,
            count => discardedJarCount += count,
            (_, amount) => discardedLiquidCount += amount,
            ledger, () => 29,
            operationId: operationId);

        Assert.Equal(seqAfterCommit, ledger.LastSeq);
        Assert.Equal(logCountAfterCommit, log.Count);
        Assert.Equal(1, discardedJarCount);
        Assert.Equal(1, discardedLiquidCount);
    }

    [Fact]
    public void LegacyOrderEventDeliversSelectedJarInsideSlaveRollbackLedgerBatch()
    {
        var master = Bench("主台");
        var slave = Bench("从台");
        var deliveredJar = MakeJar(41, "圆罐", "酒");
        var returningJar = MakeJar(42, "方罐");
        Assert.True(slave.AddJar(deliveredJar));
        Assert.True(slave.AddJar(returningJar));
        Assert.True(slave.MarkJar(deliveredJar).Success);

        var order = new Order(
            new List<Requirement> { new() { Shape = "圆罐", Liquid = "酒" } },
            Constants.OrderType.B,
            vocabulary: Vocabulary.Default);
        var ledger = new ResourceLedger();
        var orderEvent = new OrderEvent(order, master, slave, ledger: ledger, tickOf: () => 61);
        Assert.True(orderEvent.Start());
        Assert.True(orderEvent.AdvanceToSlavePhase().Success);

        var result = orderEvent.Deliver();

        Assert.True(result.Success, result.Reason);
        Assert.Equal(Constants.OrderStatus.SUCCESS, order.Status);
        Assert.Same(deliveredJar, Assert.Single(orderEvent.DeliveredJars));
        Assert.Empty(slave.Jars);
        Assert.Same(returningJar, Assert.Single(master.Jars));

        var delivered = Assert.Single(ledger.For(ResourceCauses.Transfer),
            item => item.Kind == ResourceKinds.Jar &&
                item.ToEndpoint.Kind == ResourceEndpointKind.ExternalSink &&
                item.ToEndpoint.ExternalIdentity == "order.delivery");
        Assert.Equal("41", delivered.ResourceIdentity);
        Assert.Equal("订单交付", delivered.To);
        var returned = Assert.Single(ledger.For(ResourceCauses.Return),
            item => item.Kind == ResourceKinds.Jar);
        Assert.Equal("42", returned.ResourceIdentity);
        Assert.Equal(61, delivered.Tick);
        Assert.Equal(61, returned.Tick);
    }

    [Fact]
    public void TransferredJarsGoBackToTheMaster()
    {
        var master = Bench("主台");
        var slave = Bench("从台");
        var jar = MakeJar(7, "圆罐");
        slave.AddJar(jar);

        var log = Run(master, slave);

        Assert.Empty(slave.Jars);
        Assert.Single(master.Jars);
        Assert.Equal(7, master.Jars[0].Id);
        Assert.Empty(log);                                  // 接得下就没有丢弃日志
    }

    [Fact]
    public void EveryRemainingJarReturnsToTheMaster()
    {
        var master = Bench("主台");
        var slave = Bench("从台");
        slave.AddJar(MakeJar(9, "方罐"));

        var log = Run(master, slave);

        Assert.Empty(slave.Jars);
        Assert.Single(master.Jars);
        Assert.Equal(9, master.Jars[0].Id);
        Assert.Empty(log);
    }

    [Fact]
    public void FullMasterDiscardsTheJarAndLogsIt()
    {
        var master = Bench("主台", maxJars: 1);
        master.AddJar(MakeJar(1, "圆罐"));                  // 主台只剩 0 个罐位
        var slave = Bench("从台");
        slave.AddJar(MakeJar(7, "高罐"));

        var discarded = 0;
        var log = Run(master, slave,
            countDiscardedJars: count => discarded += count);

        Assert.Single(master.Jars);                         // 没挤进去
        Assert.Empty(slave.Jars);                  // 已离开从台
        var discard = Assert.Single(log);
        Assert.Equal("discard_jar", discard.Command);
        Assert.Contains("不回收", discard.Reason);
        Assert.Equal(1, discarded);
    }

    [Fact]
    public void BannedLiquidOnAFullJarStillTravels()
    {
        // 规则.txt:22-23 禁液令不影响满罐转移：装被禁液体的罐子原样归还，不清空罐内液体
        var master = Bench("主台", ban: "酒");
        var slave = Bench("从台");
        var jar = MakeJar(7, "圆罐", "酒");
        slave.AddJar(jar);

        Run(master, slave);

        Assert.Single(master.Jars);
        Assert.Equal("酒", master.Jars[0].Liquid);          // 液体没被清掉
    }

    [Fact]
    public void BannedFreeLiquidIsDiscardedInsteadOfReturningToTheMaster()
    {
        // 禁令作用于从台回落的游离液体；与满罐液体随罐原样返回的规则不同。
        var master = Bench("主台", maxLiquids: 10, ban: "酒");
        var slave = Bench("从台");
        slave.AddLiquid("酒", 3);

        var discarded = 0;
        var log = Run(master, slave,
            countDiscardedLiquids: (_, amount) => discarded += amount);

        Assert.Equal(0, master.LiquidStorage["酒"]);
        Assert.Equal(0, slave.LiquidStorage["酒"]);
        var discard = Assert.Single(log);
        Assert.Equal("discard_liquid", discard.Command);
        Assert.Contains("主台禁酒", discard.Reason);
        Assert.Equal(3, discarded);
    }

    [Fact]
    public void EveryRemainingLiquidPartiallyFillsAndDiscardsTheRemainder()
    {
        var master = Bench("主台", maxLiquids: 1);
        var slave = Bench("从台");
        slave.AddLiquid("蜜", 3);

        var discarded = 0;
        var log = Run(master, slave,
            countDiscardedLiquids: (_, amount) => discarded += amount);

        Assert.Equal(1, master.LiquidStorage["蜜"]);         // 部分填充
        Assert.Equal(0, slave.LiquidStorage["蜜"]);          // 从台清空
        var discard = Assert.Single(log);
        Assert.Equal("discard_liquid", discard.Command);
        Assert.Contains("不回收", discard.Reason);
        Assert.Equal(2, discarded);
    }
}

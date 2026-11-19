using System;
using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorkshopReplacementTests
{
    private sealed class NoOrderInterruption : IOrderInterruptionPort
    {
        public int Calls { get; private set; }

        public (bool Success, string? Error) InterruptForWorkshopReplacement()
        {
            Calls++;
            return (true, null);
        }
    }

    [Fact]
    public void RosterRejectsOneWorkshopHoldingBothRoles()
    {
        var master = new Workshop("主台");

        Assert.Throws<ArgumentException>(() =>
            new WorkshopRoster(new[] { master }, master, master));
    }

    [Fact]
    public void SlaveReplacementCommitsOnlyAfterItsLogicalDuration()
    {
        var master = new Workshop("主台");
        var oldSlave = new Workshop("旧从台");
        var spare = new Workshop("备用台");
        var roster = new WorkshopRoster(new[] { master, oldSlave, spare }, master, oldSlave);
        var interruption = new NoOrderInterruption();
        var process = new WorkshopReplacementProcess(
            roster, WorkshopRole.Slave, spare, interruption, phaseDurationMilli: 1_000);

        Assert.True(process.Begin().Success);
        process.AdvanceLogicalTime(999);

        Assert.Same(oldSlave, roster.Slave);
        Assert.False(process.IsFinished);

        process.AdvanceLogicalTime(1);

        Assert.Same(spare, roster.Slave);
        Assert.True(process.IsFinished);
        Assert.True(process.Succeeded);
        Assert.Equal(1, interruption.Calls);
    }

    [Fact]
    public void SlaveReplacementRejectsNonEmptySpareWorkshop()
    {
        var master = new Workshop("主台");
        var oldSlave = new Workshop("旧从台");
        var spare = new Workshop("备用台");
        spare.AddJar(new Jar("圆罐"));
        var roster = new WorkshopRoster(new[] { master, oldSlave, spare }, master, oldSlave);

        Assert.Throws<InvalidOperationException>(() =>
            new WorkshopReplacementProcess(
                roster, WorkshopRole.Slave, spare, new NoOrderInterruption(), 1_000));
    }

    [Fact]
    public void MasterReplacementUsesTwoPhasesAndMovesOriginalJar()
    {
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" });
        var newMaster = new Workshop("新主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        var roster = new WorkshopRoster(new[] { oldMaster, newMaster, slave }, oldMaster, slave);
        var jar = new Jar("圆罐");
        oldMaster.AddJar(jar);
        var process = new WorkshopReplacementProcess(
            roster, WorkshopRole.Master, newMaster, new NoOrderInterruption(), 1_000);

        Assert.True(process.Begin().Success);
        process.AdvanceLogicalTime(1_000);

        Assert.Same(oldMaster, roster.Master);
        Assert.Single(oldMaster.Jars);
        Assert.Empty(newMaster.Jars);
        Assert.Equal(1, process.CompletedPhases);

        process.AdvanceLogicalTime(1_000);

        Assert.Same(newMaster, roster.Master);
        Assert.Empty(oldMaster.Jars);
        Assert.Same(jar, Assert.Single(newMaster.Jars));
        Assert.True(process.Succeeded);
    }

    [Fact]
    public void CancelAfterReshapeRestoresResourcesButDoesNotUndoInterruption()
    {
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" });
        var newMaster = new Workshop("新主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        var roster = new WorkshopRoster(new[] { oldMaster, newMaster, slave }, oldMaster, slave);
        var jar = new Jar("圆罐");
        oldMaster.AddJar(jar);
        oldMaster.AddLiquid("酒");
        var interruption = new NoOrderInterruption();
        var process = new WorkshopReplacementProcess(
            roster, WorkshopRole.Master, newMaster, interruption, 1_000);

        Assert.True(process.Begin().Success);
        process.AdvanceLogicalTime(1_000);

        Assert.Equal("酒", jar.Liquid);
        Assert.Equal(0, oldMaster.LiquidStorage["酒"]);

        process.Cancel();

        Assert.Same(oldMaster, roster.Master);
        Assert.Same(jar, Assert.Single(oldMaster.Jars));
        Assert.Null(jar.Liquid);
        Assert.Equal(1, oldMaster.LiquidStorage["酒"]);
        Assert.Empty(newMaster.Jars);
        Assert.Equal(1, interruption.Calls);
        Assert.False(process.Succeeded);
    }

    [Fact]
    public void TransferEffectsAreDeferredUntilCommitAndDiscardedOnRollback()
    {
        var oldMaster = new Workshop("旧主台");
        var newMaster = new Workshop("新主台", maxJars: 0);
        var jar = new Jar("圆罐");
        oldMaster.AddJar(jar);
        var pool = new JarPool();
        var ledger = new ResourceLedger();
        var discardedJars = 0;
        var migration = new WorkshopSwitchEvent(
            oldMaster, newMaster, pool,
            countDiscardedJars: amount => discardedJars += amount,
            ledger: ledger);

        migration.Begin();
        Assert.True(migration.RunReshape().Ok);
        Assert.True(migration.RunTransfer().Ok);

        // 转移阶段已经决定丢弃，但正式副作用尚未提交。
        Assert.Equal(0, discardedJars);
        Assert.Equal(0, ledger.LastSeq);
        Assert.Equal(0, pool.Returned);
        Assert.False(jar.IsRecycled);

        migration.Rollback();

        // 回滚恢复原对象，而不是快照克隆体；暂存副作用全部被丢弃。
        Assert.Same(jar, Assert.Single(oldMaster.Jars));
        Assert.False(jar.IsRecycled);
        Assert.Equal(0, discardedJars);
        Assert.Equal(0, ledger.LastSeq);
        Assert.Equal(0, pool.Returned);

        // 正常完整执行时，副作用只在迁徙成功后提交。
        var successfulMigration = new WorkshopSwitchEvent(
            oldMaster, newMaster, pool,
            countDiscardedJars: amount => discardedJars += amount,
            ledger: ledger);
        Assert.True(successfulMigration.Execute().Success);
        Assert.Empty(oldMaster.Jars);
        Assert.Equal(1, discardedJars);
        Assert.Equal(1, ledger.LastSeq);
        Assert.Equal(1, pool.Returned);
        Assert.True(jar.IsRecycled);
    }

    [Fact]
    public void RuntimeKeepsProducingButPausesMasterFlowDuringReplacement()
    {
        var vocabulary = Vocabulary.Default;
        var producers = new ProducerModule(vocabulary);
        producers.RegisterProducer("P", new Dictionary<string, double> { ["圆罐"] = 1 });
        var liquids = new LiquidProduction(
            vocabulary, defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var resources = new ResourceArea(producers, liquids, jarFlowRate: 10, liquidFlowRate: 0);
        var master = new Workshop("主台", new List<string> { "圆罐" }, vocabulary: vocabulary);
        var oldSlave = new Workshop("旧从台");
        var spare = new Workshop("备用台");
        var roster = new WorkshopRoster(new[] { master, oldSlave, spare }, master, oldSlave);
        var runtime = new ResourceWorldRuntime(resources, roster: roster);

        Assert.True(runtime.BeginReplacement(
            WorkshopRole.Slave, spare, new NoOrderInterruption(), phaseDurationMilli: 2_000).Success);

        runtime.AdvanceWorldTime(1_000);

        Assert.Single(resources.Inventory.Jars);
        Assert.Empty(master.Jars);
        Assert.Same(oldSlave, roster.Slave);
    }

    [Fact]
    public void RuntimeExplicitMasterArgumentOverridesRosterForCompatibilityOverload()
    {
        var vocabulary = Vocabulary.Default;
        var producers = new ProducerModule(vocabulary);
        producers.RegisterProducer("P", new Dictionary<string, double> { ["圆罐"] = 1 });
        var liquids = new LiquidProduction(
            vocabulary, defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var resources = new ResourceArea(producers, liquids, jarFlowRate: 1, liquidFlowRate: 0);
        var rosterMaster = new Workshop("岗位主台", new List<string> { "圆罐" }, vocabulary: vocabulary);
        var explicitMaster = new Workshop("显式目标", new List<string> { "圆罐" }, vocabulary: vocabulary);
        var slave = new Workshop("从台");
        var spare = new Workshop("备用台");
        var roster = new WorkshopRoster(
            new[] { rosterMaster, explicitMaster, slave, spare }, rosterMaster, slave);
        var runtime = new ResourceWorldRuntime(resources, roster: roster);

        runtime.AdvanceWorldTime(1_000, explicitMaster);

        Assert.Empty(rosterMaster.Jars);
        Assert.Single(explicitMaster.Jars);
        Assert.Empty(resources.Inventory.Jars);
    }

    [Fact]
    public void RuntimeExplicitNullMasterArgumentPausesFlowEvenWhenRosterExists()
    {
        var vocabulary = Vocabulary.Default;
        var producers = new ProducerModule(vocabulary);
        producers.RegisterProducer("P", new Dictionary<string, double> { ["圆罐"] = 1 });
        var liquids = new LiquidProduction(
            vocabulary, defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var resources = new ResourceArea(producers, liquids, jarFlowRate: 1, liquidFlowRate: 0);
        var rosterMaster = new Workshop("岗位主台", new List<string> { "圆罐" }, vocabulary: vocabulary);
        var slave = new Workshop("从台");
        var spare = new Workshop("备用台");
        var roster = new WorkshopRoster(new[] { rosterMaster, slave, spare }, rosterMaster, slave);
        var runtime = new ResourceWorldRuntime(resources, roster: roster);

        runtime.AdvanceWorldTime(1_000, null);

        Assert.Empty(rosterMaster.Jars);
        Assert.Single(resources.Inventory.Jars);
    }
}

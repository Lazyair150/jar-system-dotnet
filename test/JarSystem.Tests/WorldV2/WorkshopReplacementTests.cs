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
        Assert.Throws<InvalidOperationException>(() => successfulMigration.Rollback());
        Assert.True(jar.IsRecycled);
    }

    [Fact]
    public void RuntimeUsesInjectedMasterMigrationFactory()
    {
        var vocabulary = Vocabulary.Default;
        var producers = new ProducerModule(vocabulary);
        var liquids = new LiquidProduction(
            vocabulary, defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var resources = new ResourceArea(producers, liquids, jarFlowRate: 0, liquidFlowRate: 0);
        var oldMaster = new Workshop("旧主台");
        var newMaster = new Workshop("新主台", maxJars: 0);
        var slave = new Workshop("从台");
        var roster = new WorkshopRoster(new[] { oldMaster, newMaster, slave }, oldMaster, slave);
        var jar = new Jar("圆罐");
        oldMaster.AddJar(jar);
        var pool = new JarPool();
        var ledger = new ResourceLedger();
        var discardedJars = 0;
        var runtime = new ResourceWorldRuntime(
            resources,
            roster: roster,
            masterSwitchFactory: (oldWorkshop, newWorkshop) =>
                new WorkshopSwitchEvent(
                    oldWorkshop, newWorkshop, pool,
                    countDiscardedJars: amount => discardedJars += amount,
                    ledger: ledger));

        Assert.True(runtime.BeginReplacement(
            WorkshopRole.Master, newMaster, new NoOrderInterruption(), 1_000).Success);

        runtime.AdvanceWorldTime(2_000);

        Assert.Same(newMaster, roster.Master);
        Assert.True(runtime.ActiveReplacement!.Succeeded);
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
    [Fact]
    public void RestoreFromSnapshotValidatesAllOriginalJarsBeforeChangingWorkshop()
    {
        var workshop = new Workshop("工作台");
        var firstOriginal = new Jar("圆罐");
        var secondOriginal = new Jar("圆罐");
        workshop.AddJar(firstOriginal);
        workshop.AddJar(secondOriginal);
        workshop.AddLiquid("酒", 2);
        var snapshot = workshop.CreateSnapshot();

        var additional = new Jar("方罐");
        workshop.AddJar(additional);
        workshop.AddLiquid("酒", 3);

        // 模拟快照中的第二个原始实体被错误地提前归还对象池。
        // 第一只原始罐子可恢复，第二只不可恢复，用来捕获“先恢复一部分再失败”。
        var pool = new JarPool();
        pool.Return(secondOriginal);

        var currentJars = new List<Jar>(workshop.Jars);
        var currentLiquid = workshop.LiquidStorage["酒"];

        Assert.Throws<InvalidOperationException>(() => workshop.RestoreFromSnapshot(snapshot));

        // 验证失败必须发生在任何工作台状态修改之前。
        Assert.Equal(currentJars.Count, workshop.Jars.Count);
        for (var i = 0; i < currentJars.Count; i++)
            Assert.Same(currentJars[i], workshop.Jars[i]);
        Assert.Equal(currentLiquid, workshop.LiquidStorage["酒"]);
        Assert.Contains(firstOriginal, workshop.Jars);
        Assert.Contains(secondOriginal, workshop.Jars);
        Assert.Contains(additional, workshop.Jars);
    }

    [Fact]
    public void MigrationBeginCannotReplaceTheOriginalSnapshot()
    {
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" });
        var newMaster = new Workshop("新主台", new List<string> { "圆罐" });
        var jar = new Jar("圆罐");
        oldMaster.AddJar(jar);
        oldMaster.AddLiquid("酒");
        var migration = new WorkshopSwitchEvent(oldMaster, newMaster);

        migration.Begin();
        Assert.True(migration.RunReshape().Ok);
        Assert.Equal("酒", jar.Liquid);
        Assert.Equal(0, oldMaster.LiquidStorage["酒"]);

        // 重复 Begin 不能把已经变更的状态覆盖成新的事务起点。
        Assert.Throws<InvalidOperationException>(() => migration.Begin());

        migration.Rollback();

        Assert.Same(jar, Assert.Single(oldMaster.Jars));
        Assert.Null(jar.Liquid);
        Assert.Equal(1, oldMaster.LiquidStorage["酒"]);
        Assert.Empty(newMaster.Jars);
    }

    [Fact]
    public void MigrationRejectsOutOfOrderAndRepeatedStageCalls()
    {
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" });
        var newMaster = new Workshop("新主台", new List<string> { "圆罐" });
        oldMaster.AddJar(new Jar("圆罐"));
        var migration = new WorkshopSwitchEvent(oldMaster, newMaster);

        migration.Begin();
        Assert.Throws<InvalidOperationException>(() => migration.RunTransfer());
        Assert.Throws<InvalidOperationException>(() => migration.CommitTransferEffects());

        Assert.True(migration.RunReshape().Ok);
        Assert.Throws<InvalidOperationException>(() => migration.RunReshape());

        Assert.True(migration.RunTransfer().Ok);
        Assert.Throws<InvalidOperationException>(() => migration.RunTransfer());

        migration.CommitTransferEffects();
        migration.CommitTransferEffects(); // 成功提交允许幂等重复调用。
        Assert.Throws<InvalidOperationException>(() => migration.Rollback());
    }

    [Fact]
    public void MigrationRollbackIsIdempotentAndClosesTheLifecycle()
    {
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" });
        var newMaster = new Workshop("新主台", new List<string> { "圆罐" });
        var jar = new Jar("圆罐");
        oldMaster.AddJar(jar);
        var migration = new WorkshopSwitchEvent(oldMaster, newMaster);

        migration.Begin();
        Assert.True(migration.RunReshape().Ok);
        migration.Rollback();
        migration.Rollback();

        Assert.Same(jar, Assert.Single(oldMaster.Jars));
        Assert.Empty(newMaster.Jars);
        Assert.Throws<InvalidOperationException>(() => migration.Begin());
        Assert.Throws<InvalidOperationException>(() => migration.RunReshape());
    }

    [Fact]
    public void ReplacementInitializationFailureAfterInterruptionIsTerminalAndExplicit()
    {
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" });
        var target = new Workshop("备用台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        var roster = new WorkshopRoster(new[] { oldMaster, target, slave }, oldMaster, slave);
        var interruption = new NoOrderInterruption();
        var alreadyStartedMigration = new WorkshopSwitchEvent(oldMaster, target);
        alreadyStartedMigration.Begin();

        var process = new WorkshopReplacementProcess(
            roster,
            WorkshopRole.Master,
            target,
            interruption,
            phaseDurationMilli: 1_000,
            masterSwitchFactory: (_, _) => alreadyStartedMigration);

        var result = process.Begin();

        Assert.False(result.Success);
        Assert.Contains("订单已中断", result.Error);
        Assert.Contains("不会自动撤销", result.Error);
        Assert.True(process.IsFinished);
        Assert.False(process.Succeeded);
        Assert.Equal(1, interruption.Calls);

        // 初始化失败后不能重试 Begin，避免对同一订单再次发出中断请求。
        var retry = process.Begin();
        Assert.False(retry.Success);
        Assert.Equal(1, interruption.Calls);
    }


    [Theory]
    [InlineData(WorkshopRole.Master)]
    [InlineData(WorkshopRole.Slave)]
    public void ReplacementRejectsResourcesLeftOnOldSlaveAfterSuccessfulInterruption(WorkshopRole role)
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var oldSlave = new Workshop("旧从台");
        var target = new Workshop("备用台", new List<string> { "圆罐" });
        var roster = new WorkshopRoster(new[] { master, oldSlave, target }, master, oldSlave);
        var leftover = new Jar("圆罐");
        oldSlave.AddJar(leftover);
        var interruption = new NoOrderInterruption();
        var process = new WorkshopReplacementProcess(roster, role, target, interruption, 1_000);

        var result = process.Begin();

        Assert.False(result.Success);
        Assert.Contains("订单中断已成功", result.Error);
        Assert.Contains("仍有资源", result.Error);
        Assert.True(process.InterruptionCommitted);
        Assert.False(process.InterruptionOutcomeUncertain);
        Assert.True(process.IsFinished);
        Assert.False(process.Succeeded);
        Assert.Same(master, roster.Master);
        Assert.Same(oldSlave, roster.Slave);
        Assert.Same(leftover, Assert.Single(oldSlave.Jars));
        Assert.Equal(0, process.CompletedPhases);
        Assert.Equal(1, interruption.Calls);
    }

    [Fact]
    public void RuntimeRetainsTerminalReplacementWhenInterruptionCommittedButSlaveInvariantFails()
    {
        var vocabulary = Vocabulary.Default;
        var producers = new ProducerModule(vocabulary);
        var liquids = new LiquidProduction(
            vocabulary, defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var resources = new ResourceArea(producers, liquids, jarFlowRate: 0, liquidFlowRate: 0);
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var oldSlave = new Workshop("旧从台");
        var target = new Workshop("备用台", new List<string> { "圆罐" });
        var roster = new WorkshopRoster(new[] { master, oldSlave, target }, master, oldSlave);
        oldSlave.AddJar(new Jar("圆罐"));
        var runtime = new ResourceWorldRuntime(resources, roster: roster);

        var result = runtime.BeginReplacement(
            WorkshopRole.Master, target, new NoOrderInterruption(), phaseDurationMilli: 1_000);

        Assert.False(result.Success);
        Assert.NotNull(runtime.ActiveReplacement);
        Assert.True(runtime.ActiveReplacement!.IsFinished);
        Assert.True(runtime.ActiveReplacement.InterruptionCommitted);
        Assert.False(runtime.ActiveReplacement.Succeeded);
        Assert.Contains("仍有资源", runtime.ActiveReplacement.FailureReason);
        Assert.Same(master, roster.Master);
        Assert.Same(oldSlave, roster.Slave);
    }


    [Fact]
    public void RuntimeRejectsOverlappingReplacementButAllowsANewOneAfterCompletion()
    {
        var vocabulary = Vocabulary.Default;
        var producers = new ProducerModule(vocabulary);
        var liquids = new LiquidProduction(
            vocabulary, defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var resources = new ResourceArea(producers, liquids, jarFlowRate: 0, liquidFlowRate: 0);
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        var firstSpare = new Workshop("备用台一", new List<string> { "圆罐" });
        var secondSpare = new Workshop("备用台二", new List<string> { "圆罐" });
        var roster = new WorkshopRoster(
            new[] { master, slave, firstSpare, secondSpare }, master, slave);
        var runtime = new ResourceWorldRuntime(resources, roster: roster);

        var first = runtime.BeginReplacement(
            WorkshopRole.Master, firstSpare, new NoOrderInterruption(), 1_000);

        Assert.True(first.Success);
        var firstProcess = runtime.ActiveReplacement;
        Assert.NotNull(firstProcess);

        var overlapping = runtime.BeginReplacement(
            WorkshopRole.Slave, secondSpare, new NoOrderInterruption(), 1_000);

        Assert.False(overlapping.Success);
        Assert.Contains("已有工作台替换过程", overlapping.Error);
        Assert.Same(firstProcess, runtime.ActiveReplacement);
        Assert.Same(master, roster.Master);
        Assert.Same(slave, roster.Slave);

        runtime.AdvanceWorldTime(2_000);

        Assert.Same(firstSpare, roster.Master);
        Assert.True(firstProcess!.IsFinished);
        Assert.True(firstProcess.Succeeded);

        // 第一个过程终结后，旧主台已成为备用台，可以合法接任从台。
        var second = runtime.BeginReplacement(
            WorkshopRole.Slave, master, new NoOrderInterruption(), 1_000);

        Assert.True(second.Success);
        Assert.NotSame(firstProcess, runtime.ActiveReplacement);
        runtime.AdvanceWorldTime(1_000);

        Assert.Same(master, roster.Slave);
        Assert.True(runtime.ActiveReplacement!.IsFinished);
        Assert.True(runtime.ActiveReplacement.Succeeded);
        Assert.False(runtime.IsFaulted);
    }

    [Fact]
    public void RuntimeFreezesAndRetainsReplacementWhenMigrationRollbackFails()
    {
        var vocabulary = Vocabulary.Default;
        var producers = new ProducerModule(vocabulary);
        var liquids = new LiquidProduction(
            vocabulary, defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 });
        var resources = new ResourceArea(producers, liquids, jarFlowRate: 0, liquidFlowRate: 0);
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" });
        var target = new Workshop("备用台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        var roster = new WorkshopRoster(new[] { oldMaster, target, slave }, oldMaster, slave);
        var originalJar = new Jar("圆罐");
        oldMaster.AddJar(originalJar);
        var pool = new JarPool();
        var runtime = new ResourceWorldRuntime(
            resources,
            roster: roster,
            masterSwitchFactory: (oldWorkshop, newWorkshop) =>
                new WorkshopSwitchEvent(oldWorkshop, newWorkshop, pool));

        var begin = runtime.BeginReplacement(
            WorkshopRole.Master, target, new NoOrderInterruption(), 1_000);
        Assert.True(begin.Success);
        Assert.True(runtime.ActiveReplacement!.InterruptionCommitted);

        // 第一阶段已修改工作台；随后模拟快照所依赖的原始实体被对象池提前回收。
        runtime.AdvanceWorldTime(1_000);
        pool.Return(originalJar);

        var cancel = runtime.CancelActiveReplacement("测试回滚失败");

        Assert.False(cancel.Success);
        Assert.True(runtime.IsFaulted);
        Assert.Contains("回滚失败", runtime.FaultReason);
        Assert.NotNull(runtime.ActiveReplacement);
        Assert.False(runtime.ActiveReplacement!.IsFinished);
        Assert.True(runtime.ActiveReplacement.InterruptionCommitted);
        Assert.Same(oldMaster, roster.Master);

        // 故障后不能再推进世界；替换对象仍保留，供快照/诊断读取。
        Assert.Throws<InvalidOperationException>(() => runtime.AdvanceWorldTime(1));
    }

}

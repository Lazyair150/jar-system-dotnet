using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using JarSystem.Core.Solver;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Solving;
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
    public void MasterReplacementTransfersFullJarEvenWhenTargetBansItsInternalLiquid()
    {
        var oldMaster = new Workshop("允许酒的旧主台",
            new List<string> { "圆罐", "方罐" });
        var newMaster = new Workshop("禁酒新主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        var slave = new Workshop("从台");
        var jar = new Jar("圆罐", "酒", id: 781);
        oldMaster.AddJar(jar);
        var roster = new WorkshopRoster(new[] { oldMaster, newMaster, slave }, oldMaster, slave);
        var process = new WorkshopReplacementProcess(
            roster, WorkshopRole.Master, newMaster, new NoOrderInterruption(), 1_000);

        Assert.True(process.Begin().Success);
        process.AdvanceLogicalTime(1_000);
        process.AdvanceLogicalTime(1_000);

        Assert.True(process.Succeeded);
        Assert.Same(jar, Assert.Single(newMaster.Jars));
        Assert.Equal("酒", jar.Liquid);
        Assert.True(newMaster.IsBanned("酒"));
        Assert.False(newMaster.CanReshapeTo(jar, "方罐").Granted);
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


    [Fact]
    public void WorldV2ProductionCannotConsumeMasterPrefillRollbackCapacity()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 3 });
        var resources = new ResourceArea(
            new ProducerModule(vocabulary),
            new LiquidProduction(vocabulary, defaults: new Dictionary<string, double>
            {
                ["酒"] = 0.001,
                ["蜜"] = 0.001
            }),
            0, 0, ship);
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: 4);
        var target = new Workshop("目标主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: 4);
        oldMaster.AddJar(new Jar("圆罐", vocabulary: vocabulary));
        var solverRegistry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        using var world = WorldV2Context.Create(
            resources, new[] { oldMaster, target }, oldMaster, null, solverRegistry,
            logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: ship);

        var begin = world.Runtime.BeginReplacement(
            WorkshopRole.Master, target, new NoOrderInterruption(), phaseDurationMilli: 2_000_000);
        Assert.True(begin.Success, begin.Error);
        Assert.Equal(3, ship.ReservedReturnCapacity);
        Assert.Equal(0, ship.UsedCapacity);

        world.Runtime.AdvanceWorldTime(1_000_000);

        Assert.Equal(3, ship.ReservedReturnCapacity);
        Assert.Equal(1, ship.UsedCapacity);
        Assert.Equal(0, ship.AvailableCapacity);

        var cancelled = world.Runtime.CancelActiveReplacement("验证生产与补液回滚隔离");
        Assert.True(cancelled.Success);
        Assert.Equal(0, ship.ReservedReturnCapacity);
        Assert.InRange(ship.GetAmount("酒"), 3, 4);
        Assert.Equal(4, ship.UsedCapacity);
        Assert.Empty(world.Admission.GetPrefillBalances(target));
        Assert.Contains(world.ResourceLedger.For(ResourceCauses.Return), item =>
            item.From == "主台预置" && item.To == "自机液库" &&
            item.Reason == "主工作台切换回滚");
    }

    [Fact]
    public void WorldV2SlaveReplacementDoesNotPrefillOrWithdrawShipLiquids()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var ship = new ShipLiquidInventory(6, new Dictionary<string, int>
        {
            ["酒"] = 3,
            ["蜜"] = 2
        });
        var resources = new ResourceArea(
            new ProducerModule(vocabulary),
            new LiquidProduction(vocabulary, defaults: new Dictionary<string, double>
            {
                ["酒"] = 0.001,
                ["蜜"] = 0.0005
            }),
            0, 0, ship);
        var master = new Workshop("主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: 4);
        var oldSlave = new Workshop("旧从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: 4);
        var target = new Workshop("备用台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: 4);
        var solverRegistry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        using var world = WorldV2Context.Create(
            resources, new[] { master, oldSlave, target }, master, oldSlave, solverRegistry,
            logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: ship);

        var begin = world.Runtime.BeginReplacement(
            WorkshopRole.Slave, target, new NoOrderInterruption(), phaseDurationMilli: 1_000);

        Assert.True(begin.Success, begin.Error);
        Assert.Empty(world.Admission.GetPrefillBalances(target));
        Assert.Equal(3, ship.GetAmount("酒"));
        Assert.Equal(2, ship.GetAmount("蜜"));
        Assert.Empty(world.ResourceLedger.For(ResourceCauses.Flow));
        world.Runtime.CancelActiveReplacement("测试结束");
    }

    [Fact]
    public void WorldV2MasterReplacementPrefillsTargetBeforeMigrationAndRecordsLedger()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "蜜", "酒" });
        var ship = new ShipLiquidInventory(8, new Dictionary<string, int>
        {
            ["酒"] = 3,
            ["蜜"] = 2
        });
        var producers = new ProducerModule(vocabulary);
        var production = new LiquidProduction(vocabulary, defaults: new Dictionary<string, double>
        {
            ["酒"] = 0.001,
            ["蜜"] = 0.0005
        });
        var resources = new ResourceArea(producers, production, 0, 0, ship);
        var oldMaster = new Workshop("旧主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: 4);
        var target = new Workshop("目标主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: 4);
        var jar = new Jar("圆罐", vocabulary: vocabulary);
        oldMaster.AddJar(jar);
        var solverRegistry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        using var world = WorldV2Context.Create(
            resources, new[] { oldMaster, target }, oldMaster, null, solverRegistry,
            logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: ship);

        var begin = world.Runtime.BeginReplacement(
            WorkshopRole.Master, target, new NoOrderInterruption(), phaseDurationMilli: 1_000);

        Assert.True(begin.Success, begin.Error);
        Assert.False(world.Admission.TryAdmit(999).Success);
        Assert.Same(jar, Assert.Single(oldMaster.Jars));
        Assert.Equal(3, world.Admission.GetPrefillBalances(target)["酒"]);
        Assert.Equal(1, world.Admission.GetPrefillBalances(target)["蜜"]);
        Assert.Equal(0, ship.GetAmount("酒"));
        Assert.Equal(1, ship.GetAmount("蜜"));
        var prefillEvents = world.ResourceLedger.For(ResourceCauses.Flow);
        Assert.Equal(2, prefillEvents.Count);
        Assert.All(prefillEvents, item =>
        {
            Assert.Equal("自机液库", item.From);
            Assert.Equal("主台预置", item.To);
        });

        world.Runtime.AdvanceWorldTime(2_000);

        Assert.Same(target, world.Roster.Master);
        Assert.True(world.Runtime.ActiveReplacement!.Succeeded);
        Assert.Equal("订单已不在需求板中", world.Admission.TryAdmit(999).Reason);
        Assert.Empty(oldMaster.Jars);
        Assert.Same(jar, Assert.Single(target.Jars));
        var checkpoint = world.CaptureExecutionResourceCheckpoint();
        Assert.Equal(3, checkpoint.Admission.MasterStaging!.PrefillBalances["酒"]);
        Assert.Equal(1, checkpoint.Admission.MasterStaging.PrefillBalances["蜜"]);

        var restoredShip = new ShipLiquidInventory(8, checkpoint.ShipLiquidInventory.Balances);
        var restoredResources = new ResourceArea(
            new ProducerModule(vocabulary),
            new LiquidProduction(vocabulary, defaults: new Dictionary<string, double>
            {
                ["酒"] = 0.001,
                ["蜜"] = 0.0005
            }),
            0, 0, restoredShip);
        var restoredMaster = new Workshop("目标主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: 4);
        restoredMaster.AddJar(new Jar("圆罐",
            id: checkpoint.MasterWorkshop.Jars[0].Id, vocabulary: vocabulary));
        var restoredSpare = new Workshop("旧主台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: 4);
        using var restoredWorld = WorldV2Context.Create(
            restoredResources, new[] { restoredMaster, restoredSpare },
            restoredMaster, null, solverRegistry,
            logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: restoredShip);
        restoredWorld.RestoreExecutionResourceCheckpoint(checkpoint);
        Assert.Equal(3, restoredWorld.Admission.GetPrefillBalances(restoredMaster)["酒"]);
        Assert.Equal(1, restoredWorld.Admission.GetPrefillBalances(restoredMaster)["蜜"]);
        Assert.Equal(1, restoredShip.GetAmount("蜜"));
    }


    [Fact]
    public void RetiredMasterPrefillReturnsOnlyAfterCommitAndCancelPreservesIt()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "蜜", "酒" });
        var ship = new ShipLiquidInventory(8, new Dictionary<string, int>
        {
            ["酒"] = 4,
            ["蜜"] = 4
        });
        var producers = new ProducerModule(vocabulary);
        var production = new LiquidProduction(vocabulary, defaults: new Dictionary<string, double>
        {
            ["酒"] = 0.001,
            ["蜜"] = 0.0005
        });
        var resources = new ResourceArea(producers, production, 0, 0, ship);
        Workshop CreateWorkshop(string name, int stagingCapacity = 4) => new(name, new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: stagingCapacity);
        var firstMaster = CreateWorkshop("主台 A");
        var secondMaster = CreateWorkshop("备用台 B");
        var slave = CreateWorkshop("从台");
        var solverRegistry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        using var world = WorldV2Context.Create(
            resources, new[] { firstMaster, secondMaster, slave }, firstMaster, slave,
            solverRegistry, logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: ship);

        var first = world.Runtime.BeginReplacement(
            WorkshopRole.Master, secondMaster, new NoOrderInterruption(), 1_000);
        Assert.True(first.Success, first.Error);
        world.Runtime.AdvanceWorldTime(2_000);
        Assert.Same(secondMaster, world.Roster.Master);
        Assert.Equal(4, world.Admission.GetPrefillBalances(secondMaster)["酒"]);
        Assert.Equal(4, ship.GetAmount("蜜"));

        // 第二次替换开始时，新主台先获得自己的预置液体；取消不得返还旧主台余额。
        var cancelled = world.Runtime.BeginReplacement(
            WorkshopRole.Master, firstMaster, new NoOrderInterruption(), 1_000);
        Assert.True(cancelled.Success, cancelled.Error);
        Assert.Equal(4, world.Admission.GetPrefillBalances(secondMaster)["酒"]);
        Assert.Equal(4, world.Admission.GetPrefillBalances(firstMaster)["蜜"]);
        Assert.True(world.Runtime.CancelActiveReplacement("测试取消").Success);
        Assert.Equal(4, world.Admission.GetPrefillBalances(secondMaster)["酒"]);
        Assert.Empty(world.Admission.GetPrefillBalances(firstMaster));
        Assert.Equal(4, ship.GetAmount("蜜"));

        // 只有第二次替换正式提交后，旧主台预置余额才返还自机液库并从退役 staging 中移除。
        var committed = world.Runtime.BeginReplacement(
            WorkshopRole.Master, firstMaster, new NoOrderInterruption(), 1_000);
        Assert.True(committed.Success, committed.Error);
        world.Runtime.AdvanceWorldTime(2_000);
        Assert.Same(firstMaster, world.Roster.Master);
        Assert.Empty(world.Admission.GetPrefillBalances(secondMaster));
        Assert.Equal(4, ship.GetAmount("酒"));
        Assert.Equal(4, world.Admission.GetPrefillBalances(firstMaster)["蜜"]);
        var returns = world.ResourceLedger.For(ResourceCauses.Return);
        var returned = Assert.Single(returns,
            item => item.Reason == "主工作台替换后返还未消费预置液体");
        Assert.Equal("酒", returned.Name);
        Assert.Equal(4, returned.Amount);
        Assert.Equal("主台预置", returned.From);
        Assert.Equal("自机液库", returned.To);

        // 退役主台的 staging 已清空并脱离准入器，检查点不再被旧岗位对象阻塞。
        world.CaptureExecutionResourceCheckpoint();

        // 旧主台可以作为空备用个体接任从台，不会把主台预置液体带入从台。
        var slaveReplacement = world.Runtime.BeginReplacement(
            WorkshopRole.Slave, secondMaster, new NoOrderInterruption(), 1_000);
        Assert.True(slaveReplacement.Success, slaveReplacement.Error);
        world.Runtime.AdvanceWorldTime(1_000);
        Assert.Same(secondMaster, world.Roster.Slave);
        Assert.Empty(world.Admission.GetPrefillBalances(secondMaster));
    }


    [Fact]
    public void RetiredMasterPrefillUsesShipReturnCapacityAndRecordsOverflowDiscard()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "蜜", "酒" });
        var ship = new ShipLiquidInventory(8, new Dictionary<string, int>
        {
            ["酒"] = 4,
            ["蜜"] = 4
        });
        var production = new LiquidProduction(vocabulary, defaults: new Dictionary<string, double>
        {
            ["酒"] = 0,
            ["蜜"] = 2
        });
        var resources = new ResourceArea(new ProducerModule(vocabulary), production, 0, 0, ship);
        Workshop CreateWorkshop(string name, int stagingCapacity = 4) => new(name, new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: stagingCapacity);
        var firstMaster = CreateWorkshop("主台 A", stagingCapacity: 2);
        var secondMaster = CreateWorkshop("备用台 B");
        var slave = CreateWorkshop("从台");
        var solverRegistry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        using var world = WorldV2Context.Create(
            resources, new[] { firstMaster, secondMaster, slave }, firstMaster, slave,
            solverRegistry, logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: ship);

        var first = world.Runtime.BeginReplacement(
            WorkshopRole.Master, secondMaster, new NoOrderInterruption(), 1_000);
        Assert.True(first.Success, first.Error);
        world.Runtime.AdvanceWorldTime(2_000);
        Assert.Equal(4, world.Admission.GetPrefillBalances(secondMaster)["蜜"]);
        Assert.Equal(8, ship.UsedCapacity);
        production.SetRate("蜜", 0);

        // 新主台只预置 2 份，旧主台待返还 4 份；自机液库仅有 2 个空位。
        var second = world.Runtime.BeginReplacement(
            WorkshopRole.Master, firstMaster, new NoOrderInterruption(), 1_000);
        Assert.True(second.Success, second.Error);
        Assert.Equal(2, world.Admission.GetPrefillBalances(firstMaster)["蜜"]);
        world.Runtime.AdvanceWorldTime(2_000);

        Assert.Same(firstMaster, world.Roster.Master);
        Assert.Empty(world.Admission.GetPrefillBalances(secondMaster));
        var discardedReturn = Assert.Single(world.ResourceLedger.For(ResourceCauses.Discard),
            item => item.From == "主台预置");
        Assert.Equal("蜜", discardedReturn.Name);
        Assert.Equal(2, discardedReturn.Amount);
        Assert.Equal("主工作台替换后返还自机液库时容量不足", discardedReturn.Reason);
        Assert.Equal(4, ship.GetAmount("酒"));
        Assert.Equal(4, ship.GetAmount("蜜"));
    }


    [Fact]
    public void PrefillOrderConsumptionSettlementAndRepeatedMasterReplacementPreserveLiquidConservation()
    {
        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var ship = new ShipLiquidInventory(8, new Dictionary<string, int> { ["酒"] = 4 });
        var resources = new ResourceArea(
            new ProducerModule(vocabulary),
            new LiquidProduction(vocabulary, defaults: new Dictionary<string, double>
            {
                ["酒"] = 0,
                ["蜜"] = 0
            }),
            jarFlowRate: 0,
            liquidFlowRate: 0,
            shipLiquidInventory: ship);
        Workshop CreateWorkshop(string name, int stagingCapacity = 4) => new(name, new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: vocabulary, liquidStagingCapacity: stagingCapacity);
        var firstMaster = CreateWorkshop("主台 A");
        var secondMaster = CreateWorkshop("主台 B", stagingCapacity: 3);
        var slave = CreateWorkshop("从台");
        var deliveredJar = new Jar("圆罐", vocabulary: vocabulary);
        var solverRegistry = new WorldOrderSolverRegistry(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });
        using var world = WorldV2Context.Create(
            resources, new[] { firstMaster, secondMaster, slave }, firstMaster, slave,
            solverRegistry, logicalTimePerUnit: 1_000,
            passiveFulfillmentPolicy: DemandAreaFulfillmentPolicy.OneShot,
            activeFulfillmentPolicy: DemandAreaFulfillmentPolicy.Continuous,
            shipLiquidInventory: ship);

        // 第一次换主台：从自机液库预置 3 份液体。
        var firstSwitch = world.Runtime.BeginReplacement(
            WorkshopRole.Master, secondMaster, new NoOrderInterruption(), 1_000);
        Assert.True(firstSwitch.Success, firstSwitch.Error);
        world.Runtime.AdvanceWorldTime(2_000);
        Assert.Same(secondMaster, world.Roster.Master);
        Assert.Equal(3, world.Admission.GetPrefillBalances(secondMaster)["酒"]);
        Assert.Equal(1, ship.GetAmount("酒"));
        secondMaster.AddJar(deliveredJar);

        // A 类订单只在主台执行，优先消耗预置余额；未消耗的 2 份仍属于主台预置账户。
        var demand = new OrderDemand(new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
            Constants.OrderType.A, vocabulary), 0, world.DemandIdGenerator);
        world.DemandBoard.AddDemand(demand, DemandAreaKind.Active);
        var started = world.Scheduler.TryStartActiveDemand(demand.Id);
        Assert.True(started.Success, started.Reason);
        world.Runtime.AdvanceWorldTime(1_000);
        Assert.True(started.Session!.Succeeded, started.Session.Reason);
        Assert.Equal(2, world.Admission.GetPrefillBalances(secondMaster)["酒"]);
        Assert.Equal(1, ship.GetAmount("酒"));
        Assert.Empty(secondMaster.Jars);
        Assert.Equal("酒", deliveredJar.Liquid);

        // 第二张订单继续消费同一主台预置余额，验证订单之间不重复扣自机液库。
        var secondDeliveredJar = new Jar("圆罐", vocabulary: vocabulary);
        secondMaster.AddJar(secondDeliveredJar);
        var secondDemand = new OrderDemand(new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
            Constants.OrderType.A, vocabulary), 0, world.DemandIdGenerator);
        world.DemandBoard.AddDemand(secondDemand, DemandAreaKind.Active);
        var secondStarted = world.Scheduler.TryStartActiveDemand(secondDemand.Id);
        Assert.True(secondStarted.Success, secondStarted.Reason);
        world.Runtime.AdvanceWorldTime(1_000);
        Assert.True(secondStarted.Session!.Succeeded, secondStarted.Session.Reason);
        Assert.Equal("酒", secondDeliveredJar.Liquid);
        Assert.Equal(1, world.Admission.GetPrefillBalances(secondMaster)["酒"]);
        Assert.Equal(1, ship.GetAmount("酒"));

        // 再次换主台：新主台先从自机液库获得剩余 1 份；旧主台剩余 1 份随后返还。
        var secondSwitch = world.Runtime.BeginReplacement(
            WorkshopRole.Master, firstMaster, new NoOrderInterruption(), 1_000);
        Assert.True(secondSwitch.Success, secondSwitch.Error);
        Assert.Equal(1, world.Admission.GetPrefillBalances(firstMaster)["酒"]);
        world.Runtime.AdvanceWorldTime(2_000);

        Assert.Same(firstMaster, world.Roster.Master);
        Assert.Empty(world.Admission.GetPrefillBalances(secondMaster));
        Assert.Equal(1, world.Admission.GetPrefillBalances(firstMaster)["酒"]);
        Assert.Equal(1, ship.GetAmount("酒"));
        Assert.Equal(4, ship.GetAmount("酒") +
            world.Admission.GetPrefillBalances(firstMaster)["酒"] +
            (deliveredJar.Liquid == "酒" ? 1 : 0) +
            (secondDeliveredJar.Liquid == "酒" ? 1 : 0));

        var prefillReturn = Assert.Single(world.ResourceLedger.For(ResourceCauses.Return),
            item => item.From == "主台预置");
        Assert.Equal("酒", prefillReturn.Name);
        Assert.Equal(1, prefillReturn.Amount);
        Assert.Equal("自机液库", prefillReturn.To);

        // 连续换台、订单执行和返还后，检查点捕获不应再看到退役工作台的残留 staging。
        world.CaptureExecutionResourceCheckpoint();
    }

}

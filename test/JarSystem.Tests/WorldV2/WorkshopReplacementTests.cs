using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;
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
        Assert.Null(Assert.Single(oldMaster.Jars).Liquid);
        Assert.Equal(1, oldMaster.LiquidStorage["酒"]);
        Assert.Empty(newMaster.Jars);
        Assert.Equal(1, interruption.Calls);
        Assert.False(process.Succeeded);
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
}

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// No Mysterious Resources Contract 的内核侧验收：**每一个资源变化都必须指得到原因**。
///
/// 这里钉的是"账本与事实对得上"，而不是文案：
/// 产出有 `production`、开局库存有 `stock`、流入有 `flow`、放不下有 `discard`，
/// 且没有任何一条事实用未登记的原因。表现层因此不必从前后状态差值反推。
/// </summary>
public class ResourceProvenanceTests
{
    private static WorldOptions QuietWorld()
    {
        var world = WorldOptions.Default;
        world.Producers = [];
        world.Workshops = [new WorkshopOptions { Name = "主台", BaseMarks = ["圆罐"] }];
        world.InitialStock = new Dictionary<string, int>();
        world.Flow = new FlowOptions
        {
            JarCount = 0,
            LiquidAmount = 0,
            LiquidRate = world.Liquids.ToDictionary(liquid => liquid, _ => 0.0)
        };
        return world;
    }

    [Fact]
    public void EveryProducedJarCarriesAProductionCause()
    {
        var world = QuietWorld();
        world.Producers = [new ProducerOptions { Name = "陶工", Rates = new() { ["圆罐"] = 1.0 } }];
        var sys = new JarSystem.Core.JarSystem(randomSeed: 11, world: world);

        sys.Tick(2);

        var produced = sys.ResourceLedger.For(ResourceCauses.Production);
        var jarFacts = produced.Where(e => e.Kind == ResourceKinds.Jar).ToArray();
        Assert.Equal(2, jarFacts.Length);
        Assert.All(jarFacts, e =>
        {
            Assert.Equal("圆罐", e.Name);
            Assert.Equal(1, e.Amount);
            Assert.True(int.TryParse(e.ResourceIdentity, out var jarId) && jarId > 0);
            Assert.Equal("仓库", e.To);
        });
        Assert.Equal(2, jarFacts.Select(e => e.ResourceIdentity).Distinct(StringComparer.Ordinal).Count());
        Assert.All(produced, e => Assert.Equal("仓库", e.To));
        Assert.All(sys.ResourceLedger.Entries,
            e => Assert.Contains(e.Cause, ResourceCauses.All));
    }

    [Fact]
    public void FlowIsRecordedSeparatelyFromProduction()
    {
        var world = QuietWorld();
        world.InitialStock = new Dictionary<string, int> { ["蜜"] = 4 };
        world.Flow = new FlowOptions
        {
            JarCount = 1,
            LiquidAmount = 2,
            LiquidRate = world.Liquids.ToDictionary(liquid => liquid, _ => 0.0)
        };
        var sys = new JarSystem.Core.JarSystem(randomSeed: 13, world: world);
        sys.SetMasterIndex(0);   /* 没有主台就没有流入；账本要验的是"流入"这条事实 */
        sys.ProducerModule.Warehouse.StoreJars([new Jar("圆罐", id: 900, vocabulary: Vocabulary.Default)]);

        sys.Tick();

        var flowed = sys.ResourceLedger.For(ResourceCauses.Flow);
        Assert.Contains(flowed, e => e.Kind == ResourceKinds.Liquid && e.Name == "蜜" && e.Amount == 2);
        Assert.Contains(flowed, e => e.Kind == ResourceKinds.Jar && e.Name == "圆罐");
        Assert.All(flowed, e => Assert.Equal("主台", e.To));
        // 流入不是产出：这局没有任何产线在跑，因此不该有 production 事实来"顶账"。
        Assert.Empty(sys.ResourceLedger.For(ResourceCauses.Production));
    }

    [Fact]
    public void InitialStockIsRecordedAsStockWithTheLevelAsSource()
    {
        var world = QuietWorld();
        world.InitialStock = new Dictionary<string, int> { ["蜜"] = 3 };
        var sys = new JarSystem.Core.JarSystem(randomSeed: 17, world: world);

        var stock = sys.ResourceLedger.For(ResourceCauses.Stock);

        Assert.Contains(stock, e => e.Name == "蜜" && e.Amount == 3);
        Assert.All(stock, e =>
        {
            Assert.Equal(ResourceKinds.Liquid, e.Kind);
            Assert.Equal("关卡配置", e.From);
            Assert.Equal("仓库", e.To);
            Assert.Equal(ResourceEndpointKind.ExternalSource, e.FromEndpoint.Kind);
            Assert.Equal("level.initial-stock", e.FromEndpoint.ExternalEndpointId);
            Assert.Equal(ResourceEndpointKind.LegacyUnknown, e.ToEndpoint.Kind);
            Assert.Equal(0, e.Tick);
        });
    }

    [Fact]
    public void OverflowIsRecordedAsDiscardInsteadOfSilence()
    {
        var world = QuietWorld();
        world.InitialStock = new Dictionary<string, int> { ["蜜"] = Constants.WAREHOUSE_MAX_LIQUIDS };
        world.Flow = new FlowOptions
        {
            JarCount = 0,
            LiquidAmount = 0,
            LiquidRate = world.Liquids.ToDictionary(l => l, l => l == "蜜" ? 1.0 : 0.0)
        };
        var sys = new JarSystem.Core.JarSystem(randomSeed: 19, world: world);

        sys.Tick();

        var producedHoney = sys.ResourceLedger.For(ResourceCauses.Production)
            .Where(e => e.Name == "蜜").Sum(e => e.Amount);
        var discardedHoney = sys.ResourceLedger.For(ResourceCauses.Discard)
            .Where(e => e.Name == "蜜").Sum(e => e.Amount);

        Assert.Equal(1, producedHoney);       // 产了 1 份
        Assert.Equal(1, discardedHoney);      // 但仓库满了，1 份进丢弃账目
        var discard = Assert.Single(sys.ResourceLedger.For(ResourceCauses.Discard),
            e => e.Name == "蜜" && e.Reason == "仓库该液体已满");
        Assert.Equal(ResourceEndpointKind.LegacyUnknown, discard.FromEndpoint.Kind);
        Assert.Equal(ResourceEndpointKind.ExternalSink, discard.ToEndpoint.Kind);
        Assert.Equal("legacy-warehouse.liquid-capacity-overflow", discard.ToEndpoint.ExternalEndpointId);
    }

    [Fact]
    public void ResetStartsAFreshLedgerButTheSequenceNeverRewinds()
    {
        var world = QuietWorld();
        var sys = new JarSystem.Core.JarSystem(randomSeed: 23, world: world);

        sys.Tick();
        var seqAfterFirstTicks = sys.ResourceLedger.LastSeq;

        sys.Reset();

        Assert.Empty(sys.ResourceLedger.For(ResourceCauses.Production));
        Assert.True(sys.ResourceLedger.LastSeq >= seqAfterFirstTicks,
            "清空只清条目，序号不回绕 —— 否则读者会以为中间没丢过东西");
    }

    [Fact]
    public void TheLedgerIsBoundedAndDroppedFactsStayVisible()
    {
        var ledger = new ResourceLedger(capacity: 4);

        for (var i = 0; i < 10; i++)
            ledger.Record(tick: i, ResourceCauses.Production, ResourceKinds.Jar, "圆罐", 1, "仓库", "主台");

        Assert.True(ledger.Retained <= 4);
        Assert.Equal(10, ledger.LastSeq);
        Assert.Equal(ledger.Retained + ledger.Dropped, (int)ledger.LastSeq);
        Assert.True(ledger.Dropped > 0);
    }

    [Fact]
    public void ZeroOrNegativeAmountsAreNotFacts()
    {
        var ledger = new ResourceLedger();

        Assert.Null(ledger.Record(0, ResourceCauses.Production, ResourceKinds.Jar, "圆罐", 0));
        Assert.Null(ledger.Record(0, ResourceCauses.Production, ResourceKinds.Jar, "圆罐", -2));
        Assert.Equal(0, ledger.LastSeq);
    }

    [Fact]
    public void SwitchingTheMasterRecordsWhereEveryMovedThingWent()
    {
        var world = QuietWorld();
        // QuietWorld 把默认容量压到 1（生产测试靠它逼出溢出），迁移测试要真实容量。
        world.DefaultMaxJars = 6;
        world.DefaultMaxLiquids = 5;
        world.Workshops =
        [
            new WorkshopOptions { Name = "旧主台", BaseMarks = ["圆罐"] },
            new WorkshopOptions { Name = "新主台", BaseMarks = ["圆罐"] }
        ];
        var sys = new JarSystem.Core.JarSystem(randomSeed: 41, world: world);
        sys.SetMasterIndex(0);
        // 罐子先灌满：迁移的改形阶段只对**空罐**收成本液体（同形也算可改形），
        // 空罐会白付一份液体；这里要观察的是转移本身，所以不给它这一步。
        var jar = new Jar("圆罐", id: 9001, vocabulary: Vocabulary.Default);
        sys.Workshops[0].AddJar(jar);
        sys.Workshops[0].AddLiquid("酒", 2);
        Assert.True(sys.Workshops[0].PourIn(jar, "酒").Success);

        var started = sys.BeginSwitch(1);
        Assert.True(started.Started, started.Error);
        for (var i = 0; i < 8 && sys.Workshops[0].Jars.Count > 0; i++) sys.Tick();

        var moved = sys.ResourceLedger.For(ResourceCauses.Transfer);
        Assert.Contains(moved, e => e.Kind == ResourceKinds.Jar && e.Name == "圆罐" && e.Amount == 1
                                    && e.From == "旧主台" && e.To == "新主台" && e.ResourceIdentity == "9001");
        Assert.Contains(moved, e => e.Kind == ResourceKinds.Liquid && e.Name == "酒" && e.Amount == 1
                                    && e.From == "旧主台" && e.To == "新主台" && e.ResourceIdentity == "酒");
    }

    [Fact]
    public void WhatTheNewMasterCannotHoldIsRecordedAsADiscardNotAsASilentLoss()
    {
        var world = QuietWorld();
        world.DefaultMaxJars = 6;
        world.DefaultMaxLiquids = 5;
        world.Workshops =
        [
            new WorkshopOptions { Name = "旧主台", BaseMarks = ["圆罐"] },
            new WorkshopOptions { Name = "新主台", BaseMarks = ["圆罐"], MaxJars = 1, MaxLiquids = 1 }
        ];
        var sys = new JarSystem.Core.JarSystem(randomSeed: 43, world: world);
        sys.SetMasterIndex(0);
        var jar = new Jar("圆罐", id: 9002, vocabulary: Vocabulary.Default);
        sys.Workshops[0].AddJar(jar);
        sys.Workshops[0].AddLiquid("酒", 2);
        Assert.True(sys.Workshops[0].PourIn(jar, "酒").Success);
        // 新主台先被占满：搬过去的每一样都必然放不下，账本必须说清是"丢弃"而不是没发生。
        sys.Workshops[1].AddJar(new Jar("圆罐", id: 9003, vocabulary: Vocabulary.Default));
        // MaxLiquids 是**每种液体**的上限（不是总和）：要逼出液体丢弃，得先把同一种液体占满。
        sys.Workshops[1].AddLiquid("酒", 1);

        var started = sys.BeginSwitch(1);
        Assert.True(started.Started, started.Error);
        for (var i = 0; i < 8 && sys.Workshops[0].Jars.Count > 0; i++) sys.Tick();

        var discarded = sys.ResourceLedger.For(ResourceCauses.Discard);
        Assert.Contains(discarded, e => e.Kind == ResourceKinds.Jar && e.Amount == 1
                                       && e.From == "旧主台" && e.Reason == "新主台容量已满" && e.ResourceIdentity == "9002");
        Assert.Contains(discarded, e => e.Kind == ResourceKinds.Liquid && e.Name == "酒"
                                       && e.Amount == 1 && e.Reason == "新主台容量已满");
        Assert.DoesNotContain(sys.ResourceLedger.For(ResourceCauses.Transfer), e => e.From == "旧主台");
    }

    [Fact]
    public void SlaveRollbackRecordsWhatCameBackAndWhatWasLost()
    {
        var world = QuietWorld();
        world.Workshops =
        [
            new WorkshopOptions { Name = "主台", BaseMarks = ["圆罐"], MaxJars = 2 },
            new WorkshopOptions { Name = "从台", BaseMarks = ["圆罐"] }
        ];
        var sys = new JarSystem.Core.JarSystem(randomSeed: 47, world: world);
        var master = sys.Workshops[0];
        var slave = sys.Workshops[1];

        // 主台只剩一个罐位：从台回来的两只罐子收下一只、丢一只。
        master.AddJar(new Jar("圆罐", id: 9101, vocabulary: Vocabulary.Default));
        slave.AddJar(new Jar("圆罐", id: 9102, vocabulary: Vocabulary.Default));
        slave.AddJar(new Jar("圆罐", id: 9103, vocabulary: Vocabulary.Default));
        slave.AddLiquid("蜜", 2);

        var log = new List<ExecutionLogEntry>();
        new SlaveRollback().Execute(master, slave, log.Add,
            ledger: sys.ResourceLedger, tickOf: () => 0);

        var returned = sys.ResourceLedger.For(ResourceCauses.Return);
        Assert.Contains(returned, e => e.Kind == ResourceKinds.Jar && e.Amount == 1
                                      && e.From == "从台" && e.To == "主台" && e.ResourceIdentity == "9102");
        Assert.Contains(returned, e => e.Kind == ResourceKinds.Liquid && e.Name == "蜜"
                                      && e.Amount == 2 && e.From == "从台" && e.To == "主台" && e.ResourceIdentity == "蜜");
        var lost = sys.ResourceLedger.For(ResourceCauses.Discard);
        Assert.Contains(lost, e => e.Kind == ResourceKinds.Jar && e.Amount == 1
                                  && e.Reason == "主台罐子容量满" && e.ResourceIdentity == "9103");
        Assert.All(lost, e => Assert.False(string.IsNullOrEmpty(e.Reason)));
    }

    [Fact]
    public void ProducerModuleReplacementAndWorldResetKeepTheInjectedJarIdentitySource()
    {
        var world = QuietWorld();
        world.Producers = [new ProducerOptions { Name = "陶工", Rates = new() { ["圆罐"] = 1.0 } }];
        var ids = new SequentialIdGenerator();
        var sys = new JarSystem.Core.JarSystem(
            randomSeed: 29, world: world, jarIds: ids, orderIds: new SequentialIdGenerator());

        var beforeReplacement = ids.CurrentValue;
        sys.ResetProducerModule();
        var firstJar = new Jar("圆罐", vocabulary: sys.Vocabulary);
        sys.ProducerModule.Warehouse.StoreJars([firstJar]);
        Assert.Equal(beforeReplacement + 1, firstJar.Id);
        Assert.Equal(beforeReplacement + 1, ids.CurrentValue);

        var beforeWorldReset = ids.CurrentValue;
        sys.Reset();
        var secondJar = new Jar("圆罐", vocabulary: sys.Vocabulary);
        sys.ProducerModule.Warehouse.StoreJars([secondJar]);
        Assert.Equal(beforeWorldReset + 1, secondJar.Id);
        Assert.Equal(beforeWorldReset + 1, ids.CurrentValue);
    }

}

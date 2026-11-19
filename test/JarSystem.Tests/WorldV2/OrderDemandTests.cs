using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class OrderDemandTests
{
    private static readonly Vocabulary Words = new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static OrderDemand CreateDemand(
        IIdGenerator ids,
        long cooldown = 0,
        string type = Constants.OrderType.A,
        params Requirement[] requirements)
    {
        var actual = requirements.Length == 0
            ? new[] { new Requirement { Shape = "圆罐" } }
            : requirements;
        return new OrderDemand(actual, type, cooldown, ids, Words);
    }

    private static OrderDemandBoard CreateBoard(
        DemandAreaFulfillmentPolicy passive = DemandAreaFulfillmentPolicy.OneShot,
        DemandAreaFulfillmentPolicy active = DemandAreaFulfillmentPolicy.OneShot) =>
        new(passive, active);

    [Fact]
    public void DemandCopiesAndValidatesItsImmutableRequirements()
    {
        var source = new List<Requirement>
        {
            new() { Shape = "圆罐" },
            new() { Shape = "方罐", Liquid = "酒" }
        };
        var demand = new OrderDemand(source, Constants.OrderType.B, 1_000,
            new SequentialIdGenerator(), Words);

        source.Clear();

        Assert.Equal(2, demand.Requirements.Count);
        Assert.Equal("圆罐", demand.Requirements[0].Shape);
        Assert.Equal("酒", demand.Requirements[1].Liquid);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<Requirement>)demand.Requirements).Add(new Requirement { Shape = "圆罐" }));
    }

    [Fact]
    public void DemandRejectsInvalidTypeVocabularyAndRequirementOrdering()
    {
        var ids = new SequentialIdGenerator();

        Assert.Throws<DomainValidationException>(() => CreateDemand(ids, type: "C"));
        Assert.Throws<DomainValidationException>(() =>
            new OrderDemand(Array.Empty<Requirement>(), Constants.OrderType.A, 0, ids, Words));
        Assert.Throws<DomainValidationException>(() =>
            new OrderDemand(new[] { new Requirement { Shape = "不存在" } },
                Constants.OrderType.A, 0, ids, Words));
        Assert.Throws<DomainValidationException>(() =>
            new OrderDemand(new[]
            {
                new Requirement { Shape = "圆罐", Liquid = "酒" },
                new Requirement { Shape = "方罐" }
            }, Constants.OrderType.A, 0, ids, Words));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDemand(ids, cooldown: -1));
    }

    [Fact]
    public void BoardKeepsPassiveAndActiveAreasUsingTheSameDemandModel()
    {
        var ids = new SequentialIdGenerator();
        var board = CreateBoard();
        var passive = CreateDemand(ids);
        var active = CreateDemand(ids, type: Constants.OrderType.B);

        board.AddDemand(passive, DemandAreaKind.Passive);
        board.AddDemand(active, DemandAreaKind.Active);

        Assert.Same(passive, Assert.Single(board.Passive.Demands));
        Assert.Same(active, Assert.Single(board.Active.Demands));
        Assert.True(board.Passive.AutoExecute);
        Assert.False(board.Active.AutoExecute);
        Assert.Equal(2, board.Count);
        Assert.Equal(2, board.AllDemands.Count);
    }

    [Fact]
    public void AreaFulfillmentPolicyIsIndependentFromPassiveOrActiveKind()
    {
        var ids = new SequentialIdGenerator();
        var board = CreateBoard(
            DemandAreaFulfillmentPolicy.OneShot,
            DemandAreaFulfillmentPolicy.Continuous);
        var passiveOneShot = CreateDemand(ids);
        var activeContinuous = CreateDemand(ids);
        board.AddDemand(passiveOneShot, DemandAreaKind.Passive);
        board.AddDemand(activeContinuous, DemandAreaKind.Active);

        Assert.True(board.Passive.AutoExecute);
        Assert.False(board.Active.AutoExecute);
        Assert.Equal(DemandFulfillmentResult.RemovedFromArea,
            board.RecordSuccessfulFulfillment(passiveOneShot.Id));
        Assert.Equal(DemandFulfillmentResult.RetainedInArea,
            board.RecordSuccessfulFulfillment(activeContinuous.Id));
        Assert.Null(board.Find(passiveOneShot.Id));
        Assert.Same(activeContinuous, board.Find(activeContinuous.Id));

        var reverse = CreateBoard(
            DemandAreaFulfillmentPolicy.Continuous,
            DemandAreaFulfillmentPolicy.OneShot);
        var passiveContinuous = CreateDemand(ids);
        var activeOneShot = CreateDemand(ids);
        reverse.AddDemand(passiveContinuous, DemandAreaKind.Passive);
        reverse.AddDemand(activeOneShot, DemandAreaKind.Active);

        Assert.Equal(DemandFulfillmentResult.RetainedInArea,
            reverse.RecordSuccessfulFulfillment(passiveContinuous.Id));
        Assert.Equal(DemandFulfillmentResult.RemovedFromArea,
            reverse.RecordSuccessfulFulfillment(activeOneShot.Id));
    }

    [Fact]
    public void SuccessfulFulfillmentOnlyUnloadsDemandAndDoesNotCloseOrDestroyIt()
    {
        var ids = new SequentialIdGenerator();
        var board = CreateBoard(DemandAreaFulfillmentPolicy.OneShot);
        var demand = CreateDemand(ids);
        board.AddDemand(demand, DemandAreaKind.Passive);

        Assert.Equal(DemandFulfillmentResult.RemovedFromArea,
            board.RecordSuccessfulFulfillment(demand.Id));
        Assert.Empty(board.Passive.Demands);
        Assert.Equal("圆罐", Assert.Single(demand.Requirements).Shape);
        Assert.True(demand.ReadyForSolve); // CD 状态仍是订单对象自己的状态，不等于区域归属
        Assert.Equal(DemandFulfillmentResult.NotFound,
            board.RecordSuccessfulFulfillment(demand.Id));
    }

    [Fact]
    public void DemandIdsMustBeUniqueAcrossBothAreas()
    {
        var board = CreateBoard();
        var first = CreateDemand(new SequentialIdGenerator());
        var duplicate = CreateDemand(new SequentialIdGenerator());

        Assert.Equal(first.Id, duplicate.Id);
        board.AddDemand(first, DemandAreaKind.Passive);

        Assert.Throws<InvalidOperationException>(() =>
            board.AddDemand(duplicate, DemandAreaKind.Active));
        Assert.Single(board.Passive.Demands);
        Assert.Empty(board.Active.Demands);
    }

    [Fact]
    public void PassiveOrderIsThePriorityAndCanBeMovedWithoutPriorityField()
    {
        var ids = new SequentialIdGenerator();
        var board = CreateBoard();
        var first = CreateDemand(ids);
        var second = CreateDemand(ids);
        var third = CreateDemand(ids);

        board.AddDemand(first, DemandAreaKind.Passive);
        board.AddDemand(second, DemandAreaKind.Passive);
        board.AddDemand(third, DemandAreaKind.Passive);

        Assert.True(board.MovePassiveDemand(third.Id, 0));
        Assert.Equal(new[] { third.Id, first.Id, second.Id },
            board.Passive.Demands.Select(x => x.Id));

        Assert.False(board.MovePassiveDemand(999_999, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            board.MovePassiveDemand(first.Id, 3));
    }

    [Fact]
    public void MovingDemandBetweenAreasAppendsItToTheTargetArea()
    {
        var board = CreateBoard();
        var ids = new SequentialIdGenerator();
        var passive = CreateDemand(ids);
        var active = CreateDemand(ids);
        board.AddDemand(passive, DemandAreaKind.Passive);
        board.AddDemand(active, DemandAreaKind.Active);

        Assert.True(board.MoveToArea(passive.Id, DemandAreaKind.Active));
        Assert.Empty(board.Passive.Demands);
        Assert.Equal(new[] { active.Id, passive.Id }, board.Active.Demands.Select(x => x.Id));

        Assert.True(board.MoveToArea(passive.Id, DemandAreaKind.Active));
        Assert.Equal(2, board.Active.Demands.Count);
        Assert.True(board.MoveToArea(active.Id, DemandAreaKind.Passive));
        Assert.Same(active, Assert.Single(board.Passive.Demands));
    }

    [Fact]
    public void EachDemandHasAnIndependentCooldownAndReportsItsOwnExpiry()
    {
        var ids = new SequentialIdGenerator();
        var board = CreateBoard();
        var shortCooldown = CreateDemand(ids, cooldown: 2_000);
        var longCooldown = CreateDemand(ids, cooldown: 5_000);
        board.AddDemand(shortCooldown, DemandAreaKind.Passive);
        board.AddDemand(longCooldown, DemandAreaKind.Active);

        Assert.True(shortCooldown.EnterCooldown());
        Assert.True(longCooldown.EnterCooldown());
        Assert.False(shortCooldown.ReadyForSolve);
        Assert.False(longCooldown.ReadyForSolve);
        Assert.False(shortCooldown.EnterCooldown());

        Assert.Empty(board.AdvanceLogicalTime(1_000));
        Assert.Equal(1_000, shortCooldown.CooldownRemainingLogicalMilli);
        Assert.Equal(4_000, longCooldown.CooldownRemainingLogicalMilli);

        Assert.Equal(new[] { shortCooldown.Id }, board.AdvanceLogicalTime(1_000));
        Assert.True(shortCooldown.ReadyForSolve);
        Assert.False(longCooldown.ReadyForSolve);

        Assert.Equal(new[] { longCooldown.Id }, board.AdvanceLogicalTime(4_000));
        Assert.True(longCooldown.ReadyForSolve);
    }

    [Fact]
    public void ZeroCooldownDoesNotCreateAWaitingPeriod()
    {
        var demand = CreateDemand(new SequentialIdGenerator(), cooldown: 0);

        Assert.False(demand.EnterCooldown());
        Assert.True(demand.ReadyForSolve);
    }

    [Fact]
    public void NegativeBoardTimeIsRejectedBeforeAnyDemandCooldownChanges()
    {
        var ids = new SequentialIdGenerator();
        var board = CreateBoard();
        var first = CreateDemand(ids, cooldown: 2_000);
        var second = CreateDemand(ids, cooldown: 3_000);
        board.AddDemand(first, DemandAreaKind.Passive);
        board.AddDemand(second, DemandAreaKind.Active);
        first.EnterCooldown();
        second.EnterCooldown();

        Assert.Throws<ArgumentOutOfRangeException>(() => board.AdvanceLogicalTime(-1));
        Assert.Equal(2_000, first.CooldownRemainingLogicalMilli);
        Assert.Equal(3_000, second.CooldownRemainingLogicalMilli);
    }

    [Fact]
    public void RuntimeAdvancesDemandCooldownWithTheSameLogicalTimeAsResources()
    {
        var ids = new SequentialIdGenerator();
        var board = CreateBoard();
        var demand = CreateDemand(ids, cooldown: 2_000);
        board.AddDemand(demand, DemandAreaKind.Passive);
        demand.EnterCooldown();

        var vocabulary = new Vocabulary(new[] { "圆罐" }, new[] { "酒", "蜜" });
        var resources = new ResourceArea(
            new ProducerModule(vocabulary),
            new LiquidProduction(vocabulary,
                defaults: new Dictionary<string, double> { ["酒"] = 0, ["蜜"] = 0 }),
            jarFlowRate: 0,
            liquidFlowRate: 0);
        var clock = new WorldClock();
        clock.SetSpeedRatio(2);
        var runtime = new ResourceWorldRuntime(resources, clock, demandBoard: board);

        var step = runtime.AdvanceWorldTime(500, master: null);

        Assert.Equal(1_000, step.LogicalTimeDeltaMilli);
        Assert.Equal(1_000, demand.CooldownRemainingLogicalMilli);

        runtime.Clock.Pause();
        runtime.AdvanceWorldTime(5_000, master: null);
        Assert.Equal(1_000, demand.CooldownRemainingLogicalMilli);

        runtime.Clock.Resume();
        runtime.Clock.SetSpeedRatio(0);
        runtime.AdvanceWorldTime(5_000, master: null);
        Assert.Equal(1_000, demand.CooldownRemainingLogicalMilli);

        runtime.Clock.SetSpeedRatio(1);
        runtime.AdvanceWorldTime(1_000, master: null);
        Assert.True(demand.ReadyForSolve);
    }
}

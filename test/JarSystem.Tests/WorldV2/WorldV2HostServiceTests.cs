using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JarSystem.Core.World;
using JarSystem.Core.Config;
using JarSystem.Core.Production;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldV2HostServiceTests
{
    private static WorldV2Options ValidOptions(string masterName = "主台") => new()
    {
        Shapes = new List<string> { "圆罐", "方罐" },
        Liquids = new List<string> { "酒", "蜜" },
        Producers = new List<WorldV2ProducerOptions>
        {
            new()
            {
                Name = "陶工",
                Rates = new Dictionary<string, double> { ["圆罐"] = 1.0 }
            }
        },
        LiquidProductionRates = new Dictionary<string, double>
        {
            ["酒"] = 0.5,
            ["蜜"] = 0
        },
        InitialLiquidStock = new Dictionary<string, int> { ["酒"] = 3 },
        JarFlowRatePerLogicalTime = 1,
        LiquidFlowRatePerLogicalTime = 1,
        Workshops = new List<WorldV2WorkshopOptions>
        {
            new()
            {
                Name = masterName,
                BaseMarks = new List<string> { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10
            }
        },
        MasterWorkshopName = masterName,
        ExecutionLogicalTimePerUnit = 1000,
        PassiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot,
        ActiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot
    };

    [Fact]
    public void ReplaceCreatesAndPublishesInitialWorld()
    {
        using var host = new WorldV2HostService();

        Assert.False(host.HasWorld);
        Assert.Throws<InvalidOperationException>(() =>
            host.ExecuteExclusive(_ => { }));

        host.Replace(ValidOptions());

        string? masterName = null;
        host.ExecuteExclusive(context => masterName = context.Roster.Master!.Name);

        Assert.True(host.HasWorld);
        Assert.Equal("主台", masterName);
    }

    [Fact]
    public void SuccessfulReplaceDisposesPreviousWorldAfterPublishingNewWorld()
    {
        using var host = new WorldV2HostService();
        host.Replace(ValidOptions("第一主台"));

        WorldV2Context? previous = null;
        host.ExecuteExclusive(context => previous = context);

        host.Replace(ValidOptions("第二主台"));

        Assert.NotNull(previous);
        Assert.True(previous!.IsDisposed);

        string? currentMaster = null;
        host.ExecuteExclusive(context => currentMaster = context.Roster.Master!.Name);
        Assert.Equal("第二主台", currentMaster);
    }

    [Fact]
    public void CheckpointRestorePublishesOnlyFullyRestoredContextAndDisposesPrevious()
    {
        using var host = new WorldV2HostService();
        var options = ValidOptions();
        host.Replace(options);
        var application = new WorldV2ApplicationService(host);
        var checkpoint = application.CaptureExecutionResourceCheckpoint();

        WorldV2Context? previous = null;
        host.ExecuteExclusive(context => previous = context);

        application.RestoreExecutionResourceCheckpoint(ValidOptions(), checkpoint);

        WorldV2Context? restored = null;
        host.ExecuteExclusive(context => restored = context);
        Assert.NotNull(restored);
        Assert.NotSame(previous, restored);
        Assert.True(previous!.IsDisposed);
        Assert.False(restored!.IsDisposed);
        Assert.Equal("主台", restored.Roster.Master!.Name);

        var tamperedVocabulary = new WorldV2ExecutionResourceCheckpoint(
            checkpoint.ResourceArea, checkpoint.DemandBoard, checkpoint.ConfigurationFingerprint,
            checkpoint.ShipLiquidInventory, checkpoint.MasterWorkshop, checkpoint.SlaveWorkshop,
            checkpoint.Admission, checkpoint.Reservations, checkpoint.ReservationLeases,
            checkpoint.ResourceLedger, checkpoint.Clock, checkpoint.RuntimeLedgerTick,
            new[] { "伪造形状", "方罐" }, checkpoint.Liquids, checkpoint.Sessions);
        Assert.Throws<InvalidOperationException>(() =>
            application.RestoreExecutionResourceCheckpoint(ValidOptions(), tamperedVocabulary));

        var incompatible = ValidOptions();
        incompatible.InitialLiquidStock = new Dictionary<string, int> { ["酒"] = 4 };
        Assert.Throws<InvalidOperationException>(() =>
            application.RestoreExecutionResourceCheckpoint(incompatible, checkpoint));

        WorldV2Context? afterFailure = null;
        host.ExecuteExclusive(context => afterFailure = context);
        Assert.Same(restored, afterFailure);
        Assert.False(restored.IsDisposed);
    }

    [Fact]
    public void HostCheckpointRestoreResumesBOrderAfterPumpWithoutRepeatingTransferOrFunding()
    {
        var options = ValidOptions();
        // 两个岗位各自只能完成一种形状，确保真实 B 计划同时包含主台步骤与从台步骤。
        options.Producers[0].Rates["方罐"] = 1.0;
        options.LiquidProductionRates["酒"] = 0;
        options.JarFlowRatePerLogicalTime = 2;
        options.Workshops[0].BaseMarks = new List<string> { "方罐" };
        options.Workshops.Add(new WorldV2WorkshopOptions
        {
            Name = "从台",
            BaseMarks = new List<string> { "圆罐" },
            MaxJars = 10,
            MaxLiquids = 10,
            LiquidStagingCapacity = 10
        });
        options.SlaveWorkshopName = "从台";

        using var host = new WorldV2HostService();
        host.Replace(options);
        var application = new WorldV2ApplicationService(host);

        // 通过真实生产区→仓库→主台链路准备两种岗位各自需要的罐子。
        application.AdvanceWorld(1_000);
        host.ExecuteExclusive(context => Assert.Equal(2, context.Roster.Master!.Jars.Count));

        var demandId = application.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[]
            {
                new WorldV2RequirementInput("方罐", "酒"),
                new WorldV2RequirementInput("圆罐", "酒")
            },
            Constants.OrderType.B,
            DemandAreaKind.Active,
            CooldownDurationLogicalMilli: 0));
        var transferEventsBeforeOrder = 0;
        host.ExecuteExclusive(context =>
            transferEventsBeforeOrder = context.ResourceLedger.For(ResourceCauses.Transfer)
                .Count(item => item.Kind == ResourceKinds.Liquid));

        var started = application.StartActiveDemand(demandId);
        Assert.True(started.Success, started.Reason);

        // Checkpoint A：订单已准入并扣除自机库存，但主台→从台尚未泵送。
        var beforePump = application.CaptureExecutionResourceCheckpoint();
        var beforePumpWire = WorldV2ExecutionCheckpointWireDto.FromCheckpoint(beforePump);
        Assert.Contains("方罐", beforePumpWire.Shapes);
        Assert.Contains("蜜", beforePumpWire.Liquids);
        var beforePumpJson = WorldV2CheckpointJsonCodec.Encode(
            beforePumpWire, beforePump.ConfigurationFingerprint);
        var decodedWire = WorldV2CheckpointJsonCodec
            .Decode<WorldV2ExecutionCheckpointWireDto>(beforePumpJson).Payload;
        decodedWire.Validate();
        beforePump = WorldV2CheckpointJsonCodec.DecodeExecutionResourceCheckpoint(beforePumpJson);
        Assert.Equal(transferEventsBeforeOrder, beforePump.ResourceLedger.Entries.Count(item =>
            item.Cause == ResourceCauses.Transfer && item.Kind == ResourceKinds.Liquid));
        Assert.Equal(1, beforePump.ShipLiquidInventory.Balances["酒"]);

        WorldV2Context? previous = null;
        host.ExecuteExclusive(context => previous = context);
        application.RestoreExecutionResourceCheckpoint(options, beforePump);
        Assert.True(previous!.IsDisposed);
        host.ExecuteExclusive(context =>
        {
            var session = Assert.Single(context.Runtime.ActiveExecutionSessions);
            Assert.Equal(WorldOrderExecutionStage.MasterSteps, session.Stage);
            var scopes = context.Admission.GetLiquidScopes(demandId)!.Value;
            Assert.Equal(2, scopes.Master.GetAmount("酒"));
            Assert.Equal(0, scopes.Slave!.GetAmount("酒"));
            Assert.Equal(transferEventsBeforeOrder,
                context.ResourceLedger.For(ResourceCauses.Transfer).Count(item => item.Kind == ResourceKinds.Liquid));
            Assert.Equal(1, context.ShipLiquidInventory!.GetAmount("酒"));
        });

        WorldOrderExecutionStage stage = WorldOrderExecutionStage.MasterSteps;
        for (var i = 0; i < 12 && stage != WorldOrderExecutionStage.SlaveSteps; i++)
        {
            application.AdvanceWorld(1_000);
            host.ExecuteExclusive(context =>
                stage = Assert.Single(context.Runtime.ActiveExecutionSessions).Stage);
        }
        Assert.Equal(WorldOrderExecutionStage.SlaveSteps, stage);

        // Checkpoint B：交接已提交、从台拥有订单液体。恢复不得再次泵送。
        var afterPump = application.CaptureExecutionResourceCheckpoint();
        var afterPumpJson = WorldV2CheckpointJsonCodec.EncodeExecutionResourceCheckpoint(afterPump);
        afterPump = WorldV2CheckpointJsonCodec.DecodeExecutionResourceCheckpoint(afterPumpJson);
        Assert.Equal(transferEventsBeforeOrder + 1, afterPump.ResourceLedger.Entries.Count(item =>
            item.Cause == ResourceCauses.Transfer && item.Kind == ResourceKinds.Liquid));
        Assert.Equal(1, afterPump.ShipLiquidInventory.Balances["酒"]);

        host.ExecuteExclusive(context => previous = context);
        application.RestoreExecutionResourceCheckpoint(options, afterPump);
        Assert.True(previous!.IsDisposed);

        host.ExecuteExclusive(context =>
        {
            var session = Assert.Single(context.Runtime.ActiveExecutionSessions);
            Assert.Equal(WorldOrderExecutionStage.SlaveSteps, session.Stage);
            Assert.True(session.CaptureProgress().SlaveTransferCommitted);
            Assert.Equal(0, context.Admission.GetLiquidScopes(demandId)!.Value.Master.GetAmount("酒"));
            Assert.Equal(1, context.Admission.GetLiquidScopes(demandId)!.Value.Slave!.GetAmount("酒"));
            Assert.Equal(transferEventsBeforeOrder + 1,
                context.ResourceLedger.For(ResourceCauses.Transfer).Count(item => item.Kind == ResourceKinds.Liquid));
            Assert.Equal(1, context.ShipLiquidInventory!.GetAmount("酒"));
        });

        for (var i = 0; i < 12; i++)
        {
            var active = false;
            host.ExecuteExclusive(context => active = context.Runtime.ActiveExecutionSessions.Count != 0);
            if (!active) break;
            application.AdvanceWorld(1_000);
        }

        host.ExecuteExclusive(context =>
        {
            Assert.Empty(context.Runtime.ActiveExecutionSessions);
            Assert.Null(context.Reservations.FindLease(demandId));
            Assert.Empty(context.Roster.Slave!.Jars);
            Assert.Equal(1, context.ShipLiquidInventory!.GetAmount("酒"));
            Assert.Equal(transferEventsBeforeOrder + 1,
                context.ResourceLedger.For(ResourceCauses.Transfer).Count(item => item.Kind == ResourceKinds.Liquid));
        });
    }

    [Fact]
    public void FailedReplaceKeepsPreviousWorldCurrentAndAlive()
    {
        using var host = new WorldV2HostService();
        host.Replace(ValidOptions());

        WorldV2Context? previous = null;
        host.ExecuteExclusive(context => previous = context);

        var invalid = ValidOptions();
        invalid.MasterWorkshopName = "不存在";

        Assert.Throws<ArgumentException>(() => host.Replace(invalid));

        string? currentMaster = null;
        host.ExecuteExclusive(context => currentMaster = context.Roster.Master!.Name);

        Assert.Equal("主台", currentMaster);
        Assert.False(previous!.IsDisposed);
    }

    [Fact]
    public void DisposeIsIdempotentAndDisposesCurrentWorld()
    {
        var host = new WorldV2HostService();
        host.Replace(ValidOptions());

        WorldV2Context? current = null;
        host.ExecuteExclusive(context => current = context);

        host.Dispose();
        host.Dispose();

        Assert.False(host.HasWorld);
        Assert.True(current!.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() =>
            host.ExecuteExclusive(_ => { }));
        Assert.Throws<ObjectDisposedException>(() => host.Replace(ValidOptions()));
    }

    [Fact]
    public void ExclusiveOperationRejectsNestedExecutionAndLifecycleChanges()
    {
        using var host = new WorldV2HostService();
        host.Replace(ValidOptions());

        host.ExecuteExclusive(_ =>
        {
            Assert.Throws<InvalidOperationException>(() =>
                host.ExecuteExclusive(__ => { }));
            Assert.Throws<InvalidOperationException>(() =>
                host.Replace(ValidOptions("嵌套替换")));
            Assert.Throws<InvalidOperationException>(() => host.Dispose());
        });

        Assert.True(host.HasWorld);
    }

    [Fact]
    public void DifferentHostsHaveIndependentOperationBoundaries()
    {
        using var firstHost = new WorldV2HostService();
        using var secondHost = new WorldV2HostService();
        firstHost.Replace(ValidOptions("第一主台"));
        secondHost.Replace(ValidOptions("第二主台"));

        string? firstName = null;
        string? secondName = null;
        firstHost.ExecuteExclusive(context => firstName = context.Roster.Master!.Name);
        secondHost.ExecuteExclusive(context => secondName = context.Roster.Master!.Name);

        Assert.Equal("第一主台", firstName);
        Assert.Equal("第二主台", secondName);
    }

    [Fact]
    public async Task OperationsAndReplacementAreSerialized()
    {
        using var host = new WorldV2HostService();
        host.Replace(ValidOptions("旧主台"));

        var cancellationToken = TestContext.Current.CancellationToken;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementAttempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        WorldV2Context? activeContext = null;

        var firstOperation = Task.Run(() => host.ExecuteExclusive(context =>
        {
            activeContext = context;
            Assert.Equal("旧主台", context.Roster.Master!.Name);
            firstEntered.TrySetResult();
            if (!releaseFirst.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("测试未释放第一个独占操作");
        }), cancellationToken);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        var secondOperation = Task.Run(() =>
        {
            secondAttempting.TrySetResult();
            host.ExecuteExclusive(_ => secondEntered.TrySetResult());
        }, cancellationToken);

        await secondAttempting.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await Task.Delay(100, cancellationToken);
        Assert.False(secondEntered.Task.IsCompleted);

        var replacement = Task.Run(() =>
        {
            replacementAttempting.TrySetResult();
            host.Replace(ValidOptions("新主台"));
        }, cancellationToken);

        await replacementAttempting.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await Task.Delay(100, cancellationToken);
        Assert.False(replacement.IsCompleted);
        Assert.False(activeContext!.IsDisposed);

        releaseFirst.Set();
        await firstOperation.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await secondOperation.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await replacement.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.True(secondEntered.Task.IsCompleted);
        Assert.True(activeContext.IsDisposed);
        string? currentMaster = null;
        host.ExecuteExclusive(context => currentMaster = context.Roster.Master!.Name);
        Assert.Equal("新主台", currentMaster);
    }

    [Fact]
    public async Task DisposeWaitsForActiveWorldOperation()
    {
        var host = new WorldV2HostService();
        host.Replace(ValidOptions());

        var cancellationToken = TestContext.Current.CancellationToken;
        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeAttempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOperation = new ManualResetEventSlim();
        WorldV2Context? activeContext = null;

        var operation = Task.Run(() => host.ExecuteExclusive(context =>
        {
            activeContext = context;
            operationEntered.TrySetResult();
            if (!releaseOperation.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("测试未释放世界操作");
        }), cancellationToken);

        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        var dispose = Task.Run(() =>
        {
            disposeAttempting.TrySetResult();
            host.Dispose();
        }, cancellationToken);

        await disposeAttempting.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await Task.Delay(100, cancellationToken);
        Assert.False(dispose.IsCompleted);
        Assert.False(activeContext!.IsDisposed);

        releaseOperation.Set();
        await operation.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.False(host.HasWorld);
        Assert.True(activeContext.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() =>
            host.ExecuteExclusive(_ => { }));
    }
}

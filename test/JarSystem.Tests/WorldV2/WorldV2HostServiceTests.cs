using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JarSystem.Core.World;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;
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

        host.ExecuteExclusive(context =>
        {
            var vocabulary = new Vocabulary(
                context.Resources.Producers.Generators.Keys,
                context.Resources.Inventory.LiquidWarehouse.Keys);
            context.Roster.Master!.AddJar(new Jar("圆罐", id: 501, vocabulary: vocabulary));
        });

        var demandId = application.AddJarDemand(new WorldV2AddJarDemandRequest(
            new[] { new WorldV2RequirementInput("圆罐", "酒") },
            Constants.OrderType.B,
            DemandAreaKind.Active,
            CooldownDurationLogicalMilli: 0));
        var started = application.StartActiveDemand(demandId);
        Assert.True(started.Success, started.Reason);

        WorldOrderExecutionStage stage = WorldOrderExecutionStage.MasterSteps;
        for (var i = 0; i < 12 && stage != WorldOrderExecutionStage.SlaveSteps; i++)
        {
            application.AdvanceWorld(1_000);
            host.ExecuteExclusive(context =>
                stage = Assert.Single(context.Runtime.ActiveExecutionSessions).Stage);
        }
        Assert.Equal(WorldOrderExecutionStage.SlaveSteps, stage);

        var checkpoint = application.CaptureExecutionResourceCheckpoint();
        var beforeRestore = checkpoint.ResourceLedger.Entries;
        Assert.Single(beforeRestore, item =>
            item.Cause == ResourceCauses.Transfer && item.Kind == ResourceKinds.Liquid);
        Assert.Single(beforeRestore, item => item.Cause == ResourceCauses.Flow &&
            item.Kind == ResourceKinds.Liquid);

        WorldV2Context? previous = null;
        host.ExecuteExclusive(context => previous = context);
        application.RestoreExecutionResourceCheckpoint(options, checkpoint);
        Assert.True(previous!.IsDisposed);

        host.ExecuteExclusive(context =>
        {
            var session = Assert.Single(context.Runtime.ActiveExecutionSessions);
            Assert.Equal(WorldOrderExecutionStage.SlaveSteps, session.Stage);
            Assert.True(session.CaptureProgress().SlaveTransferCommitted);
            Assert.Equal(1, context.Admission.GetLiquidScopes(demandId)!.Value.Slave!.GetAmount("酒"));
            Assert.Equal(1, context.ResourceLedger.For(ResourceCauses.Transfer)
                .Count(item => item.Kind == ResourceKinds.Liquid));
            Assert.Equal(1, context.ResourceLedger.For(ResourceCauses.Flow)
                .Count(item => item.Kind == ResourceKinds.Liquid));
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
            Assert.Equal(2, context.ShipLiquidInventory!.GetAmount("酒"));
            Assert.Single(context.ResourceLedger.For(ResourceCauses.Transfer),
                item => item.Kind == ResourceKinds.Liquid);
            Assert.Single(context.ResourceLedger.For(ResourceCauses.Flow),
                item => item.Kind == ResourceKinds.Liquid);
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

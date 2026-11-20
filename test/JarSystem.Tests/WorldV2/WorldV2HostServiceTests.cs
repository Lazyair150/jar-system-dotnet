using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
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
    public void ReplaceCreatesAndPublishesInitialWorldLease()
    {
        using var host = new WorldV2HostService();

        Assert.False(host.HasWorld);
        Assert.Throws<InvalidOperationException>(() => host.AcquireCurrent());

        using var created = host.Replace(ValidOptions());

        Assert.True(host.HasWorld);
        using var current = host.AcquireCurrent();
        Assert.Same(created.Context, current.Context);
        Assert.False(created.Context.IsDisposed);
    }

    [Fact]
    public void SuccessfulReplaceRetiresPreviousWorldButWaitsForItsLeases()
    {
        using var host = new WorldV2HostService();
        var first = host.Replace(ValidOptions("第一主台"));
        var firstContext = first.Context;

        using var second = host.Replace(ValidOptions("第二主台"));

        Assert.NotSame(firstContext, second.Context);
        Assert.Equal("第二主台", second.Context.Roster.Master!.Name);
        Assert.False(firstContext.IsDisposed);

        first.Dispose();
        Assert.True(firstContext.IsDisposed);
        Assert.False(second.Context.IsDisposed);
    }

    [Fact]
    public void FailedReplaceKeepsPreviousWorldCurrentAndAlive()
    {
        using var host = new WorldV2HostService();
        using var previous = host.Replace(ValidOptions());
        var previousContext = previous.Context;

        var invalid = ValidOptions();
        invalid.MasterWorkshopName = "不存在";

        Assert.Throws<ArgumentException>(() => host.Replace(invalid));

        using var current = host.AcquireCurrent();
        Assert.Same(previousContext, current.Context);
        Assert.False(previousContext.IsDisposed);
    }

    [Fact]
    public void HostDisposeDefersContextDisposalUntilLastLeaseIsReleased()
    {
        var host = new WorldV2HostService();
        var lease = host.Replace(ValidOptions());
        var context = lease.Context;

        host.Dispose();
        host.Dispose();

        Assert.False(host.HasWorld);
        Assert.False(context.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => host.AcquireCurrent());
        Assert.Throws<ObjectDisposedException>(() => host.Replace(ValidOptions()));

        lease.Dispose();
        Assert.True(context.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Context);
    }


    [Fact]
    public void ExclusiveOperationsAreSerializedAndReplacementWaitsForActiveOperation()
    {
        using var host = new WorldV2HostService();
        using var initial = host.Replace(ValidOptions("旧主台"));

        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondAttempting = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var replacementAttempting = new ManualResetEventSlim();

        var firstOperation = Task.Run(() => host.ExecuteExclusive(context =>
        {
            Assert.Equal("旧主台", context.Roster.Master!.Name);
            firstEntered.Set();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
        }));

        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));

        var secondOperation = Task.Run(() =>
        {
            secondAttempting.Set();
            host.ExecuteExclusive(_ => secondEntered.Set());
        });

        Assert.True(secondAttempting.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(secondEntered.Wait(TimeSpan.FromMilliseconds(100)));

        var replacement = Task.Run(() =>
        {
            replacementAttempting.Set();
            using var next = host.Replace(ValidOptions("新主台"));
            return next.Context.Roster.Master!.Name;
        });

        Assert.True(replacementAttempting.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(replacement.IsCompleted);

        releaseFirst.Set();
        Assert.True(Task.WaitAll(new[] { firstOperation, secondOperation }, TimeSpan.FromSeconds(5)));
        Assert.True(secondEntered.IsSet);
        Assert.Equal("新主台", replacement.GetAwaiter().GetResult());
    }

    [Fact]
    public void ExclusiveOperationRejectsNestedExecutionAndLifecycleChanges()
    {
        using var host = new WorldV2HostService();
        using var current = host.Replace(ValidOptions());

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
    public void HostsAreIndependentAndLeaseReleaseIsIdempotent()
    {
        using var firstHost = new WorldV2HostService();
        using var secondHost = new WorldV2HostService();
        var first = firstHost.Replace(ValidOptions());
        using var second = secondHost.Replace(ValidOptions());

        var firstContext = first.Context;
        var secondContext = second.Context;

        firstHost.Dispose();
        first.Dispose();
        first.Dispose();

        Assert.True(firstContext.IsDisposed);
        Assert.False(secondContext.IsDisposed);
        using (var current = secondHost.AcquireCurrent())
            Assert.Same(secondContext, current.Context);

        // 释放调用方租约不会销毁仍由 Host 持有的当前世界。
        second.Dispose();
        Assert.False(secondContext.IsDisposed);

        // Host 退役当前世界后，因租约已归零而立即释放。
        secondHost.Dispose();
        Assert.True(secondContext.IsDisposed);
    }
}

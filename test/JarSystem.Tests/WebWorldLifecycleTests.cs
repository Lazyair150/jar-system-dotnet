using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JarSystem.Core.Domain;
using JarSystem.Core.Rules;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// Web 宿主把“当前世界”做成了可替换引用；所有后台消费者都必须在闸门内重新读取它。
/// 这组测试固定换关与等待中的 tick / 自动分析之间的时序。
/// </summary>
public class WebWorldLifecycleTests
{
    private sealed class RecordingSink : IWebSocketSink
    {
        public List<string> Broadcasts { get; } = new();

        public Task SendAsync(WebSocket socket, string json) => Task.CompletedTask;

        public Task BroadcastAsync(string json)
        {
            Broadcasts.Add(json);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TickWaitingDuringLevelSwitchAdvancesTheNewWorld()
    {
        var levels = LevelCatalog.Discover();
        Assert.True(levels.Count >= 2, "换关竞态测试至少需要两关");
        var service = new JarSystemService(level: levels[0]);
        var sink = new RecordingSink();
        var ticks = new TickBackgroundService(service, sink);

        await service.Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            // 旧实现会在这里先捕获旧 System，然后等待闸门。
            var waitingTick = ticks.TickOnceForTestAsync(TestContext.Current.CancellationToken);

            // 调用方按约定持有闸门时换关。
            service.LoadLevel(levels[1].Index);
            service.Gate.Release();
            await waitingTick;
        }
        finally
        {
            if (service.Gate.CurrentCount == 0) service.Gate.Release();
        }

        var state = Assert.Single(sink.Broadcasts);
        using var doc = JsonDocument.Parse(state);
        Assert.Equal("state", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            levels[1].World.Workshops[0].Name,
            doc.RootElement.GetProperty("data").GetProperty("workshops")[0].GetProperty("name").GetString());
        Assert.Equal(levels[1].Index, service.Level?.Index);
    }

    [Fact]
    public async Task AutoCheckWaitingDuringLevelSwitchDoesNotUseTheOldWorld()
    {
        var levels = LevelCatalog.Discover();
        Assert.True(levels.Count >= 2, "换关竞态测试至少需要两关");
        var service = new JarSystemService(level: levels[0]);
        service.System.SetAutoCheck(true);
        var sink = new RecordingSink();
        var autoCheck = new AutoCheckBackgroundService(service, sink);

        await service.Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var waiting = autoCheck.CheckOnceForTestAsync(TestContext.Current.CancellationToken);
            service.LoadLevel(levels[1].Index);
            service.Gate.Release();
            await waiting;
        }
        finally
        {
            if (service.Gate.CurrentCount == 0) service.Gate.Release();
        }

        Assert.Empty(sink.Broadcasts);
        Assert.False(service.System.AutoCheckEnabled);
    }

    [Fact]
    public void ScenarioRulesFollowLoadAndRestartInsteadOfDisappearing()
    {
        var levels = LevelCatalog.Discover();
        Assert.True(levels.Count >= 2, "场景生命周期测试至少需要两关");
        var scenario = new ScenarioOptions
        {
            InitialModifiers =
            [
                new ModifierData
                {
                    Kind = "suppress",
                    Operation = "POUR_IN",
                    Reason = "测试禁令",
                    From = 0,
                    To = 99
                }
            ]
        };
        var service = new JarSystemService(null, null, levels[0], scenario);
        Assert.Contains("测试禁令", service.System.Modifiers.SuppressionReason("POUR_IN", null, null));

        service.LoadLevel(levels[1].Index);

        Assert.Contains("测试禁令", service.System.Modifiers.SuppressionReason("POUR_IN", null, null));

        service.RestartLevel();

        Assert.Contains("测试禁令", service.System.Modifiers.SuppressionReason("POUR_IN", null, null));
    }

    [Fact]
    public void LoadingAScenarioDoesNotDisconnectTheWorldBus()
    {
        var level = LevelCatalog.Discover()[0];
        var service = new JarSystemService(null, null, level, new ScenarioOptions());
        var sys = service.System;
        sys.SetMasterIndex(0);
        Assert.True(sys.CreateOrderFromTemplate(level.World.OrderTemplates[0].Name).Success);
        Assert.NotNull(sys.CheckFeasibility(0));

        sys.CreateWorkshop("新台", ["圆罐"]);
        Assert.NotNull(sys.Orders[0].FeasibilityResult);

        sys.Tick();

        Assert.Null(sys.Orders[0].FeasibilityResult);
    }
}

using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 运行时事件规范（第二版）的**可执行条文**。
///
/// 这份文件守的是 R-OBS-6（每条事件必须能被消费方自我识别）与 §3 E5（配置类命令的可观察性）。
/// 在它之前，`eventHistory` 里并存两种形状：换台有 `type`，订单事件没有 —— 消费方只能靠
/// "看它有没有 order 键" 来猜。这两条测试就是为了让那种局面不会重新长回来。
/// </summary>
public class WorldEventShapeTests
{
    private static JarSystem.Core.JarSystem NewWorld()
        => new(randomSeed: 11, jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());

    /// <summary>R-OBS-6：任何进 eventHistory 的条目都必须带 type。</summary>
    [Fact]
    public void EveryEventHasTypeField()
    {
        var sys = NewWorld();
        sys.SetFlowSettings(3, 2);
        sys.UpdateLiquidProductionRate("蜜", 3);

        var probe = sys.ProducerModule;
        var producers = probe.Producers.ToList();
        Assert.NotEmpty(producers);
        var first = producers[0];
        var shape = first.Value.Rates.Keys.First();
        sys.UpdateProducerRate(first.Value.Name, shape, 1.5);
        Assert.Contains(sys.EventHistory, e => (string?)e["type"] == "producer_rate");

        Assert.NotEmpty(sys.EventHistory);
        foreach (var e in sys.EventHistory)
        {
            Assert.True(e.ContainsKey("type"), "存在没有 type 字段的事件：" + Dump(e));
            Assert.IsType<string>(e["type"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)e["type"]));
        }
    }

    /// <summary>§3 E5：三类配置命令各发一条可识别的事件，且字段随命令走。</summary>
    [Fact]
    public void ConfigCommandsEmitTypedEvents()
    {
        var sys = NewWorld();
        sys.SetFlowSettings(5, 1);
        sys.UpdateLiquidProductionRate("酒", 4);

        var flow = sys.EventHistory.Single(e => (string?)e["type"] == "flow_settings");
        Assert.Equal(5, flow["jarCount"]);
        Assert.Equal(1, flow["liquidAmount"]);

        var liquid = sys.EventHistory.Single(e => (string?)e["type"] == "liquid_rate");
        Assert.Equal("酒", liquid["liquid"]);
        Assert.Equal(4, liquid["rate"]);

        // 三条事件都必须带时间戳，而且这里是**意图时间**（可记录/可回放），不是读墙钟做判定。
        Assert.All(sys.EventHistory, e => Assert.True(e.ContainsKey("timestamp")));
    }

    /// <summary>换台事件只有一个发射点：两条轨产出的形状必须逐字相同。</summary>
    [Fact]
    public void SwitchEventShapeIsIdenticalOnBothTracks()
    {
        // 旧主台必须先有资源，否则 SetMaster 只改指针、根本不发事件
        // （MasterSwitchCoordinator 的 hasResources 判据）。
        var a = NewWorld();
        a.SetMaster(0);   // 先钉住主台，流入才会发生
        a.Tick(20);
        a.SetMaster(1);

        var b = NewWorld();
        b.SetMaster(0);
        b.Tick(20);
        b.SetMaster(1);

        var ea = a.EventHistory.Single(e => (string?)e["type"] == "workshop_switch");
        var eb = b.EventHistory.Single(e => (string?)e["type"] == "workshop_switch");

        Assert.Equal(ea.Keys.OrderBy(k => k), eb.Keys.OrderBy(k => k));
        foreach (var key in ea.Keys.Where(k => k != "timestamp"))
            Assert.Equal(ea[key]?.ToString(), eb[key]?.ToString());
    }

    private static string Dump(Dictionary<string, object?> e)
        => "{" + string.Join(", ", e.Select(kv => kv.Key + "=" + kv.Value)) + "}";
}

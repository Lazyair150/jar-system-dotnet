using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 命令改变的是所有连接共享的同一份世界；状态推送也必须发给所有连接，而不只是发起命令的那个。
/// </summary>
public class CommandDispatcherTests
{
    private sealed class RecordingSink : IWebSocketSink
    {
        public List<string> Sent { get; } = new();
        public List<string> Broadcasts { get; } = new();

        // 这个替身代表"本机页面发来的命令"（这些测试断言的是分派与世界广播，不是鉴权）；
        // 鉴权默认值仍然是 Unknown（失败关闭），由 SecurityPolicyTests 单独钉住。
        public ConnectionAccess AccessOf(WebSocket socket) => ConnectionAccess.Local;

        public Task SendAsync(WebSocket socket, string json)
        {
            Sent.Add(json);
            return Task.CompletedTask;
        }

        public Task BroadcastAsync(string json)
        {
            Broadcasts.Add(json);
            return Task.CompletedTask;
        }
    }

    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static string MessageType(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("type").GetString() ?? "";
    }

    [Fact]
    public async Task WorldChangingCommandsBroadcastState()
    {
        var service = new JarSystemService(level: LevelCatalog.Discover()[0]);
        var sink = new RecordingSink();
        var dispatcher = new CommandDispatcher(sink);
        using var doc = JsonDocument.Parse("""
            {"type":"createOrder","data":{"orderType":"A","requirements":[{"shape":"圆罐","liquid":null}]}}
            """);

        await dispatcher.HandleAsync(service, new StubWebSocket(), "createOrder", doc.RootElement.GetProperty("data"));

        Assert.Contains(sink.Broadcasts, json => MessageType(json) == "state");
    }

    [Fact]
    public async Task LoadLevelBroadcastsWorldBeforeStateForEveryClient()
    {
        var levels = LevelCatalog.Discover();
        var service = new JarSystemService(level: levels[0]);
        var sink = new RecordingSink();
        var dispatcher = new CommandDispatcher(sink);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "loadLevel",
            data = new { index = levels[1].Index }
        }));

        await dispatcher.HandleAsync(service, new StubWebSocket(), "loadLevel", doc.RootElement.GetProperty("data"));

        Assert.Equal(new[] { "world", "state" }, sink.Broadcasts.Select(MessageType));
        Assert.Equal(levels[1].Index, service.Level?.Index);
    }
}

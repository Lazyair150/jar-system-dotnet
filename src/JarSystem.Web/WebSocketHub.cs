using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>
/// WebSocket 连接管理与会话循环（原 Program.cs 的中间件 + <c>WsSend</c>/<c>Broadcast</c> 局部函数）。
/// 只负责传输：接入、注册、收发、广播、清理；命令语义交给 <see cref="CommandDispatcher"/>。
/// </summary>
public sealed class WebSocketHub : IWebSocketSink
{
    /// <summary>允许的最大并发连接数（开发壳：避免无上限连接挤占单用户串行闸门）。</summary>
    private const int MaxClients = 8;

    private readonly List<WebSocket> _clients = new();

    /// <summary>接受一个 /ws 连接并运行其会话循环，直到对端关闭或发生异常。</summary>
    public async Task RunSession(JarSystemService svc, HttpContext ctx, CommandDispatcher dispatcher)
    {
        // 安全（CSWSH 防护）：浏览器页面可跨站发起 WebSocket 连接。
        // 仅允许「无 Origin」（非浏览器客户端，如测试脚本）或回环来源；其它来源 403。
        if (!IsAllowedOrigin(ctx.Request.Headers.Origin.ToString()))
        {
            Console.Error.WriteLine("[ws] 拒绝来自非本机来源的连接: " + ctx.Request.Headers.Origin);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        int current;
        lock (_clients) current = _clients.Count;
        if (current >= MaxClients)
        {
            Console.Error.WriteLine("[ws] 连接数已达上限 " + MaxClients + "，拒绝新连接");
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        lock (_clients) _clients.Add(ws);

        var sys = svc.System;
        await SendAsync(ws, JsonArgs.J(new { type = "init", data = sys.GetState() }));

        var buf = new byte[32768];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), default);
                if (r.MessageType == WebSocketMessageType.Close) break;
                // 单帧上限 = 接收缓冲区大小（32KB）；未结束的分片报文直接拒绝，避免解析被截断的 JSON
                if (!r.EndOfMessage)
                {
                    Console.Error.WriteLine("[ws] 报文超过 32KB 或未结束，关闭连接");
                    break;
                }
                var json = Encoding.UTF8.GetString(buf, 0, r.Count);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var te)) continue;
                var type = te.GetString() ?? "";
                using var emptyDoc = JsonDocument.Parse("{}");
                var dataEl = root.TryGetProperty("data", out var de) ? de : emptyDoc.RootElement;
                await dispatcher.HandleAsync(svc, ws, type, dataEl);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("[ws] 连接异常: " + ex.Message); }
        finally
        {
            lock (_clients) _clients.Remove(ws);
            // 收尾用空 catch：连接可能已被对端关闭，关闭失败无补救动作
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(WebSocket socket, string json)
    {
        if (socket.State == WebSocketState.Open)
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default); }
            catch (Exception ex) { Console.Error.WriteLine("[ws] 发送失败: " + ex.Message); }
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(string json)
    {
        var dead = new List<WebSocket>();
        WebSocket[] copy;
        lock (_clients) copy = _clients.ToArray();
        foreach (var w in copy)
            if (w.State == WebSocketState.Open) _ = SendAsync(w, json);
            else dead.Add(w);
        lock (_clients) foreach (var w in dead) _clients.Remove(w);
        await Task.CompletedTask;
    }

    /// <summary>来源校验：空 Origin（非浏览器客户端）或回环主机（127.0.0.1 / localhost / [::1]）放行。</summary>
    private static bool IsAllowedOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}

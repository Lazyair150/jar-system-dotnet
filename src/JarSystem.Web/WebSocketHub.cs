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

    /// <summary>
    /// 发送闸门：同一时刻只允许一个 <c>SendAsync</c> 在飞。
    ///
    /// 为什么必须有：.NET 的 <see cref="WebSocket"/> **不允许并发发送**（上一次发送尚未完成时再发即抛异常），
    /// 而本宿主的推送有三个来源 —— 命令回包（在 <c>JarSystemService.Gate</c> 闸门内）、
    /// Tick 循环、自检循环（后两者在闸门**外**广播）。此前广播是"发射即忘"（<c>_ = SendAsync(...)</c>），
    /// 与命令回包撞上时异常被吞成一行日志 ⇒ **偶发丢一条 state 推送**（用户侧表现为界面停更一次）。
    /// 现在改为逐个 await，并由本闸门串行化。
    /// </summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

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
        // 先发**世界元数据**（形状/液体目录，一次性），再发 init ——
        // 这样客户端拿到 init 时已经知道"有哪些形状"，可直接渲染下拉框与标记选择器
        await SendAsync(ws, JsonArgs.J(new { type = "world", data = sys.WorldMeta() }));
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
                // 输入健壮性（2026-09）：非法/非预期形状的报文只回一条 error，**绝不断开连接**。
                // 此前 JsonDocument.Parse 的异常、以及对非对象调用 TryGetProperty 的异常都会冒到
                // 会话级 catch ⇒ 客户端被直接掉线（而它只是发错了一个字段类型）。
                JsonDocument doc;
                try { doc = JsonDocument.Parse(json); }
                catch (JsonException)
                {
                    await SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "报文不是合法 JSON" } }));
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var type = root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("type", out var te) && te.ValueKind == JsonValueKind.String
                        ? te.GetString() ?? ""
                        : "";
                    if (type.Length == 0)
                    {
                        await SendAsync(ws, JsonArgs.J(new { type = "error", data = new { message = "缺少 type 字段（或 type 不是字符串）" } }));
                        continue;
                    }

                    // 载荷必须是对象；缺失或非对象一律给空对象，后续 JsonArgs 的读取就都是安全的
                    using var emptyDoc = JsonDocument.Parse("{}");
                    var dataEl = root.TryGetProperty("data", out var de) && de.ValueKind == JsonValueKind.Object
                        ? de
                        : emptyDoc.RootElement;
                    await dispatcher.HandleAsync(svc, ws, type, dataEl);
                }
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
        // 串行化：任何两个发送都不重叠（见 _sendGate 的说明）
        await _sendGate.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, default);
        }
        catch (Exception ex) { Console.Error.WriteLine("[ws] 发送失败: " + ex.Message); }
        finally { _sendGate.Release(); }
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(string json)
    {
        WebSocket[] copy;
        lock (_clients) copy = _clients.ToArray();
        foreach (var w in copy)
        {
            if (w.State != WebSocketState.Open)
            {
                lock (_clients) _clients.Remove(w);
                continue;
            }
            // 逐个 await（原先是发射即忘，会与命令回包并发发送同一个 socket）
            await SendAsync(w, json);
        }
    }

    /// <summary>来源校验：空 Origin（非浏览器客户端）或回环主机（127.0.0.1 / localhost / [::1]）放行。</summary>
    private static bool IsAllowedOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}

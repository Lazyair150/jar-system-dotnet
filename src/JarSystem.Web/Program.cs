// JarSystem Web 壳的启动装配（阶段 2F 拆分后只剩接线）。
//
// 结构：
//   JarSystemService   组合根（单例系统实例 + 串行闸门 + 定时器）
//   WebSocketHub       /ws 连接管理、收发与广播
//   CommandDispatcher  命令分派（对外命令名/消息名契约集中在此）
//   JsonArgs           JSON 取值与序列化辅助
//   IWebSocketSink     推送出口接口（依赖倒置）

var builder = WebApplication.CreateBuilder(args);

// 安全：未显式指定监听地址时，默认只绑回环（避免把开发壳暴露到局域网/公网）
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) &&
    !args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase)))
{
    builder.WebHost.UseUrls("http://127.0.0.1:3457");
}

// P5：核心与组合根由宿主侧的扩展方法注册（容器、日志生态只出现在宿主层；核心保持零依赖）
builder.Services.AddJarSystem();
var app = builder.Build();

// 立刻解析组合根：让"世界配置校验失败"发生在**启动时**，而不是第一个请求进来时才炸
_ = app.Services.GetRequiredService<JarSystemService>();

// 状态查询端点：与 WebSocket 命令、定时器共用同一串行闸门
app.MapGet("/api/state", async (JarSystemService svc) =>
{
    await svc.Gate.WaitAsync();
    try { return svc.System.GetState(); }
    finally { svc.Gate.Release(); }
});

app.UseWebSockets();
app.UseDefaultFiles();
app.UseStaticFiles();

// P5：推送出口与命令分派由容器持有（后台循环需要同一个 sink 广播，不能各自 new 一份）
var hub = app.Services.GetRequiredService<WebSocketHub>();
var dispatcher = app.Services.GetRequiredService<CommandDispatcher>();

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/ws" && ctx.WebSockets.IsWebSocketRequest)
    {
        var svc = ctx.RequestServices.GetRequiredService<JarSystemService>();
        await hub.RunSession(svc, ctx, dispatcher);
    }
    else await next(ctx);
});

app.Run();

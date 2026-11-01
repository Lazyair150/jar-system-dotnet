// JarSystem Web 壳的启动装配（阶段 2F 拆分后只剩接线）。
//
// 结构：
//   JarSystemService   组合根（单例系统实例 + 串行闸门 + 定时器）
//   WebSocketHub       /ws 连接管理、收发与广播
//   CommandDispatcher  命令分派（对外命令名/消息名契约集中在此）
//   JsonArgs           JSON 取值与序列化辅助
//   IWebSocketSink     推送出口接口（依赖倒置）

using System.Text;

// dotnet run 的 ContentRoot 是 Web 工程目录，而直接运行构建产物时通常不是。
// 后一种情况会让 UseStaticFiles 静默找不到 wwwroot；按内容根/输出目录/工程目录
// 三种实际启动方式解析一次，避免“命令不同，页面就 404”。
var webRoot = FindWebRoot();
var builderOptions = webRoot == null
    ? new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory }
    : new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
        WebRootPath = webRoot
    };
var builder = WebApplication.CreateBuilder(builderOptions);

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

// 本壳只提供本机开发/游玩界面：加上不依赖页面结构的基线安全响应头。
// 未添加 CSP：当前页面使用内联 onclick，强行加入会破坏界面；先保留可在浏览器 DevTools 验证的三项。
var allowedHostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var entry in (builder.Configuration["AllowedHosts"] ?? "*").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    allowedHostNames.Add(entry.Trim('[', ']'));
var allowAnyHost = allowedHostNames.Contains("*");
app.Use(async (ctx, next) =>
{
    // Host 头也是外部输入。只绑回环并不自动阻止“浏览器把恶意域名解析到 127.0.0.1”
    // 这类 DNS rebinding；因此对显式配置的允许主机名再做一次校验。
    if (!allowAnyHost && !allowedHostNames.Contains(ctx.Request.Host.Host))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(ctx);
});

// 状态查询端点：与 WebSocket 命令、定时器共用同一串行闸门。
// **来源校验与 /ws 同一条**（2026-09-16 补）：原先只有 WebSocket 那条入口做了 CSWSH 防护，
// HTTP 这条没有 —— 同一份状态、两个入口，对"谁能访问"的口径应当一致（本壳只绑回环）。
// 注：只拒绝**浏览器跨源**请求；无 Origin 的客户端（curl / 测试脚本 / 非浏览器宿主）照常放行，
// 因此文档里的 `curl /api/state` 冒烟方式不受影响。
app.MapGet("/api/state", async (HttpContext ctx, JarSystemService svc) =>
{
    if (!WebSocketHub.IsAllowedOrigin(ctx.Request.Headers.Origin.ToString()))
    {
        Console.Error.WriteLine("[api] 拒绝来自非本机来源的 /api/state: " + ctx.Request.Headers.Origin);
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    await svc.Gate.WaitAsync();
    // 两种返回都走 IResult（否则 lambda 的返回类型无法统一：StateDto 与 IResult 没有公共隐式转换）
    // **并复用 WS 的序列化策略**（`JsonArgs.Options`）：本项目自己的 T2 条目指出"两条路径各自序列化同一 DTO，
    // 将来必分叉"。这里让两条路径**字面上共用同一个选项实例** ⇒ 分叉在结构上不可能，
    // 而不是靠"两边都记得同步"。键名输出不变（契约 DTO 全部由 [JsonPropertyName] 钉死，命名策略只作用于未标注成员）。
    try { return Results.Json(svc.System.GetState(), JsonArgs.Options); }
    finally { svc.Gate.Release(); }
});

// ============================================================
// 场景编辑器后端（P5）：载入 / 校验 / 保存 `config/scenario.json`
//
// 为什么要这三个端点：场景数据（`Rules.ScenarioOptions`）是"世界事件注入"的数据形态，
// 而作者需要一个能立刻拿到**校验结论**的地方 —— 于是把核心的 `Validate()` 暴露成 HTTP。
// 边界：只在本机回环壳里提供（来源校验与 `/api/state` 同一判据）；保存前**必须先校验通过**；
// 目标路径固定为 `config/scenario.json`（不接受任意路径）。
// ============================================================
const string SCENARIO_RELATIVE = "config/scenario.json";
const string SCENARIO_SAMPLE_RELATIVE = "config/scenario.sample.json";
const int MAX_SCENARIO_BYTES = 1024 * 1024;

app.MapGet("/api/scenario", (HttpContext ctx) =>
{
    if (!WebSocketHub.IsAllowedOrigin(ctx.Request.Headers.Origin.ToString()))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    // 用与世界配置**同一套向上查找**（服务的当前目录是 src\JarSystem.Web，不是仓库根）
    var currentPath = JarSystemService.ResolveConfigPath(SCENARIO_RELATIVE);
    var samplePath = JarSystemService.ResolveConfigPath(SCENARIO_SAMPLE_RELATIVE);
    var current = currentPath != null ? File.ReadAllText(currentPath) : "";
    var sample = samplePath != null ? File.ReadAllText(samplePath) : "";
    return Results.Json(new { exists = current.Length > 0, source = current, sample });
});

app.MapPost("/api/scenario/validate", async (HttpContext ctx) =>
{
    if (!WebSocketHub.IsAllowedOrigin(ctx.Request.Headers.Origin.ToString()))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var body = await ReadBodyWithinLimitAsync(ctx, MAX_SCENARIO_BYTES);
    if (!body.Ok)
        return Results.Json(new { valid = false, errors = new[] { body.Error ?? "请求体超限" }, warnings = Array.Empty<string>() },
            statusCode: StatusCodes.Status413PayloadTooLarge);
    var report = ValidateScenarioText(body.Text);
    // 注意：**不要**直接返回元组 —— System.Text.Json 不序列化 ValueTuple 的字段（实测返回 `{}`）
    return Results.Json(new { valid = report.Valid, errors = report.Errors, warnings = report.Warnings });
});

app.MapPost("/api/scenario", async (HttpContext ctx) =>
{
    if (!WebSocketHub.IsAllowedOrigin(ctx.Request.Headers.Origin.ToString()))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    var body = await ReadBodyWithinLimitAsync(ctx, MAX_SCENARIO_BYTES);
    if (!body.Ok)
        return Results.Json(new { saved = false, errors = new[] { body.Error ?? "请求体超限" }, warnings = Array.Empty<string>() },
            statusCode: StatusCodes.Status413PayloadTooLarge);
    var report = ValidateScenarioText(body.Text);
    if (!report.Valid)
        return Results.Json(new { saved = false, errors = report.Errors, warnings = report.Warnings });
    // 保存目标**可能还不存在** ⇒ 用"配置目录"解析（不能复用 ResolveConfigPath —— 它只找已存在的文件）
    var savePath = Path.Combine(JarSystemService.ResolveConfigDir(), "scenario.json");
    var tempPath = savePath + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
        // 同目录临时文件 + 原子替换：崩溃或并发读不会看到半份场景 JSON。
        File.WriteAllText(tempPath, body.Text);
        File.Move(tempPath, savePath, overwrite: true);
        Console.WriteLine("[scenario] saved: " + Path.GetFullPath(savePath) + "（重启后生效）");
        return Results.Json(new { saved = true, path = Path.GetFullPath(savePath), warnings = report.Warnings });
    }
    catch (Exception ex)
    {
        // 写文件是副作用：IO 失败要如实回报，不能让端点 500
        return Results.Json(new { saved = false, errors = new List<string> { "写入失败: " + ex.Message }, warnings = report.Warnings });
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
});

static (bool Valid, List<string> Errors, List<string> Warnings) ValidateScenarioText(string text)
{
    try
    {
        // 解析与校验都住在核心（Rules），这里只做"搬运 + 呈现"
        var scenario = JarSystem.Core.Rules.ScenarioJson.FromJson(text);
        var report = scenario.Validate();
        return (report.IsValid, report.Errors, report.Warnings);
    }
    catch (Exception ex) { return (false, new List<string> { "解析失败: " + ex.Message }, new List<string>()); }
}

// HTTP 场景编辑端点的输入边界：Content-Length 可缺失（chunked），因此必须在读取过程中
// 继续累计字节数。超限直接 413，不把任意大的请求体读进内存。
static async Task<(bool Ok, string Text, string? Error)> ReadBodyWithinLimitAsync(HttpContext ctx, int maxBytes)
{
    if (ctx.Request.ContentLength is > 0 && ctx.Request.ContentLength > maxBytes)
        return (false, "", "场景文本超过 1 MiB 上限");

    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    int total = 0;
    while (true)
    {
        int read = await ctx.Request.Body.ReadAsync(chunk.AsMemory(0, chunk.Length), ctx.RequestAborted);
        if (read == 0) break;
        total += read;
        if (total > maxBytes) return (false, "", "场景文本超过 1 MiB 上限");
        buffer.Write(chunk, 0, read);
    }
    return (true, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), null);
}

static string? FindWebRoot()
{
    var fromOutput = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    if (Directory.Exists(fromOutput)) return fromOutput;

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && dir != null; i++)
    {
        var candidate = Path.Combine(dir.FullName, "src", "JarSystem.Web", "wwwroot");
        if (Directory.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

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

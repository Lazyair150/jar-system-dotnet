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
    using var reader = new StreamReader(ctx.Request.Body);
    var text = await reader.ReadToEndAsync();
    var report = ValidateScenarioText(text);
    // 注意：**不要**直接返回元组 —— System.Text.Json 不序列化 ValueTuple 的字段（实测返回 `{}`）
    return Results.Json(new { valid = report.Valid, errors = report.Errors, warnings = report.Warnings });
});

app.MapPost("/api/scenario", async (HttpContext ctx) =>
{
    if (!WebSocketHub.IsAllowedOrigin(ctx.Request.Headers.Origin.ToString()))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    using var reader = new StreamReader(ctx.Request.Body);
    var text = await reader.ReadToEndAsync();
    var report = ValidateScenarioText(text);
    if (!report.Valid)
        return Results.Json(new { saved = false, errors = report.Errors, warnings = report.Warnings });
    // 保存目标**可能还不存在** ⇒ 用"配置目录"解析（不能复用 ResolveConfigPath —— 它只找已存在的文件）
    var savePath = Path.Combine(JarSystemService.ResolveConfigDir(), "scenario.json");
    try
    {
        File.WriteAllText(savePath, text);
        Console.WriteLine("[scenario] saved: " + Path.GetFullPath(savePath) + "（重启后生效）");
        return Results.Json(new { saved = true, path = Path.GetFullPath(savePath), warnings = report.Warnings });
    }
    catch (Exception ex)
    {
        // 写文件是副作用：IO 失败要如实回报，不能让端点 500
        return Results.Json(new { saved = false, errors = new List<string> { "写入失败: " + ex.Message }, warnings = report.Warnings });
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

/// <summary>
/// 推进循环（P5：替代 <c>CommandDispatcher</c> 里手写的 <c>System.Threading.Timer</c>）。
///
/// 语义与原先一致：仅当 <see cref="JarSystemService.IsRunning"/> 时，每 2 秒在同一个串行闸门
/// （<see cref="JarSystemService.Gate"/>）下 Tick 一次并广播 `state`。
///
/// 唯一的细微差别（已记录）：原定时器在 `startTick` 时**立即**推进一次，本实现首次推进最多晚 2 秒
/// （周期相位差）。世界状态与推进次数都不受影响，只是相位偏移，肉眼不可见。
///
/// 改善：<see cref="PeriodicTimer"/> **不会重入**（上一次未跑完不会叠加），且随宿主优雅停止。
/// </summary>
public sealed class TickBackgroundService : BackgroundService
{
    /// <summary>推进间隔的**默认值**（与原定时器一致：2 秒）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>配置键（宿主侧时间参数，见 appsettings.json 的 <c>JarSystem:TickIntervalMs</c>）。</summary>
    public const string IntervalConfigKey = "JarSystem:TickIntervalMs";

    private readonly JarSystemService _svc;
    private readonly IWebSocketSink _sink;

    /// <summary>
    /// 推进间隔可由**宿主配置**调整（P5 第一片：把"墙钟 ↔ 逻辑 tick"的映射参数化）。
    ///
    /// 为什么放在宿主侧：核心只认逻辑 tick（判定必须与墙钟无关）；"多久推进一次"纯属表现层的时间参数，
    /// 因此它属于宿主而不是内核。下限 100ms 是防呆：把间隔调成 0 会让推进变成忙循环。
    /// </summary>
    public TickBackgroundService(JarSystemService svc, IWebSocketSink sink, Microsoft.Extensions.Configuration.IConfiguration? config = null)
    {
        _svc = svc;
        _sink = sink;
        var raw = config?[IntervalConfigKey];
        var ms = int.TryParse(raw, out var parsed) ? parsed : (int)Interval.TotalMilliseconds;
        if (ms < 100) ms = 100;
        _svc.TickIntervalMs = ms;
        if (ms != (int)Interval.TotalMilliseconds)
            Console.WriteLine("[tick] 推进间隔来自配置 " + IntervalConfigKey + " = " + ms + " ms");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 每轮读一次间隔：于是配置与客户端都能随时改节奏（倍速）。
        // "不重入"由顺序 await 保证（上一轮没跑完不会叠加），与原先的 PeriodicTimer 等价。
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = CurrentInterval();
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (!_svc.IsRunning) continue;
            var before = DateTimeOffset.UtcNow;
            await TickOnceAsync(stoppingToken);
            ReportIfStalled(before, CurrentInterval());
        }
    }

    /// <summary>当前推进间隔（下限 100ms 防呆：调成 0 会让推进变成忙循环）。</summary>
    private TimeSpan CurrentInterval() => TimeSpan.FromMilliseconds(Math.Max(100, _svc.TickIntervalMs));

    /// <summary>
    /// 诊断（2026-09-16）：一次 Tick 从"进入"到"完成"若超过一个周期，说明**闸门被别的长任务占住**了
    /// （通常是自动检测在按默认预算逐个分析待执行订单）。这段时间本该发生的推进被 <see cref="PeriodicTimer"/>
    /// 丢弃（**不补算是刻意的**：1 tick = 一次"流入 + 产出"的世界步进，补算等于凭空推进世界 ⇒ 改玩法语义）。
    ///
    /// 只写 stderr、**不进任何契约**（所以既不改状态、也不影响回放）；目的是把"世界比墙钟慢"这件事
    /// 从"用户隐约觉得卡"变成一行带毫秒数的证据。真要缓解，得动预算或分片 —— 而那两样会破坏
    /// "回放能复现同一份分析结果"这条承重不变量（分析结果进契约、且被记进日志，
    /// 见 <c>JarSystem.CheckFeasibility</c> 的注释），因此**不做**，只让它可见。
    /// </summary>
    private static void ReportIfStalled(DateTimeOffset before, TimeSpan interval)
    {
        var held = DateTimeOffset.UtcNow - before;
        if (held <= interval) return;
        Console.Error.WriteLine(
            $"[tick] 闸门被占用 {held.TotalMilliseconds:F0} ms（周期 {interval.TotalMilliseconds:F0} ms）"
            + " — 期间的推进被跳过且不补算");
    }

    private async Task TickOnceAsync(CancellationToken token)
    {
        try
        {
            string json;
            await _svc.Gate.WaitAsync(token);
            try
            {
                // 必须在拿到闸门之后才读取当前世界：wait 期间 loadLevel 可能已经替换了 System。
                var sys = _svc.System;
                sys.Tick();
                json = JsonArgs.J(new { type = "state", data = sys.GetState() });
            }
            finally { _svc.Gate.Release(); }
            await _sink.BroadcastAsync(json);
        }
        catch (OperationCanceledException) { /* 宿主停止中 */ }
        catch (Exception ex) { Console.Error.WriteLine("[tick] " + ex.Message); }
    }

    /// <summary>供聚焦测试驱动一次推进；生产循环仍只通过 <see cref="ExecuteAsync"/> 调用。</summary>
    internal Task TickOnceForTestAsync(CancellationToken token) => TickOnceAsync(token);
}

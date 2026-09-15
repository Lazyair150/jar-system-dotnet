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
    /// <summary>推进间隔（与原定时器一致）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly JarSystemService _svc;
    private readonly IWebSocketSink _sink;

    public TickBackgroundService(JarSystemService svc, IWebSocketSink sink)
    {
        _svc = svc;
        _sink = sink;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!_svc.IsRunning) continue;
            await TickOnceAsync(stoppingToken);
        }
    }

    private async Task TickOnceAsync(CancellationToken token)
    {
        try
        {
            var sys = _svc.System;
            string json;
            await _svc.Gate.WaitAsync(token);
            try { sys.Tick(); json = JsonArgs.J(new { type = "state", data = sys.GetState() }); }
            finally { _svc.Gate.Release(); }
            await _sink.BroadcastAsync(json);
        }
        catch (OperationCanceledException) { /* 宿主停止中 */ }
        catch (Exception ex) { Console.Error.WriteLine("[tick] " + ex.Message); }
    }
}

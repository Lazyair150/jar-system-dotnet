/// <summary>
/// 自动可行性检测循环（P5：替代 <c>CommandDispatcher</c> 里手写的 <c>System.Threading.Timer</c>）。
///
/// 语义与原先**一致**：仅当 <c>sys.AutoCheckEnabled</c> 时，每 5 秒在串行闸门下做一次
/// <c>CheckAllFeasibility()</c> 并广播 `autoCheckResults`；首次检测同样在 5 秒后
/// （原先 <c>dueTime = 5000</c>，因此这里没有"立即执行"的特例）。
/// </summary>
public sealed class AutoCheckBackgroundService : BackgroundService
{
    /// <summary>自动检测间隔（与原定时器一致）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly JarSystemService _svc;
    private readonly IWebSocketSink _sink;

    public AutoCheckBackgroundService(JarSystemService svc, IWebSocketSink sink)
    {
        _svc = svc;
        _sink = sink;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!_svc.System.AutoCheckEnabled) continue;
            try
            {
                var sys = _svc.System;
                string json;
                await _svc.Gate.WaitAsync(stoppingToken);
                try { json = JsonArgs.J(new { type = "autoCheckResults", data = sys.CheckAllFeasibility() }); }
                finally { _svc.Gate.Release(); }
                await _sink.BroadcastAsync(json);
            }
            catch (OperationCanceledException) { /* 宿主停止中 */ }
            catch (Exception ex) { Console.Error.WriteLine("[autocheck] " + ex.Message); }
        }
    }
}

/// <summary>
/// Web 壳的组合根（单例）：持有唯一的 <see cref="JarSystem.Core.JarSystem"/> 实例、
/// 串行闸门与两个定时器。连接管理已迁至 <see cref="WebSocketHub"/>。
/// </summary>
public class JarSystemService
{
    public JarSystem.Core.JarSystem System { get; } = new();
    public bool IsRunning { get; set; }
    public Timer? TickTimer { get; set; }
    public Timer? AutoCheckTimer { get; set; }
    public System.Threading.SemaphoreSlim Gate { get; } = new(1, 1);
}

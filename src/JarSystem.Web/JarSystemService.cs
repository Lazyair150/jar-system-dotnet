/// <summary>
/// Web 壳的组合根（单例）：持有唯一的 <see cref="JarSystem.Core.JarSystem"/> 实例、
/// 串行闸门与两个定时器。连接管理已迁至 <see cref="WebSocketHub"/>。
///
/// **S3b：世界由配置装配**。宿主在这里读 <c>config/world.json</c>（若存在）：
/// <list type="bullet">
/// <item>文件不存在 ⇒ 用内核内置默认世界（<c>new JarSystem()</c> 等价行为）；</item>
/// <item>文件存在但**校验失败** ⇒ **启动即失败**（宁可起不来，也不要"静默缺产出"的世界）；</item>
/// <item>文件存在且合法 ⇒ 用它装配世界（形状/工作台/生产者/库存/流入都可配）。</item>
/// </list>
/// 读取与解析属于宿主职责，核心只提供 <c>WorldOptions</c> 与 <c>WorldOptionsJson</c>（纯字符串，不碰文件）。
/// </summary>
public class JarSystemService
{
    private const string CONFIG_RELATIVE_PATH = "config/world.json";

    public JarSystem.Core.JarSystem System { get; }

    /// <param name="log">核心的可观测性出口（由宿主注入，见 <see cref="JarSystemHosting"/>）；null 时核心用默认控制台日志。</param>
    public JarSystemService(JarSystem.Core.Abstractions.ISystemLog? log = null)
    {
        System = new JarSystem.Core.JarSystem(log: log, world: LoadWorldOrNull());
    }

    public bool IsRunning { get; set; }
    public Timer? TickTimer { get; set; }
    public Timer? AutoCheckTimer { get; set; }
    public System.Threading.SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>读取并校验世界配置；没有配置文件时返回 null（用内置默认世界）。校验失败即抛异常。</summary>
    private static JarSystem.Core.Config.WorldOptions? LoadWorldOrNull()
    {
        var path = ResolveConfigPath();
        if (path == null) return null;

        var world = JarSystem.Core.Config.WorldOptionsJson.FromJson(File.ReadAllText(path));
        var report = world.Validate();
        foreach (var warning in report.Warnings) Console.Error.WriteLine("[world] warning: " + warning);
        if (!report.IsValid)
        {
            foreach (var error in report.Errors) Console.Error.WriteLine("[world] error: " + error);
            throw new InvalidOperationException(
                "世界配置校验失败（" + path + "）：" + report.Errors.Count + " 个错误，已拒绝启动");
        }
        Console.WriteLine("[world] loaded: " + path + "（工作台 " + world.Workshops.Count + " 个，生产者 " + world.Producers.Count + " 个）");
        return world;
    }

    /// <summary>
    /// 配置查找顺序（逐条尝试，取第一个存在的）：
    /// ① 当前工作目录 ② 应用目录（AppContext.BaseDirectory） ③ 从应用目录向上最多 4 层
    /// —— ③ 是为了兼容 `dotnet run`（工作目录可能是 bin/Debug/net10.0）与"仓库根启动"两种方式。
    /// </summary>
    private static string? ResolveConfigPath()
    {
        var candidates = new System.Collections.Generic.List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), CONFIG_RELATIVE_PATH),
            Path.Combine(AppContext.BaseDirectory, CONFIG_RELATIVE_PATH)
        };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 4 && dir != null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, CONFIG_RELATIVE_PATH));
            dir = dir.Parent;
        }
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }
}

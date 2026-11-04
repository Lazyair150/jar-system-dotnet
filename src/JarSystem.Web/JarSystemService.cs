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
    private const string SCENARIO_RELATIVE_PATH = "config/scenario.json";

    /// <summary>场景里"开局即生效"的修饰器作用域（持有到宿主结束；区间到点仍由栈自动摘除）。</summary>
    private readonly List<JarSystem.Core.Domain.ModifierScope> _scenarioScopes = new();

    private readonly JarSystem.Core.Abstractions.ISystemLog? _log;
    private readonly PresentationObserver? _observer;
    private readonly JarSystem.Core.Rules.ScenarioOptions? _scenario;

    /// <summary>
    /// 当前这局世界。除了构造时装配，还可以**换关**（<see cref="LoadLevel"/>）——
    /// 换关 = 起一局新世界，所以这里是可替换的引用，而不是 readonly 字段。
    /// 所有消费者（命令、HTTP 端点、两个后台服务）都用 <c>svc.System</c> 现取，
    /// 因此替换后无需逐个重新接线。
    /// </summary>
    public JarSystem.Core.JarSystem System { get; private set; }

    /// <summary>
    /// 管理令牌（环境变量 <c>JAR_ADMIN_TOKEN</c>）。null/空 = 未配置 ⇒ 管理操作只受"本机"一道约束。
    /// 配置了以后，<c>reset</c> / <c>restartLevel</c> / <c>loadLevel</c> / 写作者数据都要求连接带上
    /// 请求头 <c>X-JarSystem-Admin</c>，且值必须与它**逐字相同**（见 <see cref="JarSystem.Core.Security.AccessPolicy"/>）。
    /// </summary>
    public string? AdminToken { get; } = Environment.GetEnvironmentVariable(JarSystem.Core.Security.HostPolicy.AdminTokenVariable);

    /// <summary>宿主是否配置了管理令牌。</summary>
    public bool AdminTokenConfigured => !string.IsNullOrEmpty(AdminToken);

    /// <summary>
    /// 本进程选中的关卡（`--level N`）；null = 走 `config/world.json` 或内核默认世界。
    /// 关只是"打包好的一份世界 + 提示 + 回合上限"，核心不认识它（见 <see cref="LevelCatalog"/>）。
    /// </summary>
    public LevelInfo? Level { get; private set; }

    /// <summary>
    /// **推进间隔（毫秒）**——宿主侧时间参数（P5）：表示"世界 tick 与墙钟的映射"。
    /// 判定永远只认逻辑 tick，因此改它只影响推进的**节奏**，不影响任何判定结果。
    /// </summary>
    public int TickIntervalMs { get; set; } = 2000;

    /// <param name="log">核心的可观测性出口（由宿主注入，见 <see cref="JarSystemHosting"/>）；null 时核心用默认控制台日志。</param>
    /// <param name="observer">表现层帧的推送器（S1；null = 不推送帧，行为与今天一致）。</param>
    /// <param name="level">
    /// 选中的关卡（`--level N`）。给了就用它的世界（已在 <see cref="LevelCatalog"/> 里校验过）；
    /// 没给才去读 `config/world.json`，再没有就用内核默认世界。核心不认识"关卡"这个概念。
    /// </param>
    public JarSystemService(
        JarSystem.Core.Abstractions.ISystemLog? log = null,
        PresentationObserver? observer = null,
        LevelInfo? level = null)
        : this(log, observer, level, LoadScenarioOrNull())
    {
    }

    /// <summary>聚焦测试入口：直接提供已验证的场景，避免为了单测改写仓库配置。</summary>
    internal JarSystemService(
        JarSystem.Core.Abstractions.ISystemLog? log,
        PresentationObserver? observer,
        LevelInfo? level,
        JarSystem.Core.Rules.ScenarioOptions? scenario)
    {
        _log = log;
        _observer = observer;
        _scenario = scenario;
        Level = level;
        System = new JarSystem.Core.JarSystem(log: log, world: level?.World ?? LoadWorldOrNull());
        // 订阅核心的帧流（`PresentationFeed`）：宿主只**转发**，不再自己定义一套词汇。
        if (observer != null) System.Presentation.Subscribe(observer);
        ReplaceScenarioScopes(BindScenario(System));
        if (level != null) DeployLevelOrders(System, level);
    }

    /// <summary>
    /// **换关**：起一局新世界（不重启进程）。
    ///
    /// 为什么这件事只能由宿主做：核心不认识"关卡"，一关只是"打包好的一份 <see cref="JarSystem.Core.Config.WorldOptions"/> +
    /// 提示 + 回合上限"；换关 = 丢掉旧世界、按关卡的 world 起一局新的、再按模板派单、按信封摆台。
    /// 旧世界连同它的过程、订单、日志一起消失（这正是"换一关"的语义）。
    ///
    /// 调用方必须持有 <see cref="Gate"/>（命令分派器已经这么做），否则可能与推进/后台检查并发。
    /// </summary>
    /// <param name="index">关卡序号（<see cref="LevelCatalog.Discover"/> 的 1 起位置）。不存在即拒绝。</param>
    /// <exception cref="JarSystem.Core.Domain.DomainValidationException">关卡不存在。</exception>
    public void LoadLevel(int index)
    {
        var levels = LevelCatalog.Discover();
        var target = levels.FirstOrDefault(l => l.Index == index)
            ?? throw new JarSystem.Core.Domain.DomainValidationException(
                "没有第 " + index + " 关（本仓库共 " + levels.Count + " 关）");

        var next = new JarSystem.Core.JarSystem(log: _log, world: target.World);
        if (_observer != null) next.Presentation.Subscribe(_observer);
        var nextScenarioScopes = BindScenario(next);
        System = next;
        Level = target;

        // 上一局的开局法令作用域属于旧世界，不能带过来。
        ReplaceScenarioScopes(nextScenarioScopes);

        DeployLevelOrders(next, target);
    }

    /// <summary>
    /// 关卡开局派单：每个模板各派一单。
    ///
    /// 为什么由宿主做：核心不认识"关卡"，而"这一关开局桌上有几张单"是关卡内容的一部分。
    /// 不派单的话，打开关卡看到的是一个空桌子——没有单可做，进度条与判胜都无从谈起。
    /// 失败即拒绝启动：模板名是从已校验过的世界来的，真失败说明世界与派单口径不一致，
    /// 静默跳过只会让玩家面对一张没有订单的桌子而不知为什么。
    /// </summary>
    /// <summary>
    /// 重开本关：把世界推回开局，再按模板重新派单。
    /// 只在宿主层做——"这一关开局是什么样"是关卡内容，核心不认识关卡。
    /// </summary>
    public void RestartLevel()
    {
        System.Reset();
        ReplaceScenarioScopes(BindScenario(System));
        if (Level != null) DeployLevelOrders(System, Level);
    }

    private static void DeployLevelOrders(JarSystem.Core.JarSystem sys, LevelInfo level)
    {
        // 先摆台（信封给的 master / slave），再派单：台位是这一关的起手，
        // 摆好之后再派单，订单的分析结果才是有意义的（否则开局全是"没有主台"）。
        if (level.Master is { } master) sys.SetMasterIndex(master);
        sys.SetSlaveIndex(level.Slave);

        // 派单**按信封的 orders 表**，不是按模板目录：模板是"这一关可做的东西"，
        // 开局单数是"这一关的内容"（第六关要两张一样的单，模板只有一个）。
        foreach (var templateName in level.Orders)
        {
            var (ok, error) = sys.CreateOrderFromTemplate(templateName);
            if (!ok)
                throw new InvalidOperationException(
                    "关卡「" + level.Name + "」的模板「" + templateName + "」派单失败：" + error);
        }
    }

    /// <summary>
    /// 装配**场景**（P5 的"世界事件注入"数据版）：`config/scenario.json` 存在时加载它，
    /// 校验失败即拒绝启动（与 `world.json` 同一口径）；不存在 ⇒ 行为与今天完全一致。
    /// </summary>
    private static JarSystem.Core.Rules.ScenarioOptions? LoadScenarioOrNull()
    {
        var path = ResolveConfigPath(SCENARIO_RELATIVE_PATH);
        if (path == null) return null;

        var scenario = JarSystem.Core.Rules.ScenarioJson.FromJson(File.ReadAllText(path));
        var report = scenario.Validate();
        foreach (var warning in report.Warnings) Console.Error.WriteLine("[scenario] warning: " + warning);
        if (!report.IsValid)
        {
            foreach (var error in report.Errors) Console.Error.WriteLine("[scenario] error: " + error);
            throw new InvalidOperationException(
                "场景校验失败（" + path + "）：" + report.Errors.Count + " 个错误，已拒绝启动");
        }
        Console.WriteLine("[scenario] loaded: " + path + "（事件 " + scenario.Events.Count + " 个，初始修饰器 "
            + scenario.InitialModifiers.Count + " 条）");
        return scenario;
    }

    /// <summary>
    /// 把同一份已验证场景绑定到当前世界。换关/重开只替换世界，不重新读文件；
    /// 场景仍是进程启动时那份，保持“保存后重启生效”的既有边界。
    /// </summary>
    private List<JarSystem.Core.Domain.ModifierScope> BindScenario(JarSystem.Core.JarSystem target)
    {
        var scopes = new List<JarSystem.Core.Domain.ModifierScope>();
        if (_scenario != null)
        {
            foreach (var modifier in JarSystem.Core.Rules.ScenarioBinder.BindInitial(_scenario, target))
                scopes.Add(target.Modifiers.Push(modifier));
            target.Processes.EventSource =
                JarSystem.Core.Rules.ScenarioBinder.CreateEventSource(
                    _scenario, target.Modifiers, target, alongside: target.Bus);
        }
        else
        {
            // 没有场景时仍保留总线事件源（构造时由核心接好）。
            target.Processes.EventSource = target.Bus;
        }
        return scopes;
    }

    private void ReplaceScenarioScopes(List<JarSystem.Core.Domain.ModifierScope> scopes)
    {
        foreach (var scope in _scenarioScopes) scope.Dispose();
        _scenarioScopes.Clear();
        _scenarioScopes.AddRange(scopes);
    }

    public bool IsRunning { get; set; }
    public System.Threading.SemaphoreSlim Gate { get; } = new(1, 1);
    // P5：原先这里持有两个 System.Threading.Timer（TickTimer / AutoCheckTimer），现已移除 ——
    // 改为 TickBackgroundService / AutoCheckBackgroundService（PeriodicTimer）驱动：不重入、随宿主优雅停止。

    /// <summary>读取并校验世界配置；没有配置文件时返回 null（用内置默认世界）。校验失败即抛异常。</summary>
    private static JarSystem.Core.Config.WorldOptions? LoadWorldOrNull()
    {
        var path = ResolveConfigPath(CONFIG_RELATIVE_PATH);
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
    /// ① 当前工作目录 ② 应用目录（AppContext.BaseDirectory） ③ 从应用目录向上逐级。
    ///
    /// ③ 的层数必须够：`src\JarSystem.Web\bin\Debug\net10.0\` 到**仓库根**要向上 **6 层**
    /// （net10.0 → Debug → bin → JarSystem.Web → src → 仓库根），
    /// 早先只找 4 层 ⇒ 只有"从仓库根启动"时命中第 ① 条，其它启动方式会**静默找不到配置**。
    /// </summary>
    /// <summary>
    /// 配置查找顺序（逐条尝试，取第一个存在的）：① 当前工作目录 ② 应用目录 ③ 从应用目录向上逐级。
    /// **internal**：场景编辑器的三个端点也要用它（否则它们会按"服务的当前目录"找文件 ——
    /// 而 `dotnet run` 的当前目录是 `src\JarSystem.Web`，实测踩到过）。
    /// </summary>
    /// <summary>
    /// 解析"配置目录"（仓库根的 config/）：以**必然存在**的世界配置为锚点，其次示例文件，
    /// 最后退回当前目录。之所以需要它：<see cref="ResolveConfigPath"/> 是"找**已存在**文件"的助手，
    /// 而**保存**场景的目标文件在首次保存前并不存在（实测踩到：它退回了服务的当前目录并 500）。
    /// </summary>
    internal static string ResolveConfigDir()
    {
        var anchor = ResolveConfigPath(CONFIG_RELATIVE_PATH);
        if (anchor != null) return Path.GetDirectoryName(anchor)!;
        var sample = ResolveConfigPath("config/scenario.sample.json");
        if (sample != null) return Path.GetDirectoryName(sample)!;
        return Path.Combine(Directory.GetCurrentDirectory(), "config");
    }

    internal static string? ResolveConfigPath(string relativePath)
    {
        var candidates = new System.Collections.Generic.List<string>
        {
            Path.Combine(Directory.GetCurrentDirectory(), relativePath),
            Path.Combine(AppContext.BaseDirectory, relativePath)
        };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, relativePath));
            dir = dir.Parent;
        }
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }
}

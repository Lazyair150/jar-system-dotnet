using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using JarSystem.Core.Config;
using JarSystem.Core.Sessions;
using JarSystem.Core.Services;

// 别名：命名空间 JarSystem.Core 与类型 JarSystem 同名，直接用会歧义（核心测试工程也是这么处理的）
using CoreSystem = JarSystem.Core.JarSystem;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

/// <summary>
/// 宿主控制器（Autoload）：持有唯一的 <see cref="CoreSystem"/> 实例、驱动时间、把状态与日志广播给 UI。
///
/// 与 Web 宿主的对应关系：
///   JarSystemService（组合根 + 串行闸门）        → 本类
///   TickBackgroundService（每 2 秒 Tick + 广播） → _Process 里的累加器
///   AutoCheckBackgroundService（每 5 秒自检）    → _Process 里的自检累加器
///   WebSocket 推送                              → StateChanged / LogAppended 两个信号
///
/// 核心是**零依赖**的普通类库 —— 宿主是 Godot 还是 Web 与它无关，这正是"可移植"的含义。
/// </summary>
public partial class GameManager : Node
{
    /// <summary>状态变化（UI 订阅它重建面板，而不是每帧轮询 GetState）。</summary>
    [Signal] public delegate void StateChangedEventHandler();

    /// <summary>追加一行日志（对应 Web 端的事件日志面板）。</summary>
    [Signal] public delegate void LogAppendedEventHandler(string line);

    /// <summary>推进间隔（秒），与 Web 宿主的 2 秒一致。</summary>
    [Export] public double TickIntervalSeconds { get; set; } = 2.0;

    /// <summary>自检间隔（秒），与 Web 宿主的 5 秒一致。</summary>
    [Export] public double AutoCheckIntervalSeconds { get; set; } = 5.0;

    /// <summary>启动后自动跑一遍演示链（随机订单→分析→执行）。关掉它就得到一个纯手动的界面。</summary>
    [Export] public bool RunDemoOnStart { get; set; } = true;

    /// <summary>核心系统实例（唯一）。</summary>
    public CoreSystem System { get; private set; } = null!;

    /// <summary>是否自动推进（对应 Web 端的 startTick/stopTick）。</summary>
    public bool AutoTickEnabled { get; private set; } = true;

    /// <summary>已推进次数。</summary>
    public int TickCount { get; private set; }

    /// <summary>本局使用的随机种子（存档必须带上它，否则随机订单无法复现）。</summary>
    public int Seed => _seed;

    private const int _seed = 20260916;
    private double _tickAccumulator;
    private double _autoCheckAccumulator;
    private bool _demoDone;

    /// <summary>存档格式版本。**缺失该字段的老存档视为 0（= v1）**，仍然可读。</summary>
    private const int SaveFormatVersion = 1;

    private static readonly JsonSerializerOptions SaveJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
        // 信封（seed/world/commands/version）**故意保持宽松**：将来加字段时，
        // 老版本客户端读到未知键不该直接失败；真正决定世界状态的两段（world / commands）
        // 由 WorldOptionsJson / WorldCommandJson 各自严格把关。
    };

    public override void _Ready()
    {
        // 固定种子 ⇒ 随机订单/随机工作台可复现（核心的 Rng 已统一注入）
        // 独立 ID 源 ⇒ 存档回放能重建出**同样的 ID**（见 JarSystem.JarIds 的说明）
        System = new CoreSystem(randomSeed: _seed,
            jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());
        System.SetMasterIndex(0);   // 0 = 玄武台（基础 圆罐/方罐，禁酒）
        System.SetSlaveIndex(1);    // 1 = 朱雀台（基础 高罐，禁酒）
        Log($"JarSystem 已创建：工作台 {System.Workshops.Count} 个，生产者 {System.ProducerModule.Producers.Count} 个，"
            + $"主台={System.Workshops[0].Name}，从台={System.Workshops[1].Name}");
    }

    public override void _Process(double delta)
    {
        if (AutoTickEnabled)
        {
            _tickAccumulator += delta;
            if (_tickAccumulator >= TickIntervalSeconds)
            {
                _tickAccumulator = 0.0;
                TickOnce();
            }
        }

        // 自动可行性检测（等价于 Web 宿主的 AutoCheckBackgroundService）
        if (System.AutoCheckEnabled)
        {
            _autoCheckAccumulator += delta;
            if (_autoCheckAccumulator >= AutoCheckIntervalSeconds)
            {
                _autoCheckAccumulator = 0.0;
                RunAutoCheck();
            }
        }
    }

    // ============================================================
    // 界面要用的入口（一律"调核心 → 记日志 → 发状态信号"）
    // ============================================================

    public void TickOnce()
    {
        System.Tick();
        TickCount++;
        if (TickCount <= 3 || TickCount % 5 == 0) PrintSummary($"Tick#{TickCount}");

        if (RunDemoOnStart && !_demoDone && TickCount >= 3)
        {
            _demoDone = true;
            RunDemoChain();
        }
        EmitSignal(SignalName.StateChanged);
    }

    public void SetAutoTick(bool enabled)
    {
        AutoTickEnabled = enabled;
        Log("自动推进: " + (enabled ? "开" : "关"));
        EmitSignal(SignalName.StateChanged);
    }

    public void ToggleAutoCheck()
    {
        System.SetAutoCheck(!System.AutoCheckEnabled);
        Log("自动可行性检测: " + (System.AutoCheckEnabled ? "开" : "关"));
        EmitSignal(SignalName.StateChanged);
    }

    public void SetMaster(int index)
    {
        var result = System.SetMaster(index);
        Log($"设为主台 #{index}: success={result.GetValueOrDefault("success")} "
            + $"switched={result.GetValueOrDefault("switched")} caseType={result.GetValueOrDefault("caseType") ?? "-"}");
        EmitSignal(SignalName.StateChanged);
    }

    public void SetSlave(int index)
    {
        var result = System.SetSlave(index);
        Log($"设为从台 #{index}: success={result.GetValueOrDefault("success")} reason={result.GetValueOrDefault("reason") ?? "-"}");
        EmitSignal(SignalName.StateChanged);
    }

    public void CheckFeasibility(int index)
    {
        var r = System.CheckFeasibility(index);
        Log($"订单 #{OrderIdAt(index)} 可行性：可行={r?.Feasible} 未判定={r?.Undetermined} "
            + $"方法={r?.CheckMethod ?? "-"} 阻塞={r?.Blockage ?? "-"} 原因={r?.Reason ?? "-"}");
        EmitSignal(SignalName.StateChanged);
    }

    public void ExecuteOrder(int index)
    {
        var exec = System.ExecuteOrder(index);
        Log($"执行订单 #{OrderIdAt(index)}：success={exec.Success} reason={exec.Reason ?? "-"} "
            + $"发生转移={exec.TransferOccurred} 操作数={exec.Operations?.Count ?? 0}");
        EmitSignal(SignalName.StateChanged);
    }

    public void DeleteOrder(int index)
    {
        var result = System.DeleteOrder(index);
        Log(result.Success
            ? $"删除订单（下标 {index}）"
            : $"删除订单（下标 {index}）被拒绝: {result.Error}");
        EmitSignal(SignalName.StateChanged);
    }

    public void ResetWorld()
    {
        System.Reset();
        System.SetMasterIndex(0);
        System.SetSlaveIndex(1);
        TickCount = 0;
        _demoDone = false;
        Log("世界已重置（并重新选择主/从台）");
        EmitSignal(SignalName.StateChanged);
    }

    public void CreateRandomOrder()
    {
        var r = System.CreateRandomOrder();
        Log(r.Success ? $"创建随机订单，当前共 {System.Orders.Count} 条" : $"创建随机订单失败: {r.Error}");
        EmitSignal(SignalName.StateChanged);
    }

    /// <summary>
    /// 按界面填写的需求创建订单。**规则全部由核心判定**：
    /// 空罐需求必须在前、需求条数 ≤ 10、形状/液体必须在词表内、类型只能是 A/B
    /// （这些规则住在核心，所以 Web 宿主与 Godot 宿主自动遵守同一条）。
    /// 这里只把拒绝原因原样回传给表单展示。
    /// </summary>
    public (bool Ok, string? Error) CreateOrder(List<JarSystem.Core.Abstractions.Requirement> requirements, string orderType)
    {
        var r = System.CreateOrder(requirements, orderType);
        Log(r.Success
            ? $"创建 {orderType} 类订单（{requirements.Count} 个需求），当前共 {System.Orders.Count} 条"
            : $"创建订单被核心拒绝: {r.Error}");
        EmitSignal(SignalName.StateChanged);
        return (r.Success, r.Error);
    }

    // ============================================================
    // 世界编辑类操作（建台 / 改容量 / 改速率）—— **同类操作走同一条受控通道**
    //
    // 为什么统一成这样：这三条规则（至少 1 个基础标记、容量必须为正、速率 0~100 且有限）
    // 都住在核心里，会抛 DomainValidationException。宿主若各自 try/catch，迟早漏掉一处
    // （Web 端当初就是这么漏的：校验写在适配层 ⇒ 只有 Web 遵守）。
    // 这里用一个 TryEdit 收口：调核心 → 成功记日志发信号 / 失败把**核心原文**回给界面。
    // ============================================================

    private (bool Ok, string? Error) TryEdit(string what, Action apply)
    {
        try
        {
            apply();
            Log(what + "：成功");
            EmitSignal(SignalName.StateChanged);
            return (true, null);
        }
        catch (JarSystem.Core.Domain.DomainValidationException ex)
        {
            Log(what + " 被核心拒绝: " + ex.Message);
            return (false, ex.Message);
        }
    }

    private (bool Ok, string? Error) TryEditResult(string what, Func<(bool Success, string? Error)> apply)
    {
        try
        {
            var result = apply();
            if (!result.Success)
            {
                var reason = result.Error ?? "未给出原因";
                Log(what + " 被核心拒绝: " + reason);
                return (false, reason);
            }
            Log(what + "：成功");
            EmitSignal(SignalName.StateChanged);
            return (true, null);
        }
        catch (JarSystem.Core.Domain.DomainValidationException ex)
        {
            Log(what + " 被核心拒绝: " + ex.Message);
            return (false, ex.Message);
        }
    }

    public (bool Ok, string? Error) CreateWorkshop(string name, List<string> baseMarks, List<string> additionalMarks, List<string> ban) =>
        TryEdit($"创建工作台「{name}」(基础 {baseMarks.Count} / 附加 {additionalMarks.Count} / 禁 {ban.Count})",
            () => System.CreateWorkshop(name, baseMarks, additionalMarks, ban));

    public (bool Ok, string? Error) UpdateCapacity(int index, int? maxJars, int? maxLiquids) =>
        TryEditResult($"改工作台 #{index} 容量(罐 {maxJars?.ToString() ?? "-"} / 液 {maxLiquids?.ToString() ?? "-"})",
            () => System.UpdateWorkshopCapacity(index, maxJars, maxLiquids));

    public (bool Ok, string? Error) UpdateProducerRate(string producer, string shape, double rate) =>
        TryEdit($"改生产者 {producer}/{shape} 速率 {rate}",
            () => System.UpdateProducerRate(producer, shape, rate));

    public void CreateRandomWorkshop()
    {
        System.CreateRandomWorkshop();
        Log($"创建随机工作台，当前共 {System.Workshops.Count} 个");
        EmitSignal(SignalName.StateChanged);
    }

    public void Log(string message)
    {
        GD.Print("[Host] " + message);
        EmitSignal(SignalName.LogAppended, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    // ============================================================
    // 存档 / 读档（Event Sourcing 切片的兑现）
    //
    // 存档 = 种子 + 世界配置 + **命令日志**（不是世界快照）。读档 = 用这三样重建一个全新系统：
    // 这正是"日志能重建世界"那条门（verify.bat 第 9 步）在游戏宿主上的用法 ——
    // 好处是存档极小、天然版本友好（记的是意图不是结果），并且将来做"观战/回放/多端同步"是同一套东西。
    // ============================================================

    /// <summary>导出存档文本（格式版本 + 种子 + 世界配置 + 命令日志）。</summary>
    public string ExportSave() => JsonSerializer.Serialize(new SaveFile
    {
        Version = SaveFormatVersion,
        Seed = _seed,
        World = WorldOptionsJson.ToJson(System.World),
        Commands = new List<WorldCommand>(System.CommandLog)
    }, SaveJson);

    /// <summary>从存档文本重建世界。失败时保持当前世界不变（并记日志）。</summary>
    public bool ImportSave(string json)
    {
        try
        {
            var save = JsonSerializer.Deserialize<SaveFile>(json, SaveJson);
            if (save == null) { Log("读档失败：存档内容为空"); return false; }

            // 版本门：老存档没有本字段 ⇒ 0，按 v1 读；**未来格式**则明确拒绝，
            // 而不是拿老字段拼出一个"看起来能跑"的半对世界。
            if (save.Version > SaveFormatVersion)
                throw new InvalidOperationException(
                    $"存档格式版本 {save.Version} 高于本版本支持的 {SaveFormatVersion}，请先升级游戏");

            var world = WorldOptionsJson.FromJson(save.World);

            // 与 Web 宿主同一条策略（JarSystemService.LoadWorldOrNull）：**配置坏了就拒绝读档**。
            // 原先这里漏了 Validate()：同一份 world.json，网页端会拒绝启动，Godot 里却默默跑起来
            // （4 位专家独立指出）。存档里的世界是**外部输入**，比配置文件更该校验。
            var worldReport = world.Validate();
            if (!worldReport.IsValid)
                throw new InvalidOperationException(
                    "存档里的世界配置校验失败：" + string.Join("; ", worldReport.Errors));

            var restored = new CoreSystem(randomSeed: save.Seed, world: world,
                jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());
            int applied = SessionReplay.Into(restored, save.Commands);

            System = restored;
            TickCount = 0;
            _tickAccumulator = 0;
            _autoCheckAccumulator = 0;
            _demoDone = true;                       // 读档后不再自动跑演示链
            Log($"读档成功：重放 {applied} 条命令（种子 {save.Seed}）");
            EmitSignal(SignalName.StateChanged);
            return true;
        }
        catch (Exception ex)
        {
            Log("读档失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>存档到文件（Godot 的 <c>user://</c> 路径，桌面版落在用户数据目录）。</summary>
    public bool SaveToFile(string path)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            Log($"存档失败：无法写入 {path}（{FileAccess.GetOpenError()}）");
            return false;
        }
        file.StoreString(ExportSave());
        Log($"已存档到 {path}（命令 {System.CommandLog.Count} 条）");
        return true;
    }

    /// <summary>从文件读档。</summary>
    public bool LoadFromFile(string path)
    {
        if (!FileAccess.FileExists(path))
        {
            Log($"读档失败：找不到 {path}");
            return false;
        }
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            Log($"读档失败：无法读取 {path}");
            return false;
        }
        return ImportSave(file.GetAsText());
    }

    /// <summary>存档文件结构（v1）：格式版本 + 种子 + 世界配置 JSON + 命令日志。</summary>
    private sealed class SaveFile
    {
        /// <summary>格式版本（**缺失视为 0** ⇒ 本改动之前写出的存档仍可读）。</summary>
        public int Version { get; set; }
        public int Seed { get; set; }
        public string World { get; set; } = "";
        public List<WorldCommand> Commands { get; set; } = new();
    }

    // ============================================================
    // 内部
    // ============================================================

    private void RunAutoCheck()
    {
        var results = System.CheckAllFeasibility();
        int feasible = 0;
        foreach (var item in results) if (item.Result?.Feasible == true) feasible++;
        Log($"[自动检测] 待执行订单 {results.Count} 条，其中可行 {feasible} 条");
        EmitSignal(SignalName.StateChanged);
    }

    private int OrderIdAt(int index) =>
        index >= 0 && index < System.Orders.Count ? System.Orders[index].Id : -1;

    /// <summary>
    /// 演示链（等价于 Web 界面上的三个按钮）：随机订单 → 可行性分析 → 执行。
    /// 随机订单可能天然不可行（需求形状不在任何工作台标记里），所以最多试 8 条，
    /// **优先执行第一条"判定可行"的订单** —— 那才能走到转移/交付路径。
    /// </summary>
    private void RunDemoChain()
    {
        int? feasibleIndex = null;
        for (int attempt = 0; attempt < 8 && feasibleIndex == null; attempt++)
        {
            var created = System.CreateRandomOrder();
            if (!created.Success) continue;

            int index = System.Orders.Count - 1;
            var feasibility = System.CheckFeasibility(index);
            Log($"订单#{System.Orders[index].Id}（第 {attempt + 1} 次尝试）：可行={feasibility?.Feasible} "
                + $"未判定={feasibility?.Undetermined} 方法={feasibility?.CheckMethod ?? "-"} "
                + $"阻塞={feasibility?.Blockage ?? "-"} 需求={System.Orders[index]}");
            if (feasibility?.Feasible == true) feasibleIndex = index;
        }

        if (feasibleIndex == null)
        {
            Log("8 次随机都没抽到可行订单（种子固定，可复现）——本轮只演示分析路径");
            return;
        }

        var exec = System.ExecuteOrder(feasibleIndex.Value);
        Log($"执行订单#{OrderIdAt(feasibleIndex.Value)}：success={exec.Success} reason={exec.Reason ?? "-"} "
            + $"发生转移={exec.TransferOccurred} 操作数={exec.Operations?.Count ?? 0}");
        PrintSummary("执行后");
    }

    /// <summary>把 <c>GetState()</c> 的关键字段打成一行（这就是 UI 绑定的那份强类型快照）。</summary>
    public void PrintSummary(string tag)
    {
        var state = System.GetState();
        var master = state.Workshops.Count > 0 ? state.Workshops[0] : null;
        GD.Print($"[Host] {tag}：工作台={state.Workshops.Count} 订单={state.Orders.Count} "
            + $"仓库罐={state.Warehouse.Jars} 仓库酒={state.Warehouse.Liquids.GetValueOrDefault("酒")} "
            + $"主台={(master?.Name ?? "-")}[罐={master?.Jars.Count ?? 0} 酒={master?.Liquids.GetValueOrDefault("酒") ?? 0}]");
    }
}

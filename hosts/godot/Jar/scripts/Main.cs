using System;
using System.Collections.Generic;
using Godot;
using JarSystem.Core.Orders;
using JarSystem.Core.Services;

/// <summary>
/// 主场景脚本（节点附加脚本范例：文件名 == 类名、必须 partial、必须继承 Node 类型）。
///
/// 职责：**只做表现层** —— 订阅 <see cref="GameManager"/> 的信号，把强类型 DTO 铺到节点上；
/// 所有业务调用都转发给 <see cref="GameManager"/>（它再转给零依赖的核心）。
/// 不在 Godot 侧解析 JSON、不复制任何游戏规则。
/// </summary>
public partial class Main : Control
{
    /// <summary>存档位置（Godot 的 user:// 路径：桌面版落在用户数据目录，不污染工程目录）。</summary>
    public const string SavePath = "user://jar-save.json";

    private GameManager _game = null!;

    private Button _tickButton = null!;
    private Button _autoTickButton = null!;
    private Button _autoCheckButton = null!;
    private Button _randomOrderButton = null!;
    private Button _randomWorkshopButton = null!;
    private Button _newOrderButton = null!;
    private Button _editorButton = null!;
    private Button _saveButton = null!;
    private Button _loadButton = null!;
    private Button _resetButton = null!;
    private Label _statusLabel = null!;
    private Label _summaryLabel = null!;
    private RichTextLabel _logLabel = null!;
    private VBoxContainer _workshopsList = null!;
    private VBoxContainer _ordersList = null!;

    private readonly List<WorkshopCardView> _workshopCards = new();
    private readonly List<OrderCardView> _orderCards = new();
    private PathOverlay _pathOverlay = null!;
    private OrderFormOverlay _orderForm = null!;
    private WorldEditorOverlay _editor = null!;
    private bool _selfChecked;

    public override void _Ready()
    {
        _game = GetNode<GameManager>("/root/GameManager");   // Autoload 节点比主场景更早就绪

        _tickButton = GetNode<Button>("Root/Header/TickButton");
        _autoTickButton = GetNode<Button>("Root/Header/AutoTickButton");
        _autoCheckButton = GetNode<Button>("Root/Header/AutoCheckButton");
        _randomOrderButton = GetNode<Button>("Root/Header/RandomOrderButton");
        _randomWorkshopButton = GetNode<Button>("Root/Header/RandomWorkshopButton");
        _newOrderButton = GetNode<Button>("Root/Header/NewOrderButton");
        _editorButton = GetNode<Button>("Root/Header/EditorButton");
        _saveButton = GetNode<Button>("Root/Header/SaveButton");
        _loadButton = GetNode<Button>("Root/Header/LoadButton");
        _resetButton = GetNode<Button>("Root/Header/ResetButton");
        _statusLabel = GetNode<Label>("Root/Header/StatusLabel");
        _summaryLabel = GetNode<Label>("Root/Body/SummaryColumn/SummaryLabel");
        _logLabel = GetNode<RichTextLabel>("Root/Body/SummaryColumn/LogLabel");
        _workshopsList = GetNode<VBoxContainer>("Root/Body/WorkshopsColumn/Scroll/WorkshopsList");
        _ordersList = GetNode<VBoxContainer>("Root/Body/OrdersColumn/Scroll/OrdersList");

        _tickButton.Pressed += () => _game.TickOnce();
        _autoTickButton.Pressed += () => _game.SetAutoTick(!_game.AutoTickEnabled);
        _autoCheckButton.Pressed += () => _game.ToggleAutoCheck();
        _randomOrderButton.Pressed += () => _game.CreateRandomOrder();
        _randomWorkshopButton.Pressed += () => _game.CreateRandomWorkshop();
        _newOrderButton.Pressed += () => _orderForm.Open();
        _editorButton.Pressed += () => _editor.Open();
        _saveButton.Pressed += () => _game.SaveToFile(SavePath);
        _loadButton.Pressed += () => _game.LoadFromFile(SavePath);
        _resetButton.Pressed += () => _game.ResetWorld();

        // 路径弹窗：代码构建并挂在自己身上（最后添加 ⇒ 覆盖在面板之上）
        _pathOverlay = new PathOverlay();
        AddChild(_pathOverlay);
        // 建单表单：下拉项来自**核心词表**（换 world.json 的形状表，这里自动跟着变）
        _orderForm = new OrderFormOverlay(_game.System.Vocabulary.Shapes, _game.System.Vocabulary.Liquids, _game.CreateOrder);
        AddChild(_orderForm);
        // 世界编辑器：建台 / 改容量 / 改速率 —— 同类编辑操作收在一个单元里
        _editor = new WorldEditorOverlay(_game);
        AddChild(_editor);

        _game.StateChanged += Refresh;
        _game.LogAppended += AppendLog;

        Refresh();
        GD.Print("[UI] Main.tscn 已加载并完成绑定");
    }

    private void AppendLog(string line)
    {
        _logLabel.AppendText(line + "\n");
    }

    /// <summary>把一份状态快照铺到界面（等价于 Web 端 app.js 的 render* 系列）。</summary>
    private void Refresh()
    {
        var state = _game.System.GetState();

        _autoTickButton.Text = "自动推进: " + (_game.AutoTickEnabled ? "开" : "关");
        _autoCheckButton.Text = "自动检测: " + (state.GlobalState.AutoCheckEnabled ? "开" : "关");
        _statusLabel.Text = $"主台 {state.GlobalState.MasterName ?? "未选择"} → 从台 {state.GlobalState.SlaveName ?? "未选择"} "
            + $"｜ 已推进 {_game.TickCount} 次";

        _summaryLabel.Text =
            $"仓库: 罐 {state.Warehouse.Jars}，酒 {state.Warehouse.Liquids.GetValueOrDefault("酒")}，"
            + $"蜜 {state.Warehouse.Liquids.GetValueOrDefault("蜜")}\n"
            + $"已丢弃: 罐 {state.Warehouse.DiscardedJars}，酒 {state.Warehouse.DiscardedLiquids.GetValueOrDefault("酒")}，"
            + $"蜜 {state.Warehouse.DiscardedLiquids.GetValueOrDefault("蜜")}\n"
            + $"工作台 {state.Workshops.Count} 个 ｜ 订单 {state.Orders.Count} 条 ｜ 事件 {state.EventHistory.Count} 条";

        RebuildWorkshops(state);
        RebuildOrders(state);

        // 无界面自检：证明"信号 → 重建面板 → 节点文本"这条链真的通了（渲染本身需人眼确认）
        if (!_selfChecked && _game.TickCount >= 4)
        {
            _selfChecked = true;
            PrintSelfCheck(state);
        }
    }

    private void RebuildWorkshops(StateDto state)
    {
        foreach (var card in _workshopCards) { _workshopsList.RemoveChild(card); card.QueueFree(); }
        _workshopCards.Clear();

        foreach (WorkshopStateDto w in state.Workshops)
        {
            var card = new WorkshopCardView(w.Index, _game.SetMaster, _game.SetSlave);
            card.Refresh(w);
            _workshopsList.AddChild(card);
            _workshopCards.Add(card);
        }
    }

    private void RebuildOrders(StateDto state)
    {
        foreach (var card in _orderCards) { _ordersList.RemoveChild(card); card.QueueFree(); }
        _orderCards.Clear();

        foreach (OrderStateDto o in state.Orders)
        {
            var card = new OrderCardView(o.Index, _game.CheckFeasibility, _game.ExecuteOrder, ShowPath, _game.DeleteOrder);
            card.Refresh(o);
            _ordersList.AddChild(card);
            _orderCards.Add(card);
        }
    }

    /// <summary>点"路径"：把该订单的分析结果摊开（只读）。</summary>
    private void ShowPath(int index)
    {
        if (index < 0 || index >= _game.System.Orders.Count) return;
        _pathOverlay.ShowFor(_game.System.GetState().Orders[index]);
        _game.Log($"打开订单 #{_game.System.Orders[index].Id} 的可行性路径");
    }

    /// <summary>
    /// 自检（只在 headless 时才有意义）：① 面板是否按状态建出了对应数量的卡片；
    /// ② 主台卡片标题是否带【主】；③ 点一次"推进"按钮，验证按钮→核心→信号→重建 的完整回路。
    /// </summary>
    private void PrintSelfCheck(StateDto state)
    {
        int ticksBefore = _game.TickCount;
        _tickButton.EmitSignal(BaseButton.SignalName.Pressed);     // 等价于人手点一次
        int ticksAfter = _game.TickCount;

        string masterTitle = _workshopCards.Count > 0 ? GetTitleText(_workshopCards[0]) : "(无卡片)";
        GD.Print($"[UI] 自检：工作台卡片={_workshopCards.Count}（首张标题=\"{masterTitle}\"）"
            + $" 订单卡片={_orderCards.Count} 日志行数={_logLabel.GetLineCount()}");
        GD.Print($"[UI] 自检：点击\"推进\"按钮 ⇒ TickCount {ticksBefore} → {ticksAfter}"
            + (ticksAfter == ticksBefore + 1 ? " ✅ 按钮接线正常" : " ❌ 按钮未生效"));
        GD.Print($"[UI] 自检：状态栏文本 = \"{_statusLabel.Text}\"");

        // 路径弹窗自检：找到第一条带路径的订单，渲染并断言文本非空
        foreach (OrderStateDto o in state.Orders)
        {
            var fr = o.FeasibilityResult;
            if (fr?.SolverPath is not { Count: > 0 } && fr?.MasterPath is not { Count: > 0 }) continue;

            _pathOverlay.ShowFor(o);
            var lines = _pathOverlay.LastText.Split('\n');
            GD.Print($"[UI] 自检：路径弹窗订单 #{o.Id} 渲染 {lines.Length} 行，前 3 行：");
            for (int i = 0; i < lines.Length && i < 3; i++) GD.Print("        " + lines[i].TrimEnd());
        GD.Print($"[UI] 自检：路径弹窗可见={_pathOverlay.Visible}（含转移清单={_pathOverlay.LastText.Contains("选择性转移")}）");
        _pathOverlay.Visible = false;   // 自检完关掉，别挡住后续
            break;
        }

        // 建单表单自检：① 合法单（空罐在前）应创建成功；② 违反"空罐在前"应被**核心**拒绝
        _orderForm.Open();
        _orderForm.ClearRows();
        _orderForm.SetOrderType(0);
        _orderForm.AddRow(_orderForm.ShapeIndex("圆罐"), _orderForm.LiquidIndex(null));
        int ordersBefore = _game.System.Orders.Count;
        _orderForm.Submit();
        GD.Print($"[UI] 自检：建单表单（合法：空罐(圆罐)）⇒ 订单 {ordersBefore} → {_game.System.Orders.Count}，"
            + $"消息=\"{_orderForm.Message}\"");

        int ordersBefore2 = _game.System.Orders.Count;
        _orderForm.Open();
        _orderForm.ClearRows();
        _orderForm.AddRow(_orderForm.ShapeIndex("圆罐"), _orderForm.LiquidIndex("酒"));  // 满罐在前
        _orderForm.AddRow(_orderForm.ShapeIndex("方罐"), _orderForm.LiquidIndex(null));  // 空罐在后 ⇒ 违规
        _orderForm.Submit();
        GD.Print($"[UI] 自检：建单表单（违规：满罐在前）⇒ 订单 {ordersBefore2} → {_game.System.Orders.Count}（应不变），"
            + $"消息=\"{_orderForm.Message}\"");
        _orderForm.Close();

        // 世界编辑器自检：三类编辑操作各验「核心拒绝」与「成功」两次
        _editor.Open();
        int wsBefore = _game.System.Workshops.Count;

        _editor.ClearMarks();
        _editor.SetWorkshopName("测试台");
        _editor.SubmitWorkshop();
        GD.Print($"[UI] 自检：建台（未选基础标记）⇒ 工作台 {wsBefore} → {_game.System.Workshops.Count}（应不变），"
            + $"消息=\"{_editor.Message}\"");

        _editor.SetMark("圆罐", true);
        _editor.SubmitWorkshop();
        GD.Print($"[UI] 自检：建台（基础标记=圆罐）⇒ 工作台 {wsBefore} → {_game.System.Workshops.Count}，"
            + $"消息=\"{_editor.Message}\"");

        _editor.SelectCapacityWorkshop(0);
        _editor.SetCapacityInputs("0", null);
        _editor.SubmitCapacity();
        GD.Print($"[UI] 自检：改容量（罐位=0）⇒ 消息=\"{_editor.Message}\"");

        _editor.SetCapacityInputs("25", "30");
        _editor.SubmitCapacity();
        GD.Print($"[UI] 自检：改容量（罐位=25/液=30）⇒ 消息=\"{_editor.Message}\"");

        _editor.SelectProducer(0);
        _editor.SelectProducerShape("圆罐");
        _editor.SetRateInput("1000000000");
        _editor.SubmitRate();
        GD.Print($"[UI] 自检：改速率（1e9）⇒ 消息=\"{_editor.Message}\"");

        _editor.SetRateInput("0.5");
        _editor.SubmitRate();
        GD.Print($"[UI] 自检：改速率（0.5）⇒ 消息=\"{_editor.Message}\"");

        _editor.Close();

        // 存档/读档自检：存档 → 弄乱世界 → 读档 → 状态应逐字节回到存档那一刻
        string stateAtSave = SerializeState();
        bool saved = _game.SaveToFile(SavePath);
        _game.TickOnce();
        _game.CreateRandomOrder();
        string stateDirtied = SerializeState();
        bool loaded = _game.LoadFromFile(SavePath);
        string stateAfterLoad = SerializeState();
        GD.Print($"[UI] 自检：存档/读档 ⇒ saved={saved} loaded={loaded} "
            + $"回到存档点={stateAtSave == stateAfterLoad}（弄乱后确实不同={stateAtSave != stateDirtied}）");
        if (stateAtSave != stateAfterLoad)
            GD.Print("        首个差异: " + FirstDifference(stateAtSave, stateAfterLoad));
    }

    /// <summary>找出两个 JSON 字符串的首个差异（回看/前看各 60 字符，便于定位到具体字段）。</summary>
    private static string FirstDifference(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i])
                return $"字符 {i}\n          A: …{a[Math.Max(0, i - 60)..Math.Min(a.Length, i + 60)]}…\n          B: …{b[Math.Max(0, i - 60)..Math.Min(b.Length, i + 60)]}…";
        return $"长度不同: A={a.Length} B={b.Length}";
    }

    /// <summary>
    /// 把当前状态序列化成 JSON 字符串（与 Web 宿主同一套 camelCase 口径），用于自查比对。
    /// `feasibilityResult.timestamp` 装的是**分析耗时毫秒**（墙面时间测得值），回放不可能复现，
    /// 因此按契约快照的同一口径记为 `<volatile>`；其余（含 `eventHistory[].timestamp` 这种
    /// 已被"命令日志带时间"覆盖的 ISO 时间）仍然逐字节比对。
    /// </summary>
    private string SerializeState() => System.Text.RegularExpressions.Regex.Replace(
        System.Text.Json.JsonSerializer.Serialize(
            _game.System.GetState(),
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            }),
        "\"timestamp\":\"\\d+\"", "\"timestamp\":\"<volatile>\"");

    private static string GetTitleText(WorkshopCardView card)
    {
        foreach (Node child in card.GetChildren())
            foreach (Node grandChild in child.GetChildren())
                if (grandChild is Label label && label.Text.Contains('.'))
                    return label.Text;
        return "(未找到标题)";
    }
}

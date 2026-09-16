using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

/// <summary>
/// **世界编辑器**：把"同类操作归一类"落到实处 —— 建工作台 / 改容量 / 改生产者速率
/// 三类编辑操作收在同一个单元里（Web 端对应的是三个分散的入口：建台弹窗、容量输入框、速率输入框）。
///
/// 三条规则（至少 1 个基础标记、容量必须为正、速率 0~100 且有限）**都住在核心**，
/// 本视图只负责收集输入并把核心的拒绝原因展示出来 ——
/// 因此 Web 宿主与 Godot 宿主对同一份非法输入会给出**逐字相同**的提示。
/// </summary>
public partial class WorldEditorOverlay : Control
{
    private readonly GameManager _game;

    private readonly LineEdit _workshopName = new();
    private readonly Dictionary<string, Button> _baseButtons = new();
    private readonly Dictionary<string, Button> _additionalButtons = new();
    private readonly Dictionary<string, Button> _banButtons = new();

    private readonly OptionButton _capacityWorkshop = new();
    private readonly LineEdit _maxJars = new();
    private readonly LineEdit _maxLiquids = new();

    private readonly OptionButton _producer = new();
    private readonly OptionButton _producerShape = new();
    private readonly LineEdit _rate = new();

    private readonly Label _message = new();

    /// <summary>最近一次操作的反馈（供无界面自检断言）。</summary>
    public string Message { get; private set; } = "";

    public WorldEditorOverlay(GameManager game)
    {
        _game = game;
        Visible = false;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.FullRect);
        panel.OffsetLeft = 60;
        panel.OffsetTop = 40;
        panel.OffsetRight = -60;
        panel.OffsetBottom = -40;
        AddChild(panel);

        var scroll = new ScrollContainer();
        panel.AddChild(scroll);
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(box);

        box.AddChild(new Label { Text = "世界编辑器" });

        // ---- 建工作台 ----
        box.AddChild(Section("新建工作台"));
        box.AddChild(Row("名字", _workshopName));
        AddToggleGroup(box, "基础标记（至少 1 个）", _game.System.Vocabulary.Shapes, _baseButtons);
        AddToggleGroup(box, "附加标记", _game.System.Vocabulary.Shapes, _additionalButtons);
        AddToggleGroup(box, "禁令（液体）", _game.System.Vocabulary.Liquids, _banButtons);
        var createButton = new Button { Text = "创建工作台" };
        createButton.Pressed += SubmitWorkshop;
        box.AddChild(createButton);

        // ---- 改容量 ----
        box.AddChild(Section("改工作台容量"));
        box.AddChild(Row("工作台", _capacityWorkshop));
        box.AddChild(Row("罐位上限", _maxJars));
        box.AddChild(Row("液体上限", _maxLiquids));
        var capacityButton = new Button { Text = "应用容量" };
        capacityButton.Pressed += SubmitCapacity;
        box.AddChild(capacityButton);

        // ---- 改生产者速率 ----
        box.AddChild(Section("改生产者速率"));
        box.AddChild(Row("生产者", _producer));
        box.AddChild(Row("形状", _producerShape));
        box.AddChild(Row("速率(每 tick)", _rate));
        var rateButton = new Button { Text = "应用速率" };
        rateButton.Pressed += SubmitRate;
        box.AddChild(rateButton);

        // ---- 收尾 ----
        var footer = new HBoxContainer();
        var closeButton = new Button { Text = "关闭" };
        closeButton.Pressed += Close;
        footer.AddChild(closeButton);
        box.AddChild(footer);
        box.AddChild(_message);

        _producer.ItemSelected += _ => RefreshShapeOptions();
    }

    public void Open()
    {
        RefreshOptions();
        Visible = true;
    }

    public void Close() => Visible = false;

    // ============================================================
    // 供界面与自检共同使用的操作入口
    // ============================================================

    public void SetWorkshopName(string name) => _workshopName.Text = name;

    /// <summary>按名字切换某个标记按钮（三个组里同名的都切）。</summary>
    public void SetMark(string shape, bool on)
    {
        foreach (var map in new[] { _baseButtons, _additionalButtons, _banButtons })
            if (map.TryGetValue(shape, out var button)) button.ButtonPressed = on;
    }

    public void ClearMarks()
    {
        foreach (var map in new[] { _baseButtons, _additionalButtons, _banButtons })
            foreach (var button in map.Values) button.ButtonPressed = false;
    }

    public void SubmitWorkshop()
    {
        var (ok, error) = _game.CreateWorkshop(
            string.IsNullOrWhiteSpace(_workshopName.Text) ? "自定义台" : _workshopName.Text,
            Selected(_baseButtons), Selected(_additionalButtons), Selected(_banButtons));
        SetMessage(ok, error);
    }

    public void SelectCapacityWorkshop(int index) => _capacityWorkshop.Selected = ClampIndex(index, _capacityWorkshop.ItemCount);

    public void SetCapacityInputs(string? maxJars, string? maxLiquids)
    {
        _maxJars.Text = maxJars ?? "";
        _maxLiquids.Text = maxLiquids ?? "";
    }

    public void SubmitCapacity()
    {
        var (ok, error) = _game.UpdateCapacity(_capacityWorkshop.Selected, ParseInt(_maxJars.Text), ParseInt(_maxLiquids.Text));
        SetMessage(ok, error);
    }

    public void SelectProducer(int index)
    {
        _producer.Selected = ClampIndex(index, _producer.ItemCount);
        RefreshShapeOptions();
    }

    /// <summary>按形状名选中（供自检与将来的存档恢复）。</summary>
    public bool SelectProducerShape(string shape)
    {
        for (int i = 0; i < _producerShape.ItemCount; i++)
            if (_producerShape.GetItemText(i) == shape) { _producerShape.Selected = i; return true; }
        return false;
    }

    public void SetRateInput(string text) => _rate.Text = text;

    public void SubmitRate()
    {
        string producer = _producer.ItemCount > 0 ? _producer.GetItemText(_producer.Selected) : "";
        string shape = _producerShape.ItemCount > 0 ? _producerShape.GetItemText(_producerShape.Selected) : "";
        var (ok, error) = _game.UpdateProducerRate(producer, shape, ParseDouble(_rate.Text));
        SetMessage(ok, error);
    }

    // ============================================================
    // 内部
    // ============================================================

    private void SetMessage(bool ok, string? error)
    {
        Message = ok ? "成功" : "被核心拒绝: " + (error ?? "未知原因");
        _message.Text = Message;
    }

    /// <summary>重建下拉项（工作台清单 / 生产者清单 / 形状清单都来自当前状态）。</summary>
    private void RefreshOptions()
    {
        var state = _game.System.GetState();

        int keepWorkshop = _capacityWorkshop.Selected;
        _capacityWorkshop.Clear();
        foreach (var w in state.Workshops) _capacityWorkshop.AddItem($"{w.Index}. {w.Name}");
        _capacityWorkshop.Selected = ClampIndex(keepWorkshop, _capacityWorkshop.ItemCount);

        int keepProducer = _producer.Selected;
        _producer.Clear();
        foreach (var p in state.ProducerStatus.Producers) _producer.AddItem(p.Name);
        _producer.Selected = ClampIndex(keepProducer, _producer.ItemCount);
        RefreshShapeOptions();
    }

    private void RefreshShapeOptions()
    {
        var state = _game.System.GetState();
        int keep = _producerShape.Selected;
        _producerShape.Clear();
        if (_producer.ItemCount > 0 && _producer.Selected < state.ProducerStatus.Producers.Count)
        {
            var rates = state.ProducerStatus.Producers[_producer.Selected].Rates;
            foreach (var shape in rates.Keys) _producerShape.AddItem(shape);
        }
        _producerShape.Selected = ClampIndex(keep, _producerShape.ItemCount);
    }

    private static List<string> Selected(Dictionary<string, Button> map) =>
        map.Where(kv => kv.Value.ButtonPressed).Select(kv => kv.Key).ToList();

    private static int? ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int ClampIndex(int index, int count) => index < 0 || index >= count ? 0 : index;

    private static Label Section(string text) => new() { Text = "── " + text + " ──" };

    private static HBoxContainer Row(string label, Control input)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(120, 0) });
        input.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(input);
        return row;
    }

    private static void AddToggleGroup(VBoxContainer parent, string label, IReadOnlyList<string> items, Dictionary<string, Button> store)
    {
        parent.AddChild(new Label { Text = label });
        var flow = new HFlowContainer();
        foreach (var item in items)
        {
            var button = new Button { Text = item, ToggleMode = true };
            store[item] = button;
            flow.AddChild(button);
        }
        parent.AddChild(flow);
    }
}

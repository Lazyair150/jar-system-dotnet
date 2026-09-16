using System;
using System.Collections.Generic;
using Godot;
using JarSystem.Core.Orders;

/// <summary>
/// 新建订单表单（等价于 Web 端 `showCreateOrder` 那个弹窗）。
///
/// 关键点：**形状/液体下拉项来自核心的词表**（<c>System.Vocabulary</c>），Godot 侧不硬编码任何清单 ——
/// 换了 `config/world.json` 的形状表，这里自动跟着变（这正是 S2/S3 数据驱动要兑现的东西）。
///
/// 需求顺序**不由本表单校验**："空罐需求必须排在满罐之前"是玩法规则，住在核心里
/// （<c>Order.ValidateOrder</c> 抛 <c>DomainValidationException</c>）。表单只负责把拒绝原因展示出来 ——
/// 于是"规则搬进核心"这件事在宿主上是**看得见**的。
/// </summary>
public partial class OrderFormOverlay : Control
{
    private readonly IReadOnlyList<string> _shapes;
    private readonly IReadOnlyList<string> _liquids;
    private readonly Func<List<Requirement>, string, (bool Ok, string? Error)> _submit;

    private readonly VBoxContainer _rows = new();
    private readonly OptionButton _typeOption = new();
    private readonly Label _message = new();
    private readonly List<(OptionButton Shape, OptionButton Liquid)> _rowItems = new();

    /// <summary>最近一次提交的反馈文本（供无界面自检断言）。</summary>
    public string Message { get; private set; } = "";

    public int RowCount => _rowItems.Count;

    public OrderFormOverlay(
        IReadOnlyList<string> shapes,
        IReadOnlyList<string> liquids,
        Func<List<Requirement>, string, (bool Ok, string? Error)> submit)
    {
        _shapes = shapes;
        _liquids = liquids;
        _submit = submit;

        Visible = false;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.FullRect);
        panel.OffsetLeft = 80;
        panel.OffsetTop = 60;
        panel.OffsetRight = -80;
        panel.OffsetBottom = -60;
        AddChild(panel);

        var box = new VBoxContainer();
        panel.AddChild(box);
        box.AddChild(new Label { Text = "新建订单" });

        var typeRow = new HBoxContainer();
        typeRow.AddChild(new Label { Text = "类型" });
        _typeOption.AddItem("A 类（主台独立完成）", 0);
        _typeOption.AddItem("B 类（主台预处理→转移到从台）", 1);
        _typeOption.Selected = 0;
        typeRow.AddChild(_typeOption);
        box.AddChild(typeRow);

        box.AddChild(new Label { Text = "需求（空罐需求必须排在满罐之前 —— 这条规则由核心校验）" });
        box.AddChild(_rows);

        var actions = new HBoxContainer();
        var addButton = new Button { Text = "+ 添加需求" };
        addButton.Pressed += () => AddRow();
        var submitButton = new Button { Text = "创建" };
        submitButton.Pressed += Submit;
        var cancelButton = new Button { Text = "取消" };
        cancelButton.Pressed += Close;
        actions.AddChild(addButton);
        actions.AddChild(submitButton);
        actions.AddChild(cancelButton);
        box.AddChild(actions);

        box.AddChild(_message);
        AddRow();   // 打开就带一行，少点一次
    }

    public void Open() => Visible = true;
    public void Close() => Visible = false;

    /// <summary>形状名 → 下拉项下标（供自检与将来的"从存档恢复表单"使用）。</summary>
    public int ShapeIndex(string shape) => IndexOf(_shapes, shape);

    /// <summary>液体名 → 下拉项下标（0 = 空罐）。</summary>
    public int LiquidIndex(string? liquid) => liquid is null ? 0 : IndexOf(_liquids, liquid) + 1;

    public void SetOrderType(int index) => _typeOption.Selected = index;

    public void ClearRows()
    {
        foreach (var (shape, liquid) in _rowItems)
        {
            shape.QueueFree();
            liquid.QueueFree();
        }
        _rowItems.Clear();
        foreach (Node child in _rows.GetChildren())
        {
            _rows.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <param name="shapeIndex">形状下拉项下标。</param>
    /// <param name="liquidIndex">0 = 空罐，其余 = 液体下标 + 1。</param>
    public void AddRow(int shapeIndex = 0, int liquidIndex = 0)
    {
        var row = new HBoxContainer();

        var shapeOption = new OptionButton();
        for (int i = 0; i < _shapes.Count; i++) shapeOption.AddItem(_shapes[i], i);
        shapeOption.Selected = Clamp(shapeIndex, _shapes.Count);

        var liquidOption = new OptionButton();
        liquidOption.AddItem("空罐", 0);
        for (int i = 0; i < _liquids.Count; i++) liquidOption.AddItem(_liquids[i], i + 1);
        liquidOption.Selected = Clamp(liquidIndex, _liquids.Count + 1);

        var removeButton = new Button { Text = "✕" };
        int captured = _rowItems.Count;
        removeButton.Pressed += () => RemoveRow(captured);

        row.AddChild(shapeOption);
        row.AddChild(liquidOption);
        row.AddChild(removeButton);
        _rows.AddChild(row);
        _rowItems.Add((shapeOption, liquidOption));
        _message.Text = "";
    }

    public void SetRow(int index, int shapeIndex, int liquidIndex)
    {
        if (index < 0 || index >= _rowItems.Count) return;
        var (shape, liquid) = _rowItems[index];
        shape.Selected = Clamp(shapeIndex, _shapes.Count);
        liquid.Selected = Clamp(liquidIndex, _liquids.Count + 1);
    }

    /// <summary>收集表单内容并提交（提交函数由宿主提供：它去调核心，并回传成功/失败原因）。</summary>
    public void Submit()
    {
        var requirements = new List<Requirement>();
        foreach (var (shape, liquid) in _rowItems)
        {
            var shapeName = shape.GetItemText(shape.Selected);
            int liquidId = liquid.Selected;
            requirements.Add(new Requirement
            {
                Shape = shapeName,
                Liquid = liquidId <= 0 ? null : liquid.GetItemText(liquidId)
            });
        }

        string orderType = _typeOption.Selected == 1 ? "B" : "A";
        var (ok, error) = _submit(requirements, orderType);
        Message = ok
            ? $"已创建 {orderType} 类订单（{requirements.Count} 个需求）"
            : "创建失败: " + (error ?? "未知原因");
        _message.Text = Message;
        if (ok) Close();
    }

    private void RemoveRow(int index)
    {
        if (index < 0 || index >= _rowItems.Count) return;
        var (shape, liquid) = _rowItems[index];
        _rowItems.RemoveAt(index);
        _rows.RemoveChild(shape.GetParent());
        shape.GetParent()?.QueueFree();
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (int i = 0; i < items.Count; i++) if (items[i] == value) return i;
        return 0;
    }

    private static int Clamp(int value, int count) => value < 0 || value >= count ? 0 : value;
}

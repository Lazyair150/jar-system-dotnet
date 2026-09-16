using System;
using System.Collections.Generic;
using Godot;
using JarSystem.Core.Services;

/// <summary>
/// 单个工作台的卡片视图（**代码构建**，不依赖 .tscn）。
///
/// 为什么用代码而不是 WorkshopCard.tscn：卡片里有动态数量的罐子/标记，手写 .tscn 属性容易出错，
/// 而代码构建的树可以被无界面自检（打印节点文本）。若将来更想用编辑器拖拽，把它换成
/// PackedScene.Instantiate() 即可，绑定逻辑（Refresh）不用动。
/// </summary>
public partial class WorkshopCardView : PanelContainer
{
    private readonly int _index;
    private readonly Label _title = new();
    private readonly Label _marks = new();
    private readonly Label _ban = new();
    private readonly Label _capacity = new();
    private readonly Label _liquids = new();
    private readonly HBoxContainer _jars = new();
    private readonly Button _masterButton = new();
    private readonly Button _slaveButton = new();

    /// <param name="index">工作台下标（门面 API 用下标而非引用）。</param>
    /// <param name="onSetMaster">点"设为主"时回调。</param>
    /// <param name="onSetSlave">点"设为从"时回调。</param>
    public WorkshopCardView(int index, Action<int> onSetMaster, Action<int> onSetSlave)
    {
        _index = index;

        var box = new VBoxContainer();
        AddChild(box);
        box.AddChild(_title);
        box.AddChild(_marks);
        box.AddChild(_ban);
        box.AddChild(_capacity);
        box.AddChild(_jars);
        box.AddChild(_liquids);

        var buttons = new HBoxContainer();
        _masterButton.Text = "设为主";
        _slaveButton.Text = "设为从";
        _masterButton.Pressed += () => onSetMaster(_index);
        _slaveButton.Pressed += () => onSetSlave(_index);
        buttons.AddChild(_masterButton);
        buttons.AddChild(_slaveButton);
        box.AddChild(buttons);
    }

    public void Refresh(WorkshopStateDto w)
    {
        string role = w.IsMaster ? "【主】" : w.IsSlave ? "【从】" : "";
        _title.Text = $"{w.Index}. {w.Name} {role}";
        _marks.Text = "标记: " + string.Join("/", w.BaseMarks)
            + (w.AdditionalMarks.Count > 0 ? "  +" + string.Join("/", w.AdditionalMarks) : "");
        _ban.Text = w.Ban.Count > 0 ? "🚫 " + string.Join(",", w.Ban) : "";
        _liquids.Text = $"游离液体: 酒 {w.Liquids.GetValueOrDefault("酒")} / 蜜 {w.Liquids.GetValueOrDefault("蜜")}";
        _capacity.Text = $"容量: 罐 {w.Jars.Count}/{w.MaxJars}  液 {w.MaxLiquids}";

        // 主/从按钮只在"不是当前角色"时可用，避免点出无意义的切换
        _masterButton.Disabled = w.IsMaster;
        _slaveButton.Disabled = w.IsSlave;

        foreach (Node child in _jars.GetChildren())
        {
            _jars.RemoveChild(child);
            child.QueueFree();
        }
        foreach (JarStateDto jar in w.Jars)
        {
            var chip = new Label
            {
                Text = (jar.IsMarked ? "•" : "") + (jar.Liquid is null ? "空" : jar.Liquid) + (jar.Shape.Length > 0 ? jar.Shape.Substring(0, 1) : "?"),
                TooltipText = jar.Description,
                CustomMinimumSize = new Vector2(34, 24),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            chip.AddThemeColorOverride("font_color", LiquidColor(jar.Liquid));
            _jars.AddChild(chip);
        }
    }

    /// <summary>占位配色（真美术资源由你在编辑器里替换）：空罐灰、酒红、蜜金。</summary>
    private static Color LiquidColor(string? liquid) => liquid switch
    {
        "酒" => new Color(0.85f, 0.25f, 0.35f),
        "蜜" => new Color(0.95f, 0.8f, 0.25f),
        _ => new Color(0.6f, 0.6f, 0.7f)
    };
}

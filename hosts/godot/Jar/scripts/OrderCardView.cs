using System;
using System.Collections.Generic;
using Godot;
using JarSystem.Core.Orders;
using JarSystem.Core.Services;

/// <summary>单个订单的卡片视图（代码构建，理由同 <see cref="WorkshopCardView"/>）。</summary>
public partial class OrderCardView : PanelContainer
{
    private readonly int _index;
    private readonly Label _title = new();
    private readonly Label _requirements = new();
    private readonly Label _status = new();
    private readonly Label _feasibility = new();
    private readonly Button _checkButton = new();
    private readonly Button _executeButton = new();
    private readonly Button _pathButton = new();
    private readonly Button _deleteButton = new();

    public OrderCardView(int index, Action<int> onCheck, Action<int> onExecute, Action<int> onShowPath, Action<int> onDelete)
    {
        _index = index;

        var box = new VBoxContainer();
        AddChild(box);
        box.AddChild(_title);
        box.AddChild(_requirements);
        box.AddChild(_status);
        box.AddChild(_feasibility);

        var buttons = new HBoxContainer();
        _checkButton.Text = "分析";
        _executeButton.Text = "执行";
        _pathButton.Text = "路径";
        _deleteButton.Text = "删除";
        _checkButton.Pressed += () => onCheck(_index);
        _executeButton.Pressed += () => onExecute(_index);
        _pathButton.Pressed += () => onShowPath(_index);
        _deleteButton.Pressed += () => onDelete(_index);
        buttons.AddChild(_checkButton);
        buttons.AddChild(_executeButton);
        buttons.AddChild(_pathButton);
        buttons.AddChild(_deleteButton);
        box.AddChild(buttons);
    }

    public void Refresh(OrderStateDto o)
    {
        _title.Text = $"#{o.Id}  {o.Type} 类";
        _requirements.Text = string.Join("  ", DescribeRequirements(o.Requirements));
        _status.Text = "状态: " + o.Status;

        var fr = o.FeasibilityResult;
        if (fr == null)
        {
            _feasibility.Text = "尚未分析";
            _feasibility.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        }
        else if (fr.Undetermined)
        {
            // 未判定 ≠ 不可行：预算耗尽时给出的是这个（执行按钮仍然可用）
            _feasibility.Text = "⚠ 未判定：" + (fr.Reason ?? "分析超出预算");
            _feasibility.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.3f));
        }
        else if (fr.Feasible)
        {
            _feasibility.Text = $"✅ 可行（直接 {fr.DirectCount} / 操作 {fr.OperationCount}"
                + (fr.CheckMethod is null ? "" : $"，{fr.CheckMethod}") + "）";
            _feasibility.AddThemeColorOverride("font_color", new Color(0.4f, 0.9f, 0.5f));
        }
        else
        {
            _feasibility.Text = "❌ 不可行：" + (fr.Blockage ?? fr.Reason ?? "未知原因");
            _feasibility.AddThemeColorOverride("font_color", new Color(0.95f, 0.4f, 0.4f));
        }

        bool pending = o.Status == "待执行";
        _checkButton.Disabled = !pending;
        _executeButton.Disabled = !pending;
        // 有路径才可点（与 Web 端"hasPath 才显示📋路径按钮"一致）
        _pathButton.Disabled = !(fr is { SolverPath.Count: > 0 } || fr is { MasterPath.Count: > 0 });
    }

    private static IEnumerable<string> DescribeRequirements(List<Requirement> requirements)
    {
        foreach (var r in requirements)
            yield return r.Liquid is null ? $"空罐({r.Shape})" : $"满罐({r.Shape},{r.Liquid})";
    }
}

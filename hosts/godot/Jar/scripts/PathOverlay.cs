using System.Collections.Generic;
using System.Text;
using Godot;
using JarSystem.Core.Services;

/// <summary>
/// 可行性路径弹窗（等价于 Web 端 `app.js` 的 <c>showPathModal</c>）。
///
/// 纯只读渲染：内容全部来自 <c>OrderStateDto.FeasibilityResult</c> 里的
/// <c>solverPath</c> / <c>masterPath</c> / <c>transferJarIds</c> / <c>transferLiquids</c>
/// （这些字段与 Web 端、Godot 端共用同一份契约）。弹窗用代码构建并挂在主场景上，
/// 因此无界面自检可以断言"文本确实被生成"。
/// </summary>
public partial class PathOverlay : Control
{
    private readonly Label _title = new();
    private readonly RichTextLabel _body = new();

    /// <summary>最近一次渲染出的正文（供无界面自检断言）。</summary>
    public string LastText { get; private set; } = "";

    public PathOverlay()
    {
        Visible = false;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.FullRect);
        panel.OffsetLeft = 48;
        panel.OffsetTop = 48;
        panel.OffsetRight = -48;
        panel.OffsetBottom = -48;
        AddChild(panel);

        var box = new VBoxContainer();
        panel.AddChild(box);

        _title.Text = "可行性路径";
        box.AddChild(_title);

        _body.SizeFlagsVertical = SizeFlags.ExpandFill;
        box.AddChild(_body);

        var close = new Button { Text = "关闭" };
        close.Pressed += () => Visible = false;
        box.AddChild(close);
    }

    /// <summary>按订单的分析结果渲染并显示。</summary>
    public void ShowFor(OrderStateDto order)
    {
        LastText = BuildText(order);
        _title.Text = $"订单 #{order.Id}（{order.Type} 类）可行性路径";
        _body.Text = LastText;
        Visible = true;
    }

    private static string BuildText(OrderStateDto order)
    {
        var sb = new StringBuilder();
        var fr = order.FeasibilityResult;

        sb.Append("需求: ").Append(DescribeRequirements(order)).Append('\n');
        if (fr == null)
        {
            sb.Append("（尚未做可行性分析）\n");
            return sb.ToString();
        }

        sb.Append($"判定: 可行={fr.Feasible} 未判定={fr.Undetermined} 方法={fr.CheckMethod ?? "-"} "
            + $"直接 {fr.DirectCount} / 操作 {fr.OperationCount}\n");
        if (fr.Undetermined) sb.Append($"⚠ 未判定原因: {fr.Reason ?? "-"}\n");
        if (!fr.Feasible && !fr.Undetermined) sb.Append($"❌ 不可行原因: {fr.Blockage ?? fr.Reason ?? "-"}\n");

        if (fr.TransferJarIds is { Count: > 0 } || fr.TransferLiquids is { Count: > 0 })
        {
            string jars = fr.TransferJarIds is { Count: > 0 } ? "#" + string.Join(",#", fr.TransferJarIds) : "无";
            // 注意：TransferLiquids 可能"有键但值全为 0"，过滤后要回退成"无"（否则界面上会出现空的"液体 "）
            var liquidParts = fr.TransferLiquids is { Count: > 0 } ? new List<string>(Liquids(fr.TransferLiquids)) : new List<string>();
            string liquids = liquidParts.Count > 0 ? string.Join(" ", liquidParts) : "无";
            sb.Append($"选择性转移: 罐 {jars} ；液体 {liquids}（其余资源留在主台）\n");
        }

        AppendStage(sb, "主台阶段", fr.MasterPath);
        AppendStage(sb, "从台阶段", fr.SolverPath);
        return sb.ToString();
    }

    // `IReadOnlyList` 而不是 `List`（2026-09-16）：`FeasibilityResultData.MasterPath/SolverPath` 已收紧为
    // 只读集合（执行器照着这些路径干活，交出可变 List 等于让外部改执行承诺）—— 本方法**只读**它，
    // 因此跟随接口类型；否则 Godot 工程会因为"没有从 IReadOnlyList 到 List 的隐式转换"而编不过
    // （本工程不在九步门里，门看不见这种断裂）。
    private static void AppendStage(StringBuilder sb, string label, IReadOnlyList<JarSystem.Core.Orders.SolverStepDto>? path)
    {
        sb.Append($"\n── {label} ──\n");
        if (path == null || path.Count == 0)
        {
            sb.Append("（无步骤）\n");
            return;
        }
        foreach (var step in path)
        {
            sb.Append($"{step.Step + 1}. {step.Requirement ?? "-"}  ←  {step.Operation ?? step.Type ?? "-"}\n");
            if (step.JarsAfter is { Count: > 0 })
                sb.Append("     结果罐子: ").Append(string.Join(", ", step.JarsAfter)).Append('\n');
        }
    }

    private static string DescribeRequirements(OrderStateDto order)
    {
        var parts = new List<string>();
        foreach (var r in order.Requirements)
            parts.Add(r.Liquid is null ? $"空罐({r.Shape})" : $"满罐({r.Shape},{r.Liquid})");
        return string.Join("  ", parts);
    }

    private static IEnumerable<string> Liquids(Dictionary<string, int> liquids)
    {
        foreach (var kv in liquids)
            if (kv.Value > 0) yield return $"{kv.Key}×{kv.Value}";
    }
}

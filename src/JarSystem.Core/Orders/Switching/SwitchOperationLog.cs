using System.Collections.Generic;

namespace JarSystem.Core.Orders;

/// <summary>
/// 一次工作台切换事件中的单条操作记录（对外事件历史与前端动画的数据源）。
/// **init**：日志是审计数据，构造后不得再改。
/// （原文这里连着写了两个 &lt;summary&gt;，第二个会覆盖第一个 ⇒ 已合并。）
/// </summary>
public class SwitchLogEntry
{
    public string Action { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>
/// 切换事件的操作日志（唯一写入口）。原先由 <c>WorkshopSwitchEvent</c> 直接持有 `List` 并对外暴露可变集合，
/// 现在对外只给只读视图 —— 2026-09-16 起是**真只读包装**（本类虽为 internal，但 <c>Entries</c> 经公共的
/// <c>WorkshopSwitchEvent.Operations</c> 可达 ⇒ 只给接口类型时外部仍可 `((List&lt;SwitchLogEntry&gt;)…).Clear()`）。
/// </summary>
internal sealed class SwitchOperationLog
{
    private readonly List<SwitchLogEntry> _entries = new();

    // 字段初始化器按声明顺序执行 ⇒ 可以在这里直接引用上面那个已初始化的字段（活视图）
    private readonly IReadOnlyList<SwitchLogEntry> _entriesView;

    public SwitchOperationLog() => _entriesView = _entries.AsReadOnly();

    public IReadOnlyList<SwitchLogEntry> Entries => _entriesView;

    public void Add(string action, string description) =>
        _entries.Add(new SwitchLogEntry { Action = action, Description = description });

    public void Clear() => _entries.Clear();
}

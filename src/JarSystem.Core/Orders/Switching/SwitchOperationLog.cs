using System.Collections.Generic;

namespace JarSystem.Core.Orders;

/// <summary>一次工作台切换事件中的单条操作记录（对外事件历史与前端动画数据源）。</summary>
/// <summary>切换日志的一条记录。**init**：日志是审计数据，构造后不得再改。</summary>
public class SwitchLogEntry
{
    public string Action { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>
/// 切换事件的操作日志（唯一写入口）。原先由 <c>WorkshopSwitchEvent</c> 直接持有 `List` 并对外暴露可变集合，
/// 现在对外只给只读视图。
/// </summary>
internal sealed class SwitchOperationLog
{
    private readonly List<SwitchLogEntry> _entries = new();

    public IReadOnlyList<SwitchLogEntry> Entries => _entries;

    public void Add(string action, string description) =>
        _entries.Add(new SwitchLogEntry { Action = action, Description = description });

    public void Clear() => _entries.Clear();
}

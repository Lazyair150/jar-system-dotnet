using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Services;

/// <summary>
/// **主/从工作台的选择与切换编排**（原 WorkshopService.SetMaster/SetSlave，2026-09 抽出）。
///
/// 为什么独立成类：这三件事原先缠在同一个方法里，而它们的变更原因完全不同：
/// ① 是否触发切换事件：旧主台"有任何资源"（罐子**或液体**）才触发（含"仅液体也触发"这条历史修正）；
/// ② 事件历史怎么写：workshop_switch 的键集合（type/from/to/caseType/operations/timestamp）是对外契约；
/// ③ 主从索引一致性：负数表示"清除"；越界拒绝；把当前从台设为主 ⇒ 自动解除从台（避免主从同台）。
///
/// 返回值字典的键集合同样是对外契约：success / switched / caseType / operations / slaveCleared / reason。
/// 逻辑与抽取前**逐字一致**（由 5000 用例 + S4/S5/S6/S7/S18 守护）。
/// </summary>
internal sealed class MasterSwitchCoordinator
{
    private readonly JarSystem _system;

    public MasterSwitchCoordinator(JarSystem system)
    {
        _system = system;
    }

    /// <summary>
    /// 设为主工作台。旧主台有任何资源（罐子或液体）时执行切换事件并记录事件历史；
    /// 把当前从台设为主 ⇒ 切换成功后自动解除从台（避免主从同台）。
    /// </summary>
    public Dictionary<string, object?> SetMaster(int index)
    {
        var result = new Dictionary<string, object?> { ["success"] = true, ["switched"] = false };
        if (index < 0) { _system.CurrentMaster = null; return result; }
        if (index >= _system.Workshops.Count) return new() { ["success"] = false, ["reason"] = "无效工作台索引" };

        var next = _system.Workshops[index];
        if (_system.CurrentMaster.HasValue && _system.CurrentMaster.Value != index)
        {
            var old = _system.Workshops[_system.CurrentMaster.Value];
            // 只要有罐子或液体就执行切换事件（问题 6：仅液体也触发）
            bool hasResources = old != null && (old.Jars.Count > 0 || old.LiquidStorage.Values.Any(v => v > 0));
            if (old != null && hasResources)
            {
                var switchEvent = new WorkshopSwitchEvent(old, next);
                var (ok, err) = switchEvent.Execute();
                if (!ok) return new() { ["success"] = false, ["reason"] = err ?? "切换事件失败" };

                List<object> ops = switchEvent.Operations
                    .Select(o => (object)new Dictionary<string, object?>
                    {
                        ["action"] = o.Action,
                        ["description"] = o.Description
                    })
                    .ToList();
                _system.AddEvent(new Dictionary<string, object?>
                {
                    ["type"] = "workshop_switch",
                    ["from"] = old.Name,
                    ["to"] = next.Name,
                    ["caseType"] = switchEvent.CaseType,
                    ["operations"] = ops,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("o")
                });
                result["switched"] = true;
                result["caseType"] = switchEvent.CaseType;
                result["operations"] = ops;
            }
        }

        _system.CurrentMaster = index;
        // 问题 9：把当前从台设为主 → 自动解除从台，避免主从同台
        if (_system.CurrentSlave == index) { _system.CurrentSlave = null; result["slaveCleared"] = true; }
        return result;
    }

    /// <summary>设为从工作台；拒绝把当前主台设为从台。</summary>
    public Dictionary<string, object?> SetSlave(int index)
    {
        if (index < 0) { _system.CurrentSlave = null; return new() { ["success"] = true }; }
        if (index >= _system.Workshops.Count) return new() { ["success"] = false, ["reason"] = "无效工作台索引" };
        if (index == _system.CurrentMaster) return new() { ["success"] = false, ["reason"] = "不能把当前主台设为从台" };
        _system.CurrentSlave = index;
        return new() { ["success"] = true };
    }
}

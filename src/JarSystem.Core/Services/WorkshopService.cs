using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Services;

/// <summary>
/// 工作台相关用例（原 <c>JarSystem.CreateWorkshop / CreateRandomWorkshop / UpdateWorkshopCapacity / SetMaster / SetSlave</c>）。
///
/// 状态仍存放在门面上（批 E 后：对外只读视图 + 程序集内可变视图 <c>WorkshopList</c>/<c>EventHistoryList</c>），
/// 本服务只承载「用例编排」，通过门面实时读写，不缓存任何会被 <c>Reset()</c> 重建的对象。
/// </summary>
internal sealed class WorkshopService
{
    private readonly JarSystem _system;

    public WorkshopService(JarSystem system)
    {
        _system = system;
    }

    public void CreateWorkshop(string name, List<string> baseMarks, List<string>? additionalMarks = null, List<string>? ban = null)
    {
        _system.WorkshopList.Add(new Workshop(name, baseMarks, additionalMarks, ban, vocabulary: _system.Vocabulary));
    }

    public void CreateRandomWorkshop()
    {
        var rand = _system.Rng;  // T4：共用门面的随机源（给了种子即可复现）
        // S2：随机内容按**词表**取（原先写死读 Constants.SHAPES / Constants.LIQUIDS）
        var shapes = _system.Vocabulary.Shapes;
        var liquids = _system.Vocabulary.Liquids;
        var name = _system.World.NamePools;
        var fullName = name.Prefix[rand.Next(name.Prefix.Count)] + name.Suffix[rand.Next(name.Suffix.Count)];
        var baseMarks = new List<string>();
        var count = rand.Next(1, 3);
        while (baseMarks.Count < count) { var s = shapes[rand.Next(shapes.Count)]; if (!baseMarks.Contains(s)) baseMarks.Add(s); }
        var additionalMarks = new List<string>();
        count = rand.Next(0, 4);
        while (additionalMarks.Count < count) { var s = shapes[rand.Next(shapes.Count)]; if (!baseMarks.Contains(s) && !additionalMarks.Contains(s)) additionalMarks.Add(s); }
        var ban = new List<string>();
        count = rand.Next(0, 3);
        while (ban.Count < count) { var l = liquids[rand.Next(liquids.Count)]; if (!ban.Contains(l)) ban.Add(l); }
        _system.WorkshopList.Add(new Workshop(fullName, baseMarks, additionalMarks, ban, vocabulary: _system.Vocabulary));
    }

    /// <summary>调整工作台容量；溢出资源直接丢弃并计数（仓库单向流动，不回流中央仓库）。</summary>
    public object? UpdateWorkshopCapacity(int index, int? maxJars, int? maxLiquids)
    {
        var ws = _system.Workshops.ElementAtOrDefault(index);
        if (ws == null) return new { success = false, reason = "无效工作台索引" };
        if (maxJars.HasValue)
        {
            var discarded = ws.ResizeJarCapacity(maxJars.Value);
            if (discarded > 0) _system.ProducerModule.DiscardJars(discarded);
        }
        if (maxLiquids.HasValue)
        {
            foreach (var kv in ws.ResizeLiquidCapacity(maxLiquids.Value))
                _system.ProducerModule.DiscardLiquids(kv.Key, kv.Value);
        }
        return new { success = true };
    }

    /// <summary>
    /// 设为主工作台。旧主台有任何资源（罐子或液体）时执行切换事件并记录事件历史；
    /// 把当前从台设为主 → 切换成功后自动解除从台（避免主从同台）。
    /// 返回字典的键集合是对外契约：success / switched / caseType / operations / slaveCleared / reason。
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
                _system.EventHistoryList.Add(new Dictionary<string, object?>
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

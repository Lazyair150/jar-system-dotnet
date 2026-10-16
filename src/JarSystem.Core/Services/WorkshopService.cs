using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Services;

/// <summary>
/// 工作台相关用例（原 <c>JarSystem.CreateWorkshop / CreateRandomWorkshop / UpdateWorkshopCapacity / SetMaster / SetSlave</c>）。
///
/// 状态仍存放在门面上（批 E 后：对外只读视图 + 程序集内可变视图 <c>WorkshopList</c>；事件历史经 <c>JarSystem.AddEvent</c> 写入），
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
        // 规则住在核心：没有基础标记的工作台永远无法倒液/改形（"卡死台"）。
        // **两条入口同一条判据**（2026-09-16 对齐）：这里拒绝 + `WorldOptions.Validate()` 也拒绝
        // （原先只有这里检查，配置里写 `"baseMarks": []` 就能绕过 —— 原注释声称"与 world.json 校验口径一致"，
        // 当时其实**不一致**）。
        if (baseMarks == null || baseMarks.Count == 0)
            throw new DomainValidationException("工作台至少需要 1 个基础标记");
        _system.WorkshopList.Add(new Workshop(name, baseMarks, additionalMarks, ban,
            ids: _system.JarIds, vocabulary: _system.Vocabulary, modifiers: _system.Modifiers));
    }

    public void CreateRandomWorkshop()
    {
        var rand = _system.Rng;  // T4：共用门面的随机源（给了种子即可复现）
        // S2：随机内容按**词表**取（原先写死读 Constants.SHAPES / Constants.LIQUIDS）
        var shapes = _system.Vocabulary.Shapes;
        var liquids = _system.Vocabulary.Liquids;
        var name = _system.World.NamePools;
        // 名字池为空时**不能**写 `Prefix[rand.Next(0)]`：那会抛 ArgumentOutOfRangeException，
        // 而 `Validate()` 对空池只给 **Warning**（不是 Error）⇒ 宿主完全可以带着空池启动，
        // 用户一点「随机工作台」就崩（2026-09-16 修）。
        // 注意：守卫**不消耗随机数**，因此非空池的随机序列与修前逐次相同（回放/快照不受影响）。
        var fullName = name.Prefix.Count > 0 && name.Suffix.Count > 0
            ? name.Prefix[rand.Next(name.Prefix.Count)] + name.Suffix[rand.Next(name.Suffix.Count)]
            : "随机台" + (_system.Workshops.Count + 1);
        // 三个随机数量都必须**夹在"可用不重复项"之内**：原实现是 `while (list.Count < count)` 抽取不重复项，
        // 一旦 count 超过可用项数就会**无限循环**（默认词表 8 形状 / 2 液体时侥幸安全，
        // 但词汇表是可注入的 —— 例如 2 个形状的自定义词表 + count=3 就会卡死宿主）。
        var baseMarks = new List<string>();
        var count = Math.Min(rand.Next(1, 3), shapes.Count);
        while (baseMarks.Count < count) { var s = shapes[rand.Next(shapes.Count)]; if (!baseMarks.Contains(s)) baseMarks.Add(s); }
        var additionalMarks = new List<string>();
        count = Math.Min(rand.Next(0, 4), shapes.Count - baseMarks.Count);
        while (additionalMarks.Count < count) { var s = shapes[rand.Next(shapes.Count)]; if (!baseMarks.Contains(s) && !additionalMarks.Contains(s)) additionalMarks.Add(s); }
        var ban = new List<string>();
        count = Math.Min(rand.Next(0, 3), liquids.Count);
        while (ban.Count < count) { var l = liquids[rand.Next(liquids.Count)]; if (!ban.Contains(l)) ban.Add(l); }
        _system.WorkshopList.Add(new Workshop(fullName, baseMarks, additionalMarks, ban,
            ids: _system.JarIds, vocabulary: _system.Vocabulary, modifiers: _system.Modifiers));
    }

    /// <summary>调整工作台容量；溢出资源直接丢弃并计数（仓库单向流动，不回流中央仓库）。</summary>
    public object? UpdateWorkshopCapacity(int index, int? maxJars, int? maxLiquids)
    {
        var ws = _system.Workshops.ElementAtOrDefault(index);
        if (ws == null) return new { success = false, reason = "无效工作台索引" };
        // 容量规则住在核心（原先只在 Web 适配层校验 ⇒ 只有 Web 宿主遵守）：
        // 负数会让 Resize*Capacity 的区间移除越界；0 则让工作台永久收不了罐子。
        if (maxJars is < 1) throw new DomainValidationException("罐位上限必须是正整数");
        if (maxLiquids is < 1) throw new DomainValidationException("液体上限必须是正整数");
        if (maxJars.HasValue)
        {
            var discarded = ws.ResizeJarCapacity(maxJars.Value);
            if (discarded > 0) _system.ProducerModule.Warehouse.CountDiscardedJars(discarded);
        }
        if (maxLiquids.HasValue)
        {
            foreach (var kv in ws.ResizeLiquidCapacity(maxLiquids.Value))
                _system.ProducerModule.Warehouse.CountDiscardedLiquids(kv.Key, kv.Value);
        }
        return new { success = true };
    }

}

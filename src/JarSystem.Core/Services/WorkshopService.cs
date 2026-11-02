using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;

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


    /// <summary>
    /// 更换工作台的能力参数（标记 / 禁令）—— 运行期"换一套规则"的唯一入口。
    ///
    /// 三条拒绝条件都是**世界自洽**的要求，不是输入校验：
    /// ① 索引无效；
    /// ② 标记或禁令不在词表内（世界只有这些形状与液体，写进去永远无法被满足）；
    /// ③ **新禁令会锁死已在池中的液体** —— `IsBanned` 同时挡住 PourIn / PourOut / 空罐改形成本 /
    ///    满罐自耗，池里存着该液体时它就再也倒不出来，世界当场违反 `BANNED-LIQUID-STORED`。
    ///    所以先要求把池子腾空（倒进罐子、或换台），再改禁令。
    ///
    /// 只换规则参数，**不动**罐子与游离液体：资源搬迁是 `SetMaster` 切换事件的职责。
    /// 换完允许存在"形状不在新标记里"的罐子 —— 标记约束的是操作合法性，不是存储合法性。
    /// </summary>
    public void SetWorkshopCapability(int index, List<string>? baseMarks, List<string>? additionalMarks, List<string>? ban)
    {
        var ws = _system.Workshops.ElementAtOrDefault(index);
        if (ws == null) throw new DomainValidationException("无效工作台索引: " + index);

        var baseList = baseMarks ?? new List<string>();
        var addList = additionalMarks ?? new List<string>();
        var banList = ban ?? new List<string>();

        foreach (var s in baseList)
            if (!_system.Vocabulary.IsValidShape(s)) throw new DomainValidationException("无效形状: " + s);
        foreach (var s in addList)
            if (!_system.Vocabulary.IsValidShape(s)) throw new DomainValidationException("无效形状: " + s);
        foreach (var l in banList)
            if (!_system.Vocabulary.IsValidLiquid(l)) throw new DomainValidationException("无效液体: " + l);

        var stranded = new List<string>();
        foreach (var l in banList)
            if (ws.LiquidStorage.TryGetValue(l, out var n) && n > 0) stranded.Add(l + " ×" + n);
        if (stranded.Count > 0)
            throw new DomainValidationException(
                "新禁令会锁死已在池中的液体（" + string.Join("、", stranded) + "）：先把它们倒空或换台再改");

        ws.ReplaceCapability(new WorkshopCapability(baseList, addList, banList));
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

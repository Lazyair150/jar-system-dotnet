using JarSystem.Core.Config;

namespace JarSystem.Core.Services;

/// <summary>
/// 产出与时间推进（原 <c>JarSystem.Tick / UpdateProducerRate / UpdateLiquidProductionRate</c>）。
///
/// 语义（不可改动）：每次推进 = 生产者累积产罐 → 按速率产液体 → 向当前主台流入（3 罐 2 液，基础形状优先）。
/// 属性一律从门面实时读取（<c>Reset()</c> 会重建 <c>ProducerModule</c> 与液体速率）。
///
/// **2026-10（P2）拆分**：原先只有一个 <c>Tick(count)</c>，现在拆成
/// <see cref="TickStep"/>（一个世界步进的产出）+ <see cref="FlushFlow"/>（流入一次），
/// 由门面按"每个 tick 一次 TickStep、每次宿主 Tick 调用一次 FlushFlow"组合。
/// 拆分的目的是把**逻辑时钟与 tick 管线**插到"每个世界步进"这一层（见 <c>Process.TickPipeline</c>），
/// 而状态结果与拆分前逐项相同（生产者逐 tick 入库 ≡ 批量入库；液体原本就是逐 tick 循环；流入仍是一次）。
/// </summary>
internal sealed class ProductionService
{
    private readonly JarSystem _system;

    public ProductionService(JarSystem system)
    {
        _system = system;
    }

    /// <summary>推进**一个世界步进的产出部分**：生产者累积产罐 + 按速率产液体（不含流入）。</summary>
    internal void TickStep()
    {
        _system.ProducerModule.Tick(1);
        // S0（数据驱动）：按**词表**产出，不再写死 酒/蜜 两个名字。
        // 液体仍然固定为 2 种（这是语义），这里去掉的只是"名字重复出现在逻辑里"。
        // 顺序仍是词表顺序（酒 → 蜜），因此行为与重构前逐次相同。
        foreach (var liquid in _system.Vocabulary.Liquids)
        {
            // P3b：强度修饰器可折减产出速率（整数千分比；无修饰器时恒等 ⇒ 行为不变）
            var rate = _system.Modifiers.ApplyIntensity(
                "liquid:" + liquid, _system.LiquidProductionRate.GetValueOrDefault(liquid));
            if (rate > 0) _system.ProducerModule.Warehouse.ProduceLiquid(liquid, rate);
        }
    }

    /// <summary>
    /// 向当前主台流入一次。**每次宿主 <c>Tick(count)</c> 调用一次**（不是每个 tick 一次）——
    /// 这是既有语义（"每次流入"的批量口径），P2 刻意保持不变，改它需要单独裁决。
    /// </summary>
    internal void FlushFlow()
    {
        if (!_system.CurrentMaster.HasValue) return;
        var master = _system.Workshops[_system.CurrentMaster.Value];
        if (master == null) return;
        // P4d：流入参数也可被强度修饰器折算（整数千分比；无匹配 ⇒ 恒等）
        // 目标名：`flow:jars`（每次流入的罐子数）、`flow:liquid`（每次流入的液体量）。
        var jars = _system.Modifiers.ApplyIntensity("flow:jars", _system.FlowSettings.JarCount);
        var liquid = _system.Modifiers.ApplyIntensity("flow:liquid", _system.FlowSettings.LiquidAmount);
        _system.ProducerModule.Warehouse.FlowToMaster(master, jars, liquid);
    }

    /// <summary>
    /// 设置某生产者的某形状速率。规则（生产者存在 / 形状存在 / 速率上界）统一住在
    /// <see cref="Production.ProducerModule.UpdateRate"/> —— 那里才是这份数据的归属地，
    /// 于是直接调模块的宿主也受同一条约束；本方法只做转发，不再重复一份规则。
    /// </summary>
    public void UpdateProducerRate(string name, string shape, double rate) =>
        _system.ProducerModule.UpdateRate(name, shape, rate);

    public void UpdateLiquidProductionRate(string liquid, int rate)
    {
        // 注意：走 **Core**（不写日志）—— 会话日志由门面的 UpdateLiquidProductionRate 记一条即可，
        // 否则同一次宿主意图会被记两条（回放时日志越滚越长）。
        if (_system.LiquidProductionRate.ContainsKey(liquid)) _system.SetLiquidProductionRateCore(liquid, rate);
    }
}

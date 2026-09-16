using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Services;

/// <summary>
/// 产出与时间推进（原 <c>JarSystem.Tick / UpdateProducerRate / UpdateLiquidProductionRate</c>）。
///
/// 语义（不可改动）：每次推进 = 生产者累积产罐 → 按速率产液体 → 向当前主台流入（3 罐 2 液，基础形状优先）。
/// 属性一律从门面实时读取（<c>Reset()</c> 会重建 <c>ProducerModule</c> 与液体速率）。
/// </summary>
internal sealed class ProductionService
{
    private readonly JarSystem _system;

    public ProductionService(JarSystem system)
    {
        _system = system;
    }

    /// <summary>推进 count 次；返回值恒为 0（保持对外签名与行为）。</summary>
    public int Tick(int count = 1)
    {
        _system.ProducerModule.Tick(count);
        for (int i = 0; i < count; i++)
        {
            // S0（数据驱动）：按**词表**产出，不再写死 酒/蜜 两个名字。
            // 液体仍然固定为 2 种（这是语义），这里去掉的只是"名字重复出现在逻辑里"。
            // 顺序仍是词表顺序（酒 → 蜜），因此行为与重构前逐次相同。
            foreach (var liquid in _system.Vocabulary.Liquids)
            {
                var rate = _system.LiquidProductionRate.GetValueOrDefault(liquid);
                if (rate > 0) _system.ProducerModule.ProduceLiquid(liquid, rate);
            }
        }
        if (_system.CurrentMaster.HasValue)
        {
            var master = _system.Workshops[_system.CurrentMaster.Value];
            if (master != null)
                _system.ProducerModule.FlowToMaster(master, _system.FlowSettings.JarCount, _system.FlowSettings.LiquidAmount);
        }
        return 0;
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
        if (_system.LiquidProductionRate.ContainsKey(liquid)) _system.SetLiquidProductionRate(liquid, rate);
    }
}

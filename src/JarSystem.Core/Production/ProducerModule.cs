using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

public class Producer
{
    public string Name { get; }
    public Dictionary<string, double> Rates { get; }

    public Producer(string name, Dictionary<string, double> rates)
    {
        Name = name;
        Rates = new Dictionary<string, double>(rates);
    }
}

public class ShapeProductionLine
{
    private readonly Vocabulary _vocabulary;

    public string Shape { get; }
    public List<(string Name, double Rate)> Producers { get; } = new();
    public double Accumulated { get; set; }

    /// <param name="vocabulary">词表（S2）：本线产出的罐子按它校验形状；null 表示内置默认词表。</param>
    public ShapeProductionLine(string shape, Vocabulary? vocabulary = null)
    {
        Shape = shape;
        _vocabulary = vocabulary ?? Vocabulary.Default;
    }

    public void AddProducer(Producer p)
    {
        if (p.Rates.TryGetValue(Shape, out var rate))
            Producers.Add((p.Name, rate));
    }

    public void RemoveProducer(string name) =>
        Producers.RemoveAll(p => p.Name == name);

    public void UpdateRate(string name, double rate)
    {
        var idx = Producers.FindIndex(p => p.Name == name);
        if (idx >= 0)
            Producers[idx] = (name, rate);
    }

    public List<Jar> Tick()
    {
        var total = Producers.Sum(p => p.Rate);
        Accumulated += total;
        var n = (int)Math.Floor(Accumulated);
        Accumulated -= n;
        var jars = new List<Jar>();
        for (int i = 0; i < n; i++)
            jars.Add(new Jar(Shape, null, null, _vocabulary));
        return jars;
    }

    public double GetTotalRate() => Producers.Sum(p => p.Rate);

}

public class ProducerModule
{
    private readonly Dictionary<string, ShapeProductionLine> _generators = new();
    private readonly Dictionary<string, Producer> _producers = new();
    private readonly List<Jar> _warehouse = new();

    // S2：词表（形状/液体目录）——生产线的建立、Tick 的遍历、罐子创建都按它来；
    // 因此"换一份词表 = 多/少一种形状的生产线"，不需要改生产模块的代码。
    private readonly Vocabulary _vocabulary;
    // S0（数据驱动）：液体账目按**词表**初始化（原先写死 酒/蜜 两个名字）。
    // 逐项构造而非集合初始化器，保证键的**插入顺序 = 词表顺序**（契约快照里 liquids 的键序依赖它）。
    private readonly Dictionary<string, int> _liquidWarehouse = new();
    private readonly Dictionary<string, int> _discardedLiquids = new();

    /// <summary>各形状的生产线（只读视图）。</summary>
    public IReadOnlyDictionary<string, ShapeProductionLine> Generators => _generators;

    /// <summary>已注册的生产者（只读视图）。</summary>
    public IReadOnlyDictionary<string, Producer> Producers => _producers;

    /// <summary>中央仓库中的罐子（只读视图；增删只能经 Tick / FlowToMaster）。</summary>
    public IReadOnlyList<Jar> Warehouse => _warehouse;

    /// <summary>中央仓库液体库存（只读视图；变更只能经 ProduceLiquid / FlowToMaster）。</summary>
    public IReadOnlyDictionary<string, int> LiquidWarehouse => _liquidWarehouse;

    /// <summary>因仓库上限而丢弃的液体计数（只读视图）。</summary>
    public IReadOnlyDictionary<string, int> DiscardedLiquids => _discardedLiquids;

    /// <summary>仓库中的罐子数量（替代原先泄漏内部列表的 GetWarehouseJars）。</summary>
    public int WarehouseJarCount => _warehouse.Count;

    /// <summary>因仓库/工作台容量上限而丢弃的罐子计数。</summary>
    public int DiscardedJars { get; private set; }

    public void DiscardJars(int n) => DiscardedJars += n;
    public void DiscardLiquids(string liquid, int n) =>
        _discardedLiquids[liquid] = _discardedLiquids.GetValueOrDefault(liquid, 0) + n;

    /// <param name="vocabulary">词表（S2）；null 表示内置默认词表（行为与重构前一致）。</param>
    public ProducerModule(Vocabulary? vocabulary = null)
    {
        _vocabulary = vocabulary ?? Vocabulary.Default;
        foreach (var s in _vocabulary.Shapes)
            _generators[s] = new ShapeProductionLine(s, _vocabulary);
        // S0（数据驱动）：液体账目按**词表**初始化（原先写死 酒/蜜 两个名字）。
        // 逐项赋值保证键的**插入顺序 = 词表顺序**（契约快照里 liquids 的键序依赖它）。
        foreach (var liquid in _vocabulary.Liquids)
        {
            _liquidWarehouse[liquid] = 0;
            _discardedLiquids[liquid] = 0;
        }
    }

    public Producer RegisterProducer(string name, Dictionary<string, double> rates)
    {
        var p = new Producer(name, rates);
        _producers[name] = p;
        foreach (var s in rates.Keys)
            if (_generators.ContainsKey(s))
                _generators[s].AddProducer(p);
        return p;
    }

    public void RemoveProducer(string name)
    {
        _producers.Remove(name);
        foreach (var g in _generators.Values)
            g.RemoveProducer(name);
    }

    /// <summary>
    /// 设置某生产者对某形状的产出速率。**三条规则都住在这一层**（数据在这里 ⇒ 规则在这里）：
    /// <list type="number">
    /// <item>生产者必须存在 —— 否则"静默无操作"，调用方以为改成功了（原实现如此）；</item>
    /// <item>形状必须存在 —— 否则会往 <c>Rates</c> 里插入一个**没有对应生产线**的垃圾键，
    /// 而 <c>Rates</c> 会经 <c>producerStatus.producers[].rates</c> 进入对外契约（原实现实测可复现）；</item>
    /// <item>速率必须是 0~<see cref="Constants.MAX_PRODUCER_RATE"/> 的有限数 —— 否则产出循环会被放大到 OOM。</item>
    /// </list>
    /// </summary>
    public void UpdateRate(string name, string shape, double rate)
    {
        if (!_producers.TryGetValue(name, out var p))
            throw new DomainValidationException("没有这个生产者: " + name);
        if (!_generators.ContainsKey(shape))
            throw new DomainValidationException("没有这个形状: " + shape);
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0 || rate > Constants.MAX_PRODUCER_RATE)
            throw new DomainValidationException($"速率必须是 0 到 {Constants.MAX_PRODUCER_RATE} 之间的有限数，收到: {rate}");

        p.Rates[shape] = rate;
        _generators[shape].UpdateRate(name, rate);
    }

    public List<Jar> Tick(int count = 1)
    {
        var newJars = new List<Jar>();
        for (int i = 0; i < count; i++)
        {
            foreach (var s in _vocabulary.Shapes)
                newJars.AddRange(_generators[s].Tick());
        }
        // 仓库上限：溢出丢弃并计数，避免长跑无限膨胀
        foreach (var j in newJars)
        {
            if (_warehouse.Count < Constants.WAREHOUSE_MAX_JARS) _warehouse.Add(j);
            else DiscardedJars++;
        }
        return newJars;
    }

    public void ProduceLiquid(string liquid, int amount)
    {
        var cur = _liquidWarehouse.GetValueOrDefault(liquid, 0);
        var accepted = Math.Min(amount, Math.Max(0, Constants.WAREHOUSE_MAX_LIQUIDS - cur));
        _liquidWarehouse[liquid] = cur + accepted;
        _discardedLiquids[liquid] = _discardedLiquids.GetValueOrDefault(liquid, 0) + (amount - accepted);
    }

    public (List<Jar> Jars, Dictionary<string, int> Liquids) FlowToMaster(
        Workshop master, int jarCount = 3, int liquidAmount = 2)
    {
        var transferLog = new Dictionary<string, object>();
        var transferredJars = new List<Jar>();

        // 1. 罐子转移（带容量检查）
        int taken = 0;
        while (taken < jarCount && _warehouse.Count > 0)
        {
            // 主台基础形状优先，减少与主台无关形状的滞留
            int pick = _warehouse.FindIndex(j => master.IsBaseMarked(j.Shape));
            if (pick < 0) pick = 0;
            var jar = _warehouse[pick];
            _warehouse.RemoveAt(pick);
            if (master.AddJar(jar))
            {
                transferredJars.Add(jar);
                taken++;
            }
            else
            {
                // 主台满了，返还仓库
                _warehouse.Insert(pick, jar);
                break;
            }
        }

        // 2. 液体转移（禁令与容量由工作台的意图方法统一裁决）
        var transferredLiquids = new Dictionary<string, int>();
        foreach (var kv in _liquidWarehouse.ToList())
        {
            var liquid = kv.Key;
            var amount = Math.Min(liquidAmount, kv.Value);
            if (amount <= 0) continue;

            // 容量感知的部分填充：主台能收多少收多少，被禁/满则全额留在仓库
            var accepted = master.TryReceiveLiquid(liquid, amount);
            if (accepted <= 0) continue;

            _liquidWarehouse[liquid] = kv.Value - accepted;
            transferredLiquids[liquid] = accepted;
        }

        return (transferredJars, transferredLiquids);
    }

    // 2026-09 删除两处死代码（grep 证实无调用方）：
    //   · GetWarehouseLiquid —— 只读单个液体库存，已被 LiquidWarehouse 只读视图取代
    //   · GetProducerStatus —— 弱类型报告字典，已被 StateSerializer + StateDtos 的强类型投影取代
    //     （后者才是对外契约，且键序由 [JsonPropertyName] 与快照钉死）
}

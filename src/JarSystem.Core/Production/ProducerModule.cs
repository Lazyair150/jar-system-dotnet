using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

public class Producer
{
    public string Name { get; }

    // 私有字典 + 只读视图。Rates 会经 producerStatus.producers[].rates 进入对外契约；
    // 原先 external 拿到的是可变 Dictionary，能塞进"没有对应生产线"的垃圾键 ——
    // UpdateRate 的第 2 条规则专门在防这件事，而公开可变字典让那道防线形同虚设。
    private readonly Dictionary<string, double> _rates;

    /// <summary>各形状的产出速率（对外只读）。</summary>
    public IReadOnlyDictionary<string, double> Rates => _rates;

    public Producer(string name, Dictionary<string, double> rates)
    {
        Name = name;
        _rates = new Dictionary<string, double>(rates);
    }

    /// <summary>设置某形状的速率。仅供 <see cref="ProducerModule.UpdateRate"/> 调用 —— 规则住在那一层。</summary>
    internal void SetRate(string shape, double rate) => _rates[shape] = rate;
}

public class ShapeProductionLine
{
    private readonly Vocabulary _vocabulary;

    public string Shape { get; }

    // 私有列表 + 只读视图（元素是值元组，因此只读视图就足够）。
    private readonly List<(string Name, double Rate)> _producerList = new();

    /// <summary>本线上参与产出的生产者及其速率（对外只读）。</summary>
    public IReadOnlyList<(string Name, double Rate)> Producers => _producerList;

    /// <summary>产出累加器（对外只读；只有本类的 <see cref="Tick"/> 会推进它）。</summary>
    public double Accumulated { get; private set; }

    /// <param name="vocabulary">词表（S2）：本线产出的罐子按它校验形状；null 表示内置默认词表。</param>
    public ShapeProductionLine(string shape, Vocabulary? vocabulary = null)
    {
        Shape = shape;
        _vocabulary = vocabulary ?? Vocabulary.Default;
    }

    public void AddProducer(Producer p)
    {
        if (p.Rates.TryGetValue(Shape, out var rate))
            _producerList.Add((p.Name, rate));
    }

    public void RemoveProducer(string name) =>
        _producerList.RemoveAll(p => p.Name == name);

    public void UpdateRate(string name, double rate)
    {
        var idx = _producerList.FindIndex(p => p.Name == name);
        if (idx >= 0)
            _producerList[idx] = (name, rate);
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

    // S2：词表（形状/液体目录）——生产线的建立、Tick 的遍历、罐子创建都按它来；
    // 因此"换一份词表 = 多/少一种形状的生产线"，不需要改生产模块的代码。
    private readonly Vocabulary _vocabulary;

    /// <summary>
    /// **中央仓库**（2026-09 从本类抽出）：罐子/液体库存、容量与丢弃计数、仓库→主台的流入规则。
    /// 本类从此只管"生产者与生产线"；仓库那半边的规则在 <see cref="Production.Warehouse"/> 里自成一处。
    /// </summary>
    public Warehouse Warehouse { get; }

    /// <summary>各形状的生产线（只读视图）。</summary>
    public IReadOnlyDictionary<string, ShapeProductionLine> Generators => _generators;

    /// <summary>已注册的生产者（只读视图）。</summary>
    public IReadOnlyDictionary<string, Producer> Producers => _producers;

    /// <summary>
    /// 兼容转发：仓库液体库存。保留它是因为 `test/JarSystem.Tests`（xUnit）直接用了这个成员，
    /// 而该工程在本机**离线无法编译**（NuGet 还原受限）⇒ 不动它的调用面。
    /// **新代码请用 <see cref="Warehouse"/>。
    /// </summary>
    public IReadOnlyDictionary<string, int> LiquidWarehouse => Warehouse.LiquidWarehouse;

    /// <param name="vocabulary">词表（S2）；null 表示内置默认词表（行为与重构前一致）。</param>
    public ProducerModule(Vocabulary? vocabulary = null)
    {
        _vocabulary = vocabulary ?? Vocabulary.Default;
        foreach (var s in _vocabulary.Shapes)
            _generators[s] = new ShapeProductionLine(s, _vocabulary);
        Warehouse = new Warehouse(_vocabulary);
    }

    /// <summary>
    /// 注册生产者。**重名直接拒绝**（2026-09）：原实现是覆盖 <c>_producers[name]</c> 同时向生产线
    /// **再追加一条**同名的产出记录 ⇒ 该形状的速率静默翻倍（实测 0.4 → 0.8），而且会进
    /// <c>producerStatus.generators[].rate</c> 契约字段。改速率请用 <see cref="UpdateRate"/>。
    /// </summary>
    public Producer RegisterProducer(string name, Dictionary<string, double> rates)
    {
        if (_producers.ContainsKey(name))
            throw new DomainValidationException("生产者已存在: " + name + "（改速率请用 UpdateRate）");

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

        p.SetRate(shape, rate);
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
        Warehouse.StoreJars(newJars);
        return newJars;
    }

    // 2026-09 删除两处死代码（grep 证实无调用方）：
    //   · GetWarehouseLiquid —— 只读单个液体库存，已被 LiquidWarehouse 只读视图取代
    //   · GetProducerStatus —— 弱类型报告字典，已被 StateSerializer + StateDtos 的强类型投影取代
    //     （后者才是对外契约，且键序由 [JsonPropertyName] 与快照钉死）
}

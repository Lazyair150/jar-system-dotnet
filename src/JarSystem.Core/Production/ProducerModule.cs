using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    // 真只读包装（2026-09-16）：Rates 经 producerStatus.producers[].rates **进对外契约**，
    // 交出裸 Dictionary 时外部可以就地改速率（连 UpdateRate 的校验一起绕过）。
    private readonly IReadOnlyDictionary<string, double> _ratesView;

    /// <summary>各形状的产出速率（对外只读）。</summary>
    public IReadOnlyDictionary<string, double> Rates => _ratesView;

    public Producer(string name, Dictionary<string, double> rates)
    {
        Name = name;
        _rates = new Dictionary<string, double>(rates);
        _ratesView = new ReadOnlyDictionary<string, double>(_rates);
    }

    /// <summary>设置某形状的速率。仅供 <see cref="ProducerModule.UpdateRate"/> 调用 —— 规则住在那一层。</summary>
    internal void SetRate(string shape, double rate) => _rates[shape] = rate;
}

public class ShapeProductionLine
{
    private readonly Vocabulary _vocabulary;
    private readonly IIntensitySource? _intensity;
    private readonly JarPool? _pool;

    public string Shape { get; }

    // 私有列表 + 只读视图（元素是值元组，因此"真只读包装"足够）。
    private readonly List<(string Name, double Rate)> _producerList = new();

    private readonly IReadOnlyList<(string Name, double Rate)> _producerListView;

    /// <summary>本线上参与产出的生产者及其速率（对外只读）。</summary>
    public IReadOnlyList<(string Name, double Rate)> Producers => _producerListView;

    /// <summary>这条形状线当前是否有正速率生产源。没有时 Tick 不需要进入求和与累加路径。</summary>
    public bool HasPositiveRate => _producerList.Any(p => p.Rate > 0);

    /// <summary>
    /// 产出累加器（对外只读；**契约字段** <c>producerStatus.generators[].accumulated</c> 读它）。
    ///
    /// 2026-10（P2c）加固：内部改为**整数定点**（千分之一）累加，这里换算回小数输出。
    /// 动机是**实测到的浮点漂移** —— 修前该字段的实测值是
    /// <c>0.20000000000000018</c> / <c>0.8999999999999999</c> / <c>0.7999999999999999</c>（本该是 0.2 / 0.9 / 0.8），
    /// 而 <see cref="Tick"/> 用 <c>Math.Floor</c> 取整产出 ⇒ 累加值一旦落在整数**下方**
    /// （例如 6.999999999999999）就会**少产一个罐子**，且结果随运行时/平台可能不同（确定性风险）。
    /// 定点化后：只在注册边界上取整一次，之后全是整数运算 ⇒ 逐 tick 结果与平台无关。
    /// 对外契约只改了这 3 个**数值**（键名与形状不变）；基线已于 2026-10-16 **有意重建**（见 `过程系统/03_*.md`）。
    /// </summary>
    public double Accumulated => _accumulatedMicro / (double)MicroUnit;

    /// <summary>资源量累加器（百万分之一单位）；支持任意千分之一逻辑时间增量。</summary>
    private long _accumulatedMicro;

    /// <summary>速率注册时以千分之一保存，时间增量也以千分之一传入。</summary>
    private const int MilliUnit = 1000;
    private const long MicroUnit = 1_000_000;

    /// <param name="vocabulary">词表（S2）：本线产出的罐子按它校验形状；null 表示内置默认词表。</param>
    /// <param name="intensity">强度折算出口（可选）：`producer:形状` 目标可折减本线产出。</param>
    public ShapeProductionLine(string shape, Vocabulary? vocabulary = null, IIntensitySource? intensity = null, JarPool? pool = null)
    {
        Shape = shape;
        _vocabulary = vocabulary ?? Vocabulary.Default;
        _intensity = intensity;
        _pool = pool;
        _producerListView = _producerList.AsReadOnly();
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
        else
            _producerList.Add((name, rate));
    }

    /// <summary>旧 tick 入口：一个 tick 等价于一个逻辑时间单位，保持旧世界调用语义。</summary>
    public List<Jar> Tick() => AdvanceLogicalTime(MilliUnit);

    /// <summary>
    /// 按逻辑时间推进产线。deltaLogicalTimeMilli 的 1000 表示 1 个逻辑时间单位。
    /// 速率与时间分别以千分之一表示，乘积直接进入百万分之一资源累加器，
    /// 因而分段推进与一次性推进不会因浮点误差改变产出数量。
    /// </summary>
    public List<Jar> AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));

        var totalMilli = Producers.Sum(p => Milli(p.Rate));
        // P4c：强度修饰器可折减某形状的生产者速率（整数千分比；无匹配 ⇒ 恒等）
        if (_intensity != null) totalMilli = _intensity.Apply("producer:" + Shape, totalMilli);
        _accumulatedMicro = checked(_accumulatedMicro + (long)totalMilli * deltaLogicalTimeMilli);

        var produced = _accumulatedMicro / MicroUnit;
        if (produced > int.MaxValue)
            throw new InvalidOperationException("单次逻辑时间推进产生的罐子数量超过可表示范围");
        var count = (int)produced;
        _accumulatedMicro -= produced * MicroUnit;

        var jars = new List<Jar>(count);
        for (int i = 0; i < count; i++)
            jars.Add(_pool != null
                ? _pool.Rent(Shape, null, null, _vocabulary)
                : new Jar(Shape, null, null, _vocabulary));
        return jars;
    }

    public double GetTotalRate() => Producers.Sum(p => p.Rate);

    /// <summary>
    /// 速率 → 定点整数（千分之一）。**只在注册/改速率的边界上取整一次**，
    /// 之后累加全程是整数（这就是"确定性"的来源）。
    /// </summary>
    private static int Milli(double rate) => (int)Math.Round(rate * MilliUnit, MidpointRounding.AwayFromZero);

}

public class ProducerModule
{
    private readonly Dictionary<string, ShapeProductionLine> _generators = new();
    private readonly Dictionary<string, Producer> _producers = new();

    // S2：词表（形状/液体目录）——生产线的建立、Tick 的遍历、罐子创建都按它来；
    // 因此"换一份词表 = 多/少一种形状的生产线"，不需要改生产模块的代码。
    private readonly Vocabulary _vocabulary;

    /// <summary>强度折算出口（可选）：透传给各条生产线（目标名 `producer:形状`）。</summary>
    private readonly IIntensitySource? _intensity;

    /// <summary>罐子对象池（可选）：生产优先复用丢弃回收的罐子；null 时退化为直接 new。</summary>
    private readonly JarPool? _pool;

    /// <summary>
    /// **中央仓库**（2026-09 从本类抽出）：罐子/液体库存、容量与丢弃计数、仓库→主台的流入规则。
    /// 本类从此只管"生产者与生产线"；仓库那半边的规则在 <see cref="Production.Warehouse"/> 里自成一处。
    /// </summary>
    public Warehouse Warehouse { get; }

    // 真只读包装（2026-09-16）：这两张表都进对外契约（producerStatus 的 producers/generators）。
    private readonly IReadOnlyDictionary<string, ShapeProductionLine> _generatorsView;
    private readonly IReadOnlyDictionary<string, Producer> _producersView;

    /// <summary>各形状的生产线（只读视图）。</summary>
    public IReadOnlyDictionary<string, ShapeProductionLine> Generators => _generatorsView;

    /// <summary>已注册的生产者（只读视图）。</summary>
    public IReadOnlyDictionary<string, Producer> Producers => _producersView;

    /// <summary>
    /// 兼容转发：仓库液体库存。保留它是因为 `test/JarSystem.Tests`（xUnit）直接用了这个成员，
    /// 而该工程在本机**离线无法编译**（NuGet 还原受限）⇒ 不动它的调用面。
    /// **新代码请用 <see cref="Warehouse"/>。
    /// </summary>
    public IReadOnlyDictionary<string, int> LiquidWarehouse => Warehouse.LiquidWarehouse;

    /// <param name="vocabulary">词表（S2）；null 表示内置默认词表（行为与重构前一致）。</param>
    public ProducerModule(Vocabulary? vocabulary = null, IIntensitySource? intensity = null, JarPool? pool = null)
    {
        _vocabulary = vocabulary ?? Vocabulary.Default;
        _intensity = intensity;
        // 只读包装最先建立（活视图 ⇒ 后续注册的生产者/生产线照样反映）
        _generatorsView = new ReadOnlyDictionary<string, ShapeProductionLine>(_generators);
        _producersView = new ReadOnlyDictionary<string, Producer>(_producers);
        // 池必须在建生产线**之前**赋值：生产线持有它，生产罐子时用。
        _pool = pool;
        foreach (var s in _vocabulary.Shapes)
            _generators[s] = new ShapeProductionLine(s, _vocabulary, _intensity, _pool);
        Warehouse = new Warehouse(_vocabulary, pool);
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

        // **注册时也校验速率**（2026-09-16 补）：原先只跳过"没有对应生产线"的形状，
        // 却把垃圾键留在 Producer.Rates 里 —— 而 Rates 会经 producerStatus.producers[].rates
        // 进入**对外契约**（与 UpdateRate 第 2 条规则是同一件事，只是入口不同）。
        // 值域规则也照抄 UpdateRate：否则一个 NaN/超限速率就能把产出循环打爆。
        foreach (var kv in rates)
        {
            if (!_generators.ContainsKey(kv.Key))
                throw new DomainValidationException("没有这个形状: " + kv.Key);
            if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value)
                || kv.Value < 0 || kv.Value > Constants.MAX_PRODUCER_RATE)
                throw new DomainValidationException(
                    $"速率必须是 0 到 {Constants.MAX_PRODUCER_RATE} 之间的有限数，收到: {kv.Value}");
        }

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
    /// <summary>
    /// 产线卡槽容量（0 = 不限）。由门面从 <c>WorldOptions.ProducerSlots</c> 灌入；
    /// 只在运行期"新开一条产线"时生效，装配期（<see cref="RegisterProducer"/>）不受约束 ——
    /// 世界文件是作者意图，装配不该被自己声明的上限拒绝。
    /// </summary>
    public int ProducerSlots { get; set; }

    /// <summary>已装备的产线条数（速率 &gt; 0 的 (生产者, 形状) 对）。</summary>
    public int UsedSlots => _producers.Values.Sum(p => p.Rates.Values.Count(r => r > 0));

    public void UpdateRate(string name, string shape, double rate)
    {
        if (!_producers.TryGetValue(name, out var p))
            throw new DomainValidationException("没有这个生产者: " + name);
        if (!_generators.ContainsKey(shape))
            throw new DomainValidationException("没有这个形状: " + shape);
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0 || rate > Constants.MAX_PRODUCER_RATE)
            throw new DomainValidationException($"速率必须是 0 到 {Constants.MAX_PRODUCER_RATE} 之间的有限数，收到: {rate}");

        // 第四条规则：卡槽上限。只在"从 0 开到 >0"时占槽 —— 已装备的那条改速率不算新条。
        var alreadyActive = p.Rates.TryGetValue(shape, out var before) && before > 0;
        if (rate > 0 && !alreadyActive && ProducerSlots > 0 && UsedSlots >= ProducerSlots)
            throw new DomainValidationException(
                $"产线槽已满（{ProducerSlots} 条）：先把一条产线的速率降到 0，再开新的");

        p.SetRate(shape, rate);
        _generators[shape].UpdateRate(name, rate);
    }

    public List<Jar> Tick(int count = 1)
    {
        var newJars = new List<Jar>();
        for (int i = 0; i < count; i++)
        {
            foreach (var s in _vocabulary.Shapes)
                if (_generators[s].HasPositiveRate)
                    newJars.AddRange(_generators[s].Tick());
        }
        // 仓库上限：溢出丢弃并计数，避免长跑无限膨胀
        Warehouse.StoreJars(newJars);
        return newJars;
    }

    /// <summary>
    /// 按逻辑时间推进所有形状产线一次，并将新罐子放入中央仓库。
    /// 与 Tick(count) 不同，本方法不把时间离散成整数 tick。
    /// </summary>
    public List<Jar> AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));

        var newJars = new List<Jar>();
        foreach (var shape in _vocabulary.Shapes)
            if (_generators[shape].HasPositiveRate)
                newJars.AddRange(_generators[shape].AdvanceLogicalTime(deltaLogicalTimeMilli));

        Warehouse.StoreJars(newJars);
        return newJars;
    }

    // 2026-09 删除两处死代码（grep 证实无调用方）：
    //   · GetWarehouseLiquid —— 只读单个液体库存，已被 LiquidWarehouse 只读视图取代
    //   · GetProducerStatus —— 弱类型报告字典，已被 StateSerializer + StateDtos 的强类型投影取代
    //     （后者才是对外契约，且键序由 [JsonPropertyName] 与快照钉死）
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// **液体的产能**（原先是 <c>JarSystem</c> 里一个 <c>Dictionary&lt;string,int&gt;</c>）。
///
/// 为什么值得独立成概念体：罐子的产能属于**生产者**（按形状、有累加器、有产线槽上限），
/// 液体的产能属于**世界自己**（按品种，没有机器）。两者的规则形状不一样，但"每 tick 产多少、
/// 玩家能不能调、小数速率怎么攒"必须是同一套 —— 所以这里只做液体这一件事，且与
/// <see cref="ShapeProductionLine"/> 用**同一套整数定点累加**：
/// <list type="bullet">
/// <item>速率按千分之一定点（0.2 ⇒ 200），只在设速率那一刻取整一次 ⇒ 逐 tick 结果与平台无关；</item>
/// <item>累加器攒够 1000 才产 1 份，余数留着 ⇒ 0.5 份/tick 就是"两 tick 产一份"；</item>
/// <item>强度修饰器（目标名 <c>liquid:品种</c>）在折算速率时生效，无修饰器时恒等。</item>
/// </list>
/// </summary>
public sealed class LiquidProduction
{
    private const int MilliUnit = 1000;
    private const long MicroUnit = 1_000_000;

    private readonly Vocabulary _vocabulary;
    private IIntensitySource? _intensity;

    // 键序 = 词表顺序（契约快照按行比较，顺序变了就是一次显式重基线）
    private readonly Dictionary<string, int> _rateMilli = new();
    private readonly Dictionary<string, long> _accMilli = new();
    private readonly Dictionary<string, double> _rateView = new();
    private readonly Dictionary<string, double> _accView = new();

    private readonly IReadOnlyDictionary<string, double> _rates;
    private readonly IReadOnlyDictionary<string, double> _accumulated;

    public LiquidProduction(Vocabulary? vocabulary = null, IIntensitySource? intensity = null,
        IReadOnlyDictionary<string, double>? defaults = null)
    {
        _vocabulary = vocabulary ?? Vocabulary.Default;
        _intensity = intensity;
        _rates = new ReadOnlyDictionary<string, double>(_rateView);
        _accumulated = new ReadOnlyDictionary<string, double>(_accView);

        foreach (var liquid in _vocabulary.Liquids)
        {
            var rate = defaults != null && defaults.TryGetValue(liquid, out var r) ? r : 1d;
            _rateMilli[liquid] = Milli(rate);
            _accMilli[liquid] = 0L;
            _rateView[liquid] = rate;
            _accView[liquid] = 0;
        }
    }

    /// <summary>
    /// 接上强度修饰器（装配顺序决定：JarSystem 先建产能、后建 <c>ModifierStack</c>）。
    /// 没有修饰器时 `Apply` 恒等 ⇒ 行为与不接完全一致。
    /// </summary>
    public void AttachIntensity(IIntensitySource? intensity) => _intensity = intensity;

    /// <summary>每种液体每 tick 的产出速率（只读视图；键序 = 词表顺序）。</summary>
    public IReadOnlyDictionary<string, double> Rates => _rates;

    /// <summary>每种液体眼下攒了多少（0 ≤ 值 &lt; 1；只读视图）。</summary>
    public IReadOnlyDictionary<string, double> Accumulated => _accumulated;

    /// <summary>某液体这 tick 是否真的会产出一份（给"卡在 0.99"这类疑问一个直接答案）。</summary>
    public bool WillProduceNextTick(string liquid)
    {
        var rate = _rateMilli.GetValueOrDefault(liquid);
        if (_intensity != null) rate = _intensity.Apply("liquid:" + liquid, rate);
        return _accMilli.GetValueOrDefault(liquid) + (long)rate * MilliUnit >= MicroUnit;
    }

    /// <summary>
    /// 设置速率。规则只有这一份：未知液体、负数、超上限一律拒绝 —— 回放与存档是**外部数据**，
    /// 放行就能造出手工不可达的世界（与 <c>MAX_PRODUCER_RATE</c> 同一条护栏）。
    /// </summary>
    public void SetRate(string liquid, double rate)
    {
        if (!_rateMilli.ContainsKey(liquid))
            throw new DomainValidationException("没有这个液体: " + liquid);
        if (double.IsNaN(rate) || double.IsInfinity(rate))
            throw new DomainValidationException("液体产出速率必须是有限数: " + rate);
        if (rate < 0)
            throw new DomainValidationException("液体产出速率不能为负: " + rate);
        if (rate > Constants.MAX_PRODUCER_RATE)
            throw new DomainValidationException(
                "液体产出速率超出上限（" + Constants.MAX_PRODUCER_RATE + " 份/tick）: " + rate);

        _rateMilli[liquid] = Milli(rate);
        _rateView[liquid] = rate;
    }

    /// <summary>
    /// 推进一个 tick：累加、取整、返回本 tick 真正产出的液体。余数留在累加器里。
    /// 返回顺序 = 词表顺序（确定性：回放与契约快照都依赖它）。
    /// </summary>
    /// <summary>旧 tick 入口：一个 tick 等价于一个逻辑时间单位。</summary>
    public List<(string Liquid, int Amount)> Tick() => AdvanceLogicalTime(MilliUnit);

    /// <summary>
    /// 按逻辑时间推进液体产能。时间增量的 1000 表示 1 个逻辑时间单位；
    /// 速率×时间以百万分之一资源为单位累积，保留不足一份的余数。
    /// </summary>
    public List<(string Liquid, int Amount)> AdvanceLogicalTime(long deltaLogicalTimeMilli)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));

        var produced = new List<(string, int)>();
        foreach (var liquid in _vocabulary.Liquids)
        {
            var rateMilli = _rateMilli[liquid];
            if (_intensity != null) rateMilli = _intensity.Apply("liquid:" + liquid, rateMilli);
            _accMilli[liquid] = checked(_accMilli[liquid] + (long)rateMilli * deltaLogicalTimeMilli);

            var amountLong = _accMilli[liquid] / MicroUnit;
            if (amountLong > int.MaxValue)
                throw new InvalidOperationException("单次逻辑时间推进产生的液体数量超过可表示范围");
            var amount = (int)amountLong;
            _accMilli[liquid] -= amountLong * MicroUnit;
            _accView[liquid] = _accMilli[liquid] / (double)MicroUnit;

            if (amount > 0) produced.Add((liquid, amount));
        }
        return produced;
    }

    /// <summary>捕获液体产能的运行时速率与定点余数。</summary>
    public LiquidProductionRuntimeStateSnapshot CaptureRuntimeState() =>
        new(_rateView, _accMilli);

    /// <summary>校验产能快照与当前词表兼容；不修改当前产能。</summary>
    public void ValidateRuntimeStateSnapshot(LiquidProductionRuntimeStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_rateMilli.Keys.ToHashSet(StringComparer.Ordinal)
            .SetEquals(snapshot.Rates.Keys) ||
            !_accMilli.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(snapshot.AccumulatedMicro.Keys))
            throw new InvalidOperationException("液体产能快照的液种集合与当前词表不一致。");

        foreach (var item in snapshot.Rates)
        {
            if (double.IsNaN(item.Value) || double.IsInfinity(item.Value) ||
                item.Value < 0 || item.Value > Constants.MAX_PRODUCER_RATE)
                throw new DomainValidationException("液体产能快照包含无效速率：" + item.Key);
        }
        foreach (var item in snapshot.AccumulatedMicro)
        {
            if (item.Value < 0 || item.Value >= MicroUnit)
                throw new DomainValidationException("液体产能快照包含越界定点余数：" + item.Key);
        }
    }

    /// <summary>恢复液体产能运行状态；词表和强度修饰器仍由当前世界配置提供。</summary>
    public void RestoreRuntimeState(LiquidProductionRuntimeStateSnapshot snapshot)
    {
        ValidateRuntimeStateSnapshot(snapshot);
        foreach (var liquid in _vocabulary.Liquids)
        {
            var rate = snapshot.Rates[liquid];
            _rateMilli[liquid] = Milli(rate);
            _rateView[liquid] = rate;
            _accMilli[liquid] = snapshot.AccumulatedMicro[liquid];
            _accView[liquid] = _accMilli[liquid] / (double)MicroUnit;
        }
    }

    /// <summary>回到装配时的速率（<see cref="JarSystem.Reset"/> 用）：速率与累加器一起清。</summary>
    public void Reset(IReadOnlyDictionary<string, double> defaults)
    {
        foreach (var liquid in _vocabulary.Liquids)
        {
            var rate = defaults.TryGetValue(liquid, out var r) ? r : 1d;
            _rateMilli[liquid] = Milli(rate);
            _rateView[liquid] = rate;
            _accMilli[liquid] = 0;
            _accView[liquid] = 0;
        }
    }

    /// <summary>速率 → 定点整数（千分之一）。只在设速率的边界上取整一次。</summary>
    private static int Milli(double rate) => (int)Math.Round(rate * MilliUnit, MidpointRounding.AwayFromZero);
}


/// <summary>液体产能运行时快照；保留可见速率与百万分之一份的未产出余数。</summary>
public sealed class LiquidProductionRuntimeStateSnapshot
{
    public LiquidProductionRuntimeStateSnapshot(
        IReadOnlyDictionary<string, double> rates,
        IReadOnlyDictionary<string, long> accumulatedMicro)
    {
        Rates = new ReadOnlyDictionary<string, double>(
            new Dictionary<string, double>(rates, StringComparer.Ordinal));
        AccumulatedMicro = new ReadOnlyDictionary<string, long>(
            new Dictionary<string, long>(accumulatedMicro, StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, double> Rates { get; }
    public IReadOnlyDictionary<string, long> AccumulatedMicro { get; }
}

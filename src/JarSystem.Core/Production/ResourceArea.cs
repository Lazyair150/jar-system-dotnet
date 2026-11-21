using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// World v2 第一阶段的资源生产区。
///
/// 它组合现有罐子生产模块、液体产能与中央仓库：
/// 生产先进入资源库存，再按逻辑时间吞吐额度向主工作台输送。
/// 它不拥有 Jar / Liquid 的第二份库存，也不负责工作台加工。
///
/// 流入速率由装配方显式提供，不擅自把旧版"每次宿主调用流入 3 罐、每种液体 2 份"
/// 转换成新的"每逻辑时间"速率。这样旧 tick 契约与新世界时间语义不会混淆。
/// </summary>
public sealed class ResourceArea
{
    private const int RateUnit = 1000;
    private const long AmountUnit = 1_000_000;

    private readonly ProducerModule _producers;
    private readonly LiquidProduction _liquidProduction;
    private readonly ShipLiquidInventory? _shipLiquidInventory;
    private long _jarFlowRateMilli;
    private long _liquidFlowRateMilli;
    private long _jarFlowAccumulator;
    private long _liquidFlowAccumulator;

    public Warehouse Inventory => _producers.Warehouse;

    /// <summary>资源区按形状生产的罐子生产模块。</summary>
    public ProducerModule Producers => _producers;

    /// <summary>资源区按品种生产的游离液体产能。</summary>
    public LiquidProduction LiquidProduction => _liquidProduction;

    /// <summary>World v2 的自机长期液库；为空时使用旧仓库液体路径（迁移兼容）。</summary>
    public ShipLiquidInventory? ShipLiquidInventory => _shipLiquidInventory;

    /// <param name="jarFlowRate">每逻辑时间最多向主台输送的罐子数量。</param>
    /// <param name="liquidFlowRate">每逻辑时间、每种液体最多向主台输送的份数。</param>
    public ResourceArea(
        ProducerModule producers,
        LiquidProduction liquidProduction,
        double jarFlowRate,
        double liquidFlowRate,
        ShipLiquidInventory? shipLiquidInventory = null)
    {
        _producers = producers ?? throw new ArgumentNullException(nameof(producers));
        _liquidProduction = liquidProduction ?? throw new ArgumentNullException(nameof(liquidProduction));
        _shipLiquidInventory = shipLiquidInventory;
        _jarFlowRateMilli = ToRateMilli(jarFlowRate, nameof(jarFlowRate));
        _liquidFlowRateMilli = ToRateMilli(liquidFlowRate, nameof(liquidFlowRate));
    }

    public double JarFlowRate => _jarFlowRateMilli / (double)RateUnit;
    public double LiquidFlowRate => _liquidFlowRateMilli / (double)RateUnit;

    public void SetFlowRates(double jarFlowRate, double liquidFlowRate)
    {
        // 两项先完整校验，再一起提交，避免只改成功其中一项。
        var jars = ToRateMilli(jarFlowRate, nameof(jarFlowRate));
        var liquids = ToRateMilli(liquidFlowRate, nameof(liquidFlowRate));
        _jarFlowRateMilli = jars;
        _liquidFlowRateMilli = liquids;
    }

    /// <summary>
    /// 推进资源区一个逻辑时间片。
    /// 生产结果先进入库存；若存在主台，再按本时间片的吞吐额度流入。
    /// deltaLogicalTimeMilli 的 1000 表示 1 个逻辑时间单位。
    /// </summary>
    public ResourceAreaAdvanceResult Advance(long deltaLogicalTimeMilli, Workshop? master)
    {
        if (deltaLogicalTimeMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaLogicalTimeMilli));

        var jarQuota = 0;
        var liquidQuota = 0;
        var nextJarFlowAccumulator = _jarFlowAccumulator;
        var nextLiquidFlowAccumulator = _liquidFlowAccumulator;

        if (master != null && deltaLogicalTimeMilli > 0)
        {
            // 先计算本时间片的吞吐额度。超出单次 API 可表达范围时拒绝整个推进，
            // 不静默截断额度，也不让生产先发生、随后才因额度溢出而失败。
            nextJarFlowAccumulator = checked(_jarFlowAccumulator + _jarFlowRateMilli * deltaLogicalTimeMilli);
            if (_shipLiquidInventory == null)
                nextLiquidFlowAccumulator = checked(_liquidFlowAccumulator + _liquidFlowRateMilli * deltaLogicalTimeMilli);
            var jarQuotaLong = nextJarFlowAccumulator / AmountUnit;
            var liquidQuotaLong = _shipLiquidInventory == null
                ? nextLiquidFlowAccumulator / AmountUnit
                : 0;
            if (jarQuotaLong > int.MaxValue || liquidQuotaLong > int.MaxValue)
                throw new InvalidOperationException("单次资源流入额度超过可表示范围");

            jarQuota = (int)jarQuotaLong;
            liquidQuota = (int)liquidQuotaLong;
            nextJarFlowAccumulator %= AmountUnit;
            nextLiquidFlowAccumulator %= AmountUnit;
        }

        var producedJars = _producers.AdvanceLogicalTime(deltaLogicalTimeMilli);
        var producedLiquids = _liquidProduction.AdvanceLogicalTime(deltaLogicalTimeMilli);
        foreach (var (liquid, amount) in producedLiquids)
        {
            if (_shipLiquidInventory == null)
                Inventory.ProduceLiquid(liquid, amount);
            else
                _shipLiquidInventory.Produce(liquid, amount);
        }

        if (master == null || deltaLogicalTimeMilli == 0)
            return new ResourceAreaAdvanceResult(producedJars.Count, producedLiquids, 0,
                new Dictionary<string, int>());

        // 吞吐额度是"本时间片可处理的上限"，不是资源欠条。
        // 整数额度无论库存/目的地是否足够都会在本时间片消耗；只有小数余量留到下一片。
        _jarFlowAccumulator = nextJarFlowAccumulator;
        if (_shipLiquidInventory == null)
            _liquidFlowAccumulator = nextLiquidFlowAccumulator;
        // 新路径中液体不会由生产区自动流入任何工作台：订单启动从自机液库取用，
        // 只有 B 类会话在主从交接阶段才允许主台向从台泵送液体。
        var moved = Inventory.FlowToMaster(master, jarQuota, _shipLiquidInventory == null ? liquidQuota : 0);

        return new ResourceAreaAdvanceResult(
            producedJars.Count,
            producedLiquids,
            moved.Jars.Count,
            moved.Liquids);
    }

    private static long ToRateMilli(double rate, string parameter)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
            throw new ArgumentOutOfRangeException(parameter, "资源流入速率必须是有限非负数");

        var scaled = Math.Round(rate * RateUnit, MidpointRounding.AwayFromZero);
        if (scaled > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameter, "资源流入速率超出可表示范围");
        return (long)scaled;
    }
}

/// <summary>资源区一次逻辑时间推进的结果快照。</summary>
public sealed class ResourceAreaAdvanceResult
{
    public int ProducedJarCount { get; }
    public IReadOnlyList<(string Liquid, int Amount)> ProducedLiquids { get; }
    public int FlowedJarCount { get; }
    public IReadOnlyDictionary<string, int> FlowedLiquids { get; }

    internal ResourceAreaAdvanceResult(
        int producedJarCount,
        IReadOnlyList<(string Liquid, int Amount)> producedLiquids,
        int flowedJarCount,
        IReadOnlyDictionary<string, int> flowedLiquids)
    {
        ProducedJarCount = producedJarCount;
        ProducedLiquids = producedLiquids;
        FlowedJarCount = flowedJarCount;
        FlowedLiquids = flowedLiquids;
    }
}

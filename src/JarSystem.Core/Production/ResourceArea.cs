using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// World v2 第一阶段的资源生产区。
///
/// 它组合罐子生产模块、液体产能与资源库存：
/// - 罐子进入中央仓库，再按逻辑时间吞吐额度流入主台。
/// - 新 World v2 液体路径直接进入 ShipLiquidInventory，不自动流入任何工作台。
/// - 未注入 ShipLiquidInventory 时保留旧版仓库液体与流入行为，仅用于迁移兼容。
///
/// 它不拥有 Jar / Liquid 的第二份库存，也不负责工作台加工。
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

    /// <summary>主台切换补液优先级：正产能液种优先、产能高者优先，同速率按名称排序；零产能排最后。</summary>
    public IReadOnlyList<string> LiquidProductionPriority => _liquidProduction.Rates
        .OrderByDescending(item => item.Value > 0)
        .ThenByDescending(item => item.Value)
        .ThenBy(item => item.Key, StringComparer.Ordinal)
        .Select(item => item.Key)
        .ToArray();

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
    /// <summary>旧仓库液体流入速率；新自机液库路径不会使用它。</summary>
    public double LiquidFlowRate => _liquidFlowRateMilli / (double)RateUnit;

    public void SetFlowRates(double jarFlowRate, double liquidFlowRate)
    {
        // 两项先完整校验，再一起提交，避免只改成功其中一项。
        var jars = ToRateMilli(jarFlowRate, nameof(jarFlowRate));
        var liquids = ToRateMilli(liquidFlowRate, nameof(liquidFlowRate));
        _jarFlowRateMilli = jars;
        _liquidFlowRateMilli = liquids;
    }

    /// <summary>捕获资源区生产、仓库和流入吞吐的全部运行时余数。</summary>
    public ResourceAreaRuntimeStateSnapshot CaptureRuntimeState() =>
        new(
            _jarFlowRateMilli,
            _liquidFlowRateMilli,
            _jarFlowAccumulator,
            _liquidFlowAccumulator,
            _producers.CaptureRuntimeState(),
            _liquidProduction.CaptureRuntimeState(),
            Inventory.CaptureRuntimeState());

    /// <summary>预检资源区快照；不会修改生产器、仓库或吞吐余数。</summary>
    public void ValidateRuntimeStateSnapshot(ResourceAreaRuntimeStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.JarFlowRateMilli < 0 || snapshot.JarFlowRateMilli > int.MaxValue ||
            snapshot.LiquidFlowRateMilli < 0 || snapshot.LiquidFlowRateMilli > int.MaxValue ||
            snapshot.JarFlowAccumulator < 0 || snapshot.JarFlowAccumulator >= AmountUnit ||
            snapshot.LiquidFlowAccumulator < 0 || snapshot.LiquidFlowAccumulator >= AmountUnit)
            throw new DomainValidationException("资源区快照包含无效流入速率或吞吐余数。");

        _producers.ValidateRuntimeStateSnapshot(snapshot.Producers);
        _liquidProduction.ValidateRuntimeStateSnapshot(snapshot.LiquidProduction);
        Inventory.ValidateRuntimeStateSnapshot(snapshot.Warehouse);
    }

    /// <summary>恢复资源区运行状态；生产拓扑与强度修饰器仍由当前世界装配提供。</summary>
    public void RestoreRuntimeState(ResourceAreaRuntimeStateSnapshot snapshot)
    {
        ValidateRuntimeStateSnapshot(snapshot);
        _producers.RestoreRuntimeState(snapshot.Producers);
        _liquidProduction.RestoreRuntimeState(snapshot.LiquidProduction);
        Inventory.RestoreRuntimeState(snapshot.Warehouse);
        _jarFlowRateMilli = snapshot.JarFlowRateMilli;
        _liquidFlowRateMilli = snapshot.LiquidFlowRateMilli;
        _jarFlowAccumulator = snapshot.JarFlowAccumulator;
        _liquidFlowAccumulator = snapshot.LiquidFlowAccumulator;
    }

    /// <summary>
    /// 推进资源区一个逻辑时间片。
    /// 罐子经仓库流入主台；液体进入自机液库（新路径）或旧仓库（兼容路径）。
    /// 新路径不会在此处向主台或从台泵送液体。deltaLogicalTimeMilli 的 1000 表示 1 个逻辑时间单位。
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
        var producedLiquids = _liquidProduction.AdvanceLogicalTime(deltaLogicalTimeMilli)
            .OrderBy(item => item.Liquid, StringComparer.Ordinal).ToArray();
        var liquidProductionReceipts = new List<LiquidProductionReceipt>();
        foreach (var (liquid, amount) in producedLiquids)
        {
            if (_shipLiquidInventory == null)
            {
                Inventory.ProduceLiquid(liquid, amount);
                liquidProductionReceipts.Add(new LiquidProductionReceipt(liquid, amount, amount, 0));
            }
            else
            {
                var receipt = _shipLiquidInventory.Produce(liquid, amount);
                liquidProductionReceipts.Add(new LiquidProductionReceipt(
                    liquid, amount, receipt.AcceptedAmount, receipt.DiscardedAmount));
            }
        }

        if (master == null || deltaLogicalTimeMilli == 0)
            return new ResourceAreaAdvanceResult(producedJars.Count, producedLiquids,
                liquidProductionReceipts, 0, new Dictionary<string, int>());

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
            liquidProductionReceipts,
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
public sealed record LiquidProductionReceipt(
    string Liquid,
    int ProducedAmount,
    int AcceptedAmount,
    int DiscardedAmount);

public sealed class ResourceAreaAdvanceResult
{
    public int ProducedJarCount { get; }
    public IReadOnlyList<(string Liquid, int Amount)> ProducedLiquids { get; }
    public IReadOnlyList<LiquidProductionReceipt> LiquidProductionReceipts { get; }
    public int FlowedJarCount { get; }
    public IReadOnlyDictionary<string, int> FlowedLiquids { get; }

    internal ResourceAreaAdvanceResult(
        int producedJarCount,
        IReadOnlyList<(string Liquid, int Amount)> producedLiquids,
        IReadOnlyList<LiquidProductionReceipt> liquidProductionReceipts,
        int flowedJarCount,
        IReadOnlyDictionary<string, int> flowedLiquids)
    {
        ProducedJarCount = producedJarCount;
        ProducedLiquids = producedLiquids;
        LiquidProductionReceipts = Array.AsReadOnly(liquidProductionReceipts.ToArray());
        FlowedJarCount = flowedJarCount;
        FlowedLiquids = flowedLiquids;
    }
}


/// <summary>
/// 资源区运行时快照：生产器累加器、液体产能、中央仓库和仓库→主台吞吐余数。
/// 不包含对象池/强度修饰器实例；它们必须由相同世界配置重新装配。
/// </summary>
public sealed class ResourceAreaRuntimeStateSnapshot
{
    public ResourceAreaRuntimeStateSnapshot(
        long jarFlowRateMilli,
        long liquidFlowRateMilli,
        long jarFlowAccumulator,
        long liquidFlowAccumulator,
        ProducerModuleRuntimeStateSnapshot producers,
        LiquidProductionRuntimeStateSnapshot liquidProduction,
        WarehouseRuntimeStateSnapshot warehouse)
    {
        JarFlowRateMilli = jarFlowRateMilli;
        LiquidFlowRateMilli = liquidFlowRateMilli;
        JarFlowAccumulator = jarFlowAccumulator;
        LiquidFlowAccumulator = liquidFlowAccumulator;
        Producers = producers;
        LiquidProduction = liquidProduction;
        Warehouse = warehouse;
    }

    public long JarFlowRateMilli { get; }
    public long LiquidFlowRateMilli { get; }
    public long JarFlowAccumulator { get; }
    public long LiquidFlowAccumulator { get; }
    public ProducerModuleRuntimeStateSnapshot Producers { get; }
    public LiquidProductionRuntimeStateSnapshot LiquidProduction { get; }
    public WarehouseRuntimeStateSnapshot Warehouse { get; }
}

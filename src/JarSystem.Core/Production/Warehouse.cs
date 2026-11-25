using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

/// <summary>
/// **中央仓库**（原 <c>ProducerModule</c> 里那半块，2026-09 抽出）：罐子库存 + 液体库存 +
/// 丢弃计数 + 「仓库 → 工作台」的流入规则。
///
/// 为什么值得独立：仓库的规则是**政治性**的，和生产完全不是一回事 ——
/// <list type="bullet">
/// <item>**上限与丢弃**：仓库罐 200 / 液体 200，溢出**直接丢弃并计数**（丢弃计数会进对外契约）；</item>
/// <item>**流入偏好**：优先把"主台基础形状"的罐子给主台，减少与主台无关形状的滞留；</item>
/// <item>**容量感知的部分填充**：主台被禁/已满时，液体全额留在仓库（不做部分接收之外的补偿）。</item>
/// </list>
/// 语义与抽取前**逐字一致**（等价性由 5000 用例 + 契约快照 + 两条新门守护）。
/// </summary>
public sealed class Warehouse
{
    private readonly List<Jar> _jars = new();
    private readonly JarPool? _pool;
    private readonly Vocabulary _vocabulary;
    private readonly Dictionary<string, int> _liquidWarehouse = new();
    private readonly Dictionary<string, int> _discardedLiquids = new();

    // 真只读包装（2026-09-16 全量只读化）：只给接口类型是可以被 `((List<Jar>)w.Jars).Clear()` 绕过的。
    private readonly IReadOnlyList<Jar> _jarsView;
    private readonly IReadOnlyDictionary<string, int> _liquidWarehouseView;
    private readonly IReadOnlyDictionary<string, int> _discardedLiquidsView;

    /// <summary>仓库中的罐子（只读视图；增删只能经 <see cref="StoreJars"/> / <see cref="FlowToMaster"/>）。</summary>
    public IReadOnlyList<Jar> Jars => _jarsView;

    /// <summary>仓库液体库存（只读视图；变更只能经 <see cref="ProduceLiquid"/> / <see cref="FlowToMaster"/>）。</summary>
    public IReadOnlyDictionary<string, int> LiquidWarehouse => _liquidWarehouseView;

    /// <summary>因仓库上限而丢弃的液体计数（只读视图）。</summary>
    public IReadOnlyDictionary<string, int> DiscardedLiquids => _discardedLiquidsView;

    /// <summary>仓库中的罐子数量。</summary>
    public int JarCount => _jars.Count;

    /// <summary>因仓库/工作台容量上限而丢弃的罐子计数。</summary>
    public int DiscardedJars { get; private set; }

    /// <param name="vocabulary">词表（S2）：液体账目按它的名字与**顺序**建键 —— 契约快照里 `liquids` 的键序依赖它。</param>
    public Warehouse(Vocabulary? vocabulary = null, JarPool? pool = null)
    {
        _vocabulary = vocabulary ?? Vocabulary.Default;
        _pool = pool;
        // 只读包装最先建立（活视图）——构造期间若有代码读公开视图也不会拿到 null
        _jarsView = _jars.AsReadOnly();
        _liquidWarehouseView = new ReadOnlyDictionary<string, int>(_liquidWarehouse);
        _discardedLiquidsView = new ReadOnlyDictionary<string, int>(_discardedLiquids);

        // 逐项赋值而不是集合初始化器：保证键的插入顺序 = 词表顺序
        foreach (var liquid in _vocabulary.Liquids)
        {
            _liquidWarehouse[liquid] = 0;
            _discardedLiquids[liquid] = 0;
        }
    }

    /// <summary>捕获中央仓库中的罐子、液体和丢弃计数。</summary>
    public WarehouseRuntimeStateSnapshot CaptureRuntimeState() =>
        new(_jars, _liquidWarehouse, _discardedLiquids, DiscardedJars);

    /// <summary>验证仓库快照的词表、容量和罐子身份；不修改当前仓库。</summary>
    public void ValidateRuntimeStateSnapshot(WarehouseRuntimeStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Jars.Count > Constants.WAREHOUSE_MAX_JARS ||
            snapshot.DiscardedJars < 0 ||
            !_liquidWarehouse.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.LiquidWarehouse.Keys) ||
            !_discardedLiquids.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.DiscardedLiquids.Keys))
            throw new InvalidOperationException("中央仓库快照的容量或液体词表与当前配置不一致。");

        foreach (var item in snapshot.LiquidWarehouse)
            if (item.Value < 0 || item.Value > Constants.WAREHOUSE_MAX_LIQUIDS)
                throw new DomainValidationException("中央仓库快照包含越界液体库存：" + item.Key);
        foreach (var item in snapshot.DiscardedLiquids)
            if (item.Value < 0)
                throw new DomainValidationException("中央仓库快照包含负数液体丢弃计数：" + item.Key);

        var ids = new HashSet<int>();
        foreach (var jar in snapshot.Jars)
        {
            if (jar == null || jar.IsRecycled || !jar.IsValidShapeIn(jar.Shape) ||
                !_vocabulary.Shapes.Contains(jar.Shape, StringComparer.Ordinal) ||
                (jar.Liquid != null && !_vocabulary.Liquids.Contains(jar.Liquid, StringComparer.Ordinal)))
                throw new DomainValidationException("中央仓库快照包含无效罐子。");
            if (jar.Id is > 0 && !ids.Add(jar.Id.Value))
                throw new DomainValidationException("中央仓库快照包含重复罐子 ID。");
        }
    }

    /// <summary>恢复中央仓库运行状态；罐子使用深拷贝，不复用快照中的可变实体。</summary>
    public void RestoreRuntimeState(WarehouseRuntimeStateSnapshot snapshot)
    {
        ValidateRuntimeStateSnapshot(snapshot);
        var jars = snapshot.Jars.Select(jar => jar.DeepClone()).ToArray();
        _jars.Clear();
        _jars.AddRange(jars);
        foreach (var key in _liquidWarehouse.Keys.ToArray())
            _liquidWarehouse[key] = snapshot.LiquidWarehouse[key];
        foreach (var key in _discardedLiquids.Keys.ToArray())
            _discardedLiquids[key] = snapshot.DiscardedLiquids[key];
        DiscardedJars = snapshot.DiscardedJars;
    }

    /// <summary>收货：超过仓库上限的部分丢弃并计数（避免长跑无限膨胀）。</summary>
    public void StoreJars(IEnumerable<Jar> jars)
    {
        foreach (var jar in jars)
        {
            if (_jars.Count < Constants.WAREHOUSE_MAX_JARS)
            {
                _jars.Add(jar);
            }
            else
            {
                // 仓库溢出**直接丢弃**（`规则.txt`：溢出丢弃并计数，不回流）—— 丢弃即"已彻底离开世界"，
                // 这是可以安全归还对象池的少数位置之一。
                DiscardedJars++;
                _pool?.Return(jar);
            }
        }
    }

    /// <summary>产液：超出仓库上限的部分丢弃并计数（容量感知）。</summary>
    public void ProduceLiquid(string liquid, int amount)
    {
        var current = _liquidWarehouse.GetValueOrDefault(liquid, 0);
        var accepted = Math.Min(amount, Math.Max(0, Constants.WAREHOUSE_MAX_LIQUIDS - current));
        _liquidWarehouse[liquid] = current + accepted;
        _discardedLiquids[liquid] = _discardedLiquids.GetValueOrDefault(liquid, 0) + (amount - accepted);
    }

    /// <summary>外部丢弃计数累加（例如工作台缩小容量时丢掉的资源）。</summary>
    public void CountDiscardedJars(int count) => DiscardedJars += count;

    /// <summary>外部丢弃计数累加（液体）。</summary>
    public void CountDiscardedLiquids(string liquid, int count) =>
        _discardedLiquids[liquid] = _discardedLiquids.GetValueOrDefault(liquid, 0) + count;

    /// <summary>某液体的库存量。</summary>
    public int LiquidCount(string liquid) => _liquidWarehouse.GetValueOrDefault(liquid, 0);

    /// <summary>
    /// Exact single-liquid withdrawal for a synchronous port transaction. Unlike ProduceLiquid,
    /// this never accepts a partial amount and never records overflow as discarded.
    /// </summary>
    internal bool TryWithdrawLiquidExact(string liquid, int amount)
    {
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: false);
        if (!_liquidWarehouse.ContainsKey(liquid) || LiquidCount(liquid) < amount)
            return false;
        _liquidWarehouse[liquid] -= amount;
        return true;
    }

    /// <summary>Restore one exact pre-transaction Warehouse balance during compensation.</summary>
    internal void RestoreLiquidBalance(string liquid, int amount)
    {
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);
        if (!_liquidWarehouse.ContainsKey(liquid))
            throw new InvalidOperationException("Warehouse rollback references an unknown liquid: " + liquid);
        if (amount > Constants.WAREHOUSE_MAX_LIQUIDS)
            throw new InvalidOperationException("Warehouse rollback balance exceeds its per-liquid capacity.");
        _liquidWarehouse[liquid] = amount;
    }

    /// <summary>
    /// 仓库 → 主台流入：罐子优先给"主台基础形状"（带主台容量检查），液体按主台的**容量**部分填充
    /// （被禁液体不会流入，仍完整留在中央仓库）。
    /// 返回本次实际流入的罐子与液体（与抽取前逐字一致）。
    /// </summary>
    public (List<Jar> Jars, Dictionary<string, int> Liquids) FlowToMaster(
        Workshop master, int jarCount = 3, int liquidAmount = 2)
    {
        var transferredJars = new List<Jar>();

        // 1. 罐子转移（带容量检查）
        int taken = 0;
        while (taken < jarCount && _jars.Count > 0)
        {
            int pick = _jars.FindIndex(j => master.IsBaseMarked(j.Shape));
            if (pick < 0) pick = 0;
            var jar = _jars[pick];
            _jars.RemoveAt(pick);
            if (master.AddJar(jar))
            {
                transferredJars.Add(jar);
                taken++;
            }
            else
            {
                _jars.Insert(pick, jar);   // 主台满了：返还仓库并停止
                break;
            }
        }

        // 2. 游离液体转移（工作台禁液或容量不足时拒收；未接收部分留在仓库）
        var transferredLiquids = new Dictionary<string, int>();
        foreach (var kv in _liquidWarehouse.ToList())
        {
            var liquid = kv.Key;
            var amount = Math.Min(liquidAmount, kv.Value);
            if (amount <= 0) continue;

            var accepted = master.TryReceiveLiquid(liquid, amount);
            if (accepted <= 0) continue;

            _liquidWarehouse[liquid] = kv.Value - accepted;
            transferredLiquids[liquid] = accepted;
        }

        return (transferredJars, transferredLiquids);
    }
}


/// <summary>中央仓库运行时快照；罐子与账目均为脱离式副本。</summary>
public sealed class WarehouseRuntimeStateSnapshot
{
    private readonly Jar[] _jars;

    public WarehouseRuntimeStateSnapshot(
        IEnumerable<Jar> jars,
        IReadOnlyDictionary<string, int> liquidWarehouse,
        IReadOnlyDictionary<string, int> discardedLiquids,
        int discardedJars)
    {
        _jars = jars.Select(jar => jar.DeepClone()).ToArray();
        LiquidWarehouse = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(liquidWarehouse, StringComparer.Ordinal));
        DiscardedLiquids = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(discardedLiquids, StringComparer.Ordinal));
        DiscardedJars = discardedJars;
    }

    /// <summary>每次返回独立罐子副本，防止调用方通过可变 Jar 修改检查点内部状态。</summary>
    public IReadOnlyList<Jar> Jars =>
        Array.AsReadOnly(_jars.Select(jar => jar.DeepClone()).ToArray());
    public IReadOnlyDictionary<string, int> LiquidWarehouse { get; }
    public IReadOnlyDictionary<string, int> DiscardedLiquids { get; }
    public int DiscardedJars { get; }
}

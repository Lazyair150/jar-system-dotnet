using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Process;
using JarSystem.Core.Production;

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
///
/// **2026-11（resource-provenance）**：产出与流入都要在 <see cref="JarSystem.ResourceLedger"/> 上
/// 各留一条事实（`production` / `flow` / `discard`）。账本是只读投影 —— 记录不改变世界，
/// 删掉这些 Record 调用世界状态与回放字节逐项不变（<c>ResourceProvenanceTests</c> 守着"每条都指得到原因"）。
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
        var warehouse = _system.ProducerModule.Warehouse;
        var discardedLiquidsBefore = new Dictionary<string, int>(warehouse.DiscardedLiquids);

        _system.ProducerModule.Tick(1);
        RecordJarFacts(ResourceCauses.Production, warehouse.LastReceivedJarFacts, from: "生产者", to: "仓库");

        // S0（数据驱动）：按**词表**产出，不再写死 酒/蜜 两个名字。
        // 液体的产能现在是一个概念体（Production.LiquidProduction）：它自己攒累加器，
        // 因此 0.5 份/tick = 两 tick 产一份，而且小数不靠浮点（千分之一定点）。
        // 顺序 = 词表顺序 ⇒ 与重构前逐次相同（默认速率 1.0 时每 tick 恰好 1 份）。
        foreach (var (liquid, amount) in _system.LiquidProduction.Tick())
        {
            _system.ProducerModule.Warehouse.ProduceLiquid(liquid, amount);
            // 记的是"产了多少"，产能溢出另有一条 discard 事实 —— 两条合起来才是净变化。
            RecordLiquid(ResourceCauses.Production, liquid, amount, from: "液体产能", to: "仓库");
        }

        RecordDiscards(discardedLiquidsBefore);
    }

    /// <summary>
    /// 向当前主台流入一次。**每次宿主 <c>Tick(count)</c> 调用一次**（不是每个 tick 一次）——
    /// 这是既有语义（"每次流入"的批量口径），P2 刻意保持不变，改它需要单独裁决。
    /// </summary>
    internal void FlushFlow()
    {
        // 主台切换期间，仓库入口关闭：目标工作台尚未就位，资源不能提前流进旧主台。
        // 切换过程在 Processes 阶段推进，因此每个跨 tick 的调用都要检查，而不只是在 BeginSwitch 时检查。
        if (_system.Processes.ActiveProcesses.Any(
                p => p.Kind == ProcessKinds.WorkshopMigration))
            return;

        if (!_system.CurrentMaster.HasValue) return;
        var master = _system.Workshops[_system.CurrentMaster.Value];
        if (master == null) return;
        // P4d：流入参数也可被强度修饰器折算（整数千分比；无匹配 ⇒ 恒等）
        // 目标名：`flow:jars`（每次流入的罐子数）、`flow:liquid`（每次流入的液体量）。
        var jars = _system.Modifiers.ApplyIntensity("flow:jars", _system.FlowSettings.JarCount);
        var liquid = _system.Modifiers.ApplyIntensity("flow:liquid", _system.FlowSettings.LiquidAmount);
        var moved = _system.ProducerModule.Warehouse.FlowToMaster(master, jars, liquid);

        // 流入是**位置变化**，不是"世界产出了东西"：账本上它与 production 分开记，
        // 读者因此能区分"仓库多了三只罐"和"主台多了三只罐"。
        RecordJars(ResourceCauses.Flow, moved.Jars, from: "仓库", to: master.Name);
        foreach (var (name, amount) in moved.Liquids)
            RecordLiquid(ResourceCauses.Flow, name, amount, from: "仓库", to: master.Name);
    }

    /// <summary>
    /// 设置某生产者的某形状速率。规则（生产者存在 / 形状存在 / 速率上界）统一住在
    /// <see cref="Production.ProducerModule.UpdateRate"/> —— 那里才是这份数据的归属地，
    /// 于是直接调模块的宿主也受同一条约束；本方法只做转发，不再重复一份规则。
    /// </summary>
    public void UpdateProducerRate(string name, string shape, double rate) =>
        _system.ProducerModule.UpdateRate(name, shape, rate);

    public void UpdateLiquidProductionRate(string liquid, double rate)
    {
        // 注意：走 **Core**（不写日志）—— 会话日志由门面的 UpdateLiquidProductionRate 记一条即可，
        // 否则同一次宿主意图会被记两条（回放时日志越滚越长）。
        _system.SetLiquidProductionRateCore(liquid, rate);
    }

    // ============================================================
    // 账本记录（纯投影：不改世界）
    // ============================================================

    /// <summary>逐只记录 Jar 实体；Shape 仅作展示，ResourceIdentity 必须是 JarId。</summary>
    private void RecordJars(string cause, IReadOnlyList<Jar> jars, string from, string to)
    {
        if (jars.Count == 0) return;
        var tick = _system.Processes.Clock.Tick;
        foreach (var jar in jars)
        {
            if (jar.Id is not > 0)
                throw new DomainValidationException("账本 Jar 事实缺少 canonical JarId。");
            _system.ResourceLedger.Record(tick, cause, ResourceKinds.Jar, jar.Shape, 1, from, to,
                resourceIdentity: jar.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void RecordJarFacts(string cause, IReadOnlyList<WarehouseJarFact> jars, string from, string to)
    {
        var tick = _system.Processes.Clock.Tick;
        foreach (var jar in jars)
            _system.ResourceLedger.Record(tick, cause, ResourceKinds.Jar, jar.Shape, 1, from, to,
                resourceIdentity: jar.JarId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void RecordLiquid(string cause, string liquid, int amount, string from, string to)
    {
        if (amount <= 0) return;
        _system.ResourceLedger.Record(
            _system.Processes.Clock.Tick, cause, ResourceKinds.Liquid, liquid, amount, from, to,
            resourceIdentity: liquid);
    }

    /// <summary>
    /// 溢出丢弃：仓库满了放不下的部分。**必须单独记**，否则读者会以为"产了却没到"是丢事件。
    /// 逐液体比较前后计数，只为真的有丢弃的那几种液体记一条。
    /// </summary>
    private void RecordDiscards(IReadOnlyDictionary<string, int> discardedLiquidsBefore)
    {
        var warehouse = _system.ProducerModule.Warehouse;
        var tick = _system.Processes.Clock.Tick;

        foreach (var jar in warehouse.LastDiscardedJarFacts)
            _system.ResourceLedger.Record(tick, ResourceCauses.Discard, ResourceKinds.Jar, jar.Shape, 1,
                from: "仓库", reason: "仓库罐位已满",
                resourceIdentity: jar.JarId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var (liquid, total) in warehouse.DiscardedLiquids)
        {
            var before = discardedLiquidsBefore.TryGetValue(liquid, out var b) ? b : 0;
            var delta = total - before;
            if (delta <= 0) continue;
            _system.ResourceLedger.Record(tick, ResourceCauses.Discard, ResourceKinds.Liquid, liquid, delta,
                from: "仓库", reason: "仓库该液体已满", resourceIdentity: liquid);
        }
    }
}

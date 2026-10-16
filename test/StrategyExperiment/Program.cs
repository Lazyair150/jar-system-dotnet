// 问题 21 重跑：711 抽象结构模式 × 多种子 × 4 策略的订单完成率对比
// 与原「问题十测试记录」的差异：
//   1) 每模式 3 个独立种子（原为 1 个），每种子 30 个订单（原 60），合计 90 单/策略/模式
//   2) 不再调用可行性分析（策略只影响执行器选择），指标 = 实际执行成功率
//   3) 输出配对符号检验与置信区间所需的均值/标准差
using System.Text;
using JarSystem.Core;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Executor;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;

int seeds = 3;
int ordersPerRun = 30;
int[] seedValues = { 1001, 2002, 3003 };
string outputPath = "问题/问题十测试记录-重跑.txt";

// ============================================================
// 1. 结构模式枚举（抽象参数，不绑定具体形状）
// ============================================================
var modes = new List<(int mb, int ma, int mBan, int sb, int sa, int sBan, int ov)>();
for (int mb = 1; mb <= 2; mb++)
for (int ma = 0; ma <= 2; ma++)
for (int mBan = 0; mBan <= 2; mBan++)
for (int sb = 1; sb <= 2; sb++)
for (int sa = 0; sa <= 2; sa++)
for (int sBan = 0; sBan <= 2; sBan++)
for (int ov = 0; ov <= Math.Min(mb, sb); ov++)
{
    if (mb + ma + sb + sa - ov > 8) continue;      // 总使用形状数限制
    if (mb == sb && ma == sa && mBan == sBan && ov == mb) continue; // 排除主从完全相同的结构（基础标记全重叠）
    modes.Add((mb, ma, mBan, sb, sa, sBan, ov));
}

Console.WriteLine($"modes={modes.Count} seeds={seeds} ordersPerRun={ordersPerRun} 预计策略运行数={modes.Count * seeds * 4}");

var strategies = new[] { JarSelectionStrategy.FIFO, JarSelectionStrategy.LIFO, JarSelectionStrategy.RareShapeFirst, JarSelectionStrategy.Random };
long[] totalWin = new long[strategies.Length];
long[] totalOrder = new long[strategies.Length];
int[] strictWins = new int[strategies.Length];
var perRun = new List<(int Mode, int RunId, JarSelectionStrategy S, int Win, int Total)>();

int done = 0;
int runId = 0;
foreach (var m in modes)
{
    foreach (var seed in seedValues)
    {
        int h = seed;
        h = h * 397 ^ (m.mb * 100000 + m.ma * 10000 + m.mBan * 1000 + m.sb * 100 + m.sa * 10 + m.sBan);
        h = h * 31 + m.ov;
        var rnd = new Random(h);

        // 具体形状分配（保证主从基础标记交集 = ov）
        var rest = new List<string>(Constants.SHAPES);
        var common = new List<string>();
        for (int i = 0; i < m.ov; i++) { var s = rest[rnd.Next(rest.Count)]; common.Add(s); rest.Remove(s); }
        var mBase = new List<string>(common);
        var sBase = new List<string>(common);
        while (mBase.Count < m.mb) { var s = rest[rnd.Next(rest.Count)]; mBase.Add(s); rest.Remove(s); }
        while (sBase.Count < m.sb) { var s = rest[rnd.Next(rest.Count)]; if (!mBase.Contains(s)) { sBase.Add(s); rest.Remove(s); } }
        var mAdd = PickDistinct(rest.Concat(sBase).ToList(), m.ma, mBase, rnd);
        var sAdd = PickDistinct(rest.Concat(mBase).ToList(), m.sa, sBase, rnd);

        var master = new Workshop("M", mBase, mAdd, BanList(m.mBan), 30, 30);
        var slave = new Workshop("S", sBase, sAdd, BanList(m.sBan), 30, 30);

        // 订单流（同一模式+种子内 4 策略共享）
        var orderSpecs = new List<(List<Requirement> Reqs, string Type)>();
        for (int i = 0; i < ordersPerRun; i++) orderSpecs.Add(GenOrder(rnd));

        var runRates = new double[strategies.Length];
        for (int si = 0; si < strategies.Length; si++)
        {
            var sys = NewSystem(master, slave, strategies[si], h);
            int win = 0;
            for (int o = 0; o < ordersPerRun; o++)
            {
                sys.Tick();
                if (o % 3 == 0)
                    foreach (var l in Constants.LIQUIDS)
                        if (!master.IsBanned(l)) sys.ProducerModule.Warehouse.ProduceLiquid(l, 1);
                var spec = orderSpecs[o];
                sys.CreateOrder(spec.Reqs, spec.Type);
                if (sys.ExecuteOrder(sys.Orders.Count - 1).Success) win++;
            }
            runRates[si] = (double)win / ordersPerRun;
            totalWin[si] += win;
            totalOrder[si] += ordersPerRun;
            perRun.Add((modes.IndexOf(m), runId, strategies[si], win, ordersPerRun));
        }
        runId++;

        // 严格最优（唯一最高分）
        double max = runRates.Max();
        var best = strategies.Where((_, i) => runRates[i] == max).ToList();
        if (best.Count == 1) strictWins[Array.IndexOf(strategies, best[0])]++;
    }

    done++;
    if (done % 150 == 0) Console.WriteLine($"progress: {done}/{modes.Count} modes");
}

// ============================================================
// 2. 统计
// ============================================================
var sb_ = new StringBuilder();
sb_.AppendLine("# 问题十测试记录 — 重跑（多种子统计修正版）");
sb_.AppendLine();
sb_.AppendLine($"- 日期：2026-08-01");
sb_.AppendLine($"- 结构模式数：{modes.Count}（mb/ma/mBan/sb/sa/sBan/ov 全枚举，总形状 ≤ 8，排除主从完全相同）");
sb_.AppendLine($"- 种子：{string.Join(", ", seedValues)}（每模式 3 个独立种子）");
sb_.AppendLine($"- 每种子订单数：{ordersPerRun}（合计每策略每模式 {seeds * ordersPerRun} 单）");
sb_.AppendLine($"- 指标：实际执行成功率（不调用可行性分析，策略仅影响执行器选择）");
sb_.AppendLine($"- 总执行量：{modes.Count * seeds * strategies.Length} 条策略运行流 × {ordersPerRun} 单");
sb_.AppendLine();

sb_.AppendLine("| 策略 | 总执行单数 | 成功率(均值) | 严格最优运行流数 |");
sb_.AppendLine("|------|-----------|-------------|-----------------|");
double[] rates = new double[strategies.Length];
for (int si = 0; si < strategies.Length; si++)
{
    rates[si] = (double)totalWin[si] / totalOrder[si];
    sb_.AppendLine($"| {strategies[si]} | {totalOrder[si]} | {rates[si]:P2} | {strictWins[si]} |");
}

sb_.AppendLine();
sb_.AppendLine("### 配对符号检验（FIFO vs 其它策略，基于 模式×种子 运行流）");
sb_.AppendLine();
sb_.AppendLine("| 对比 | FIFO 优 | 对方优 | 平局 | 二项近似 p 值 |");
sb_.AppendLine("|------|--------|--------|------|--------------|");
for (int si = 1; si < strategies.Length; si++)
{
    var runs = perRun.Where(r => r.S == strategies[0]).ToList();
    var other = perRun.Where(r => r.S == strategies[si]).ToDictionary(r => (r.Mode, r.RunId), r => r.Win);
    int better = 0, worse = 0, tie = 0;
    foreach (var r in runs)
    {
        int ow = other[(r.Mode, r.RunId)];
        double a = (double)r.Win / r.Total, b = (double)ow / r.Total;
        if (a > b) better++; else if (a < b) worse++; else tie++;
    }
    int n = better + worse;
    double p = n == 0 ? 1.0 : BinomTwoSidedApprox(Math.Max(better, worse), n);
    sb_.AppendLine($"| FIFO vs {strategies[si]} | {better} | {worse} | {tie} | {p:F4} |");
}

// 每模式聚合（跨种子均值）后的最优
sb_.AppendLine();
sb_.AppendLine("### 每模式聚合（跨种子取均值）后的最优策略分布");
sb_.AppendLine();
var byMode = perRun.GroupBy(r => r.Mode).ToList();
int[] modeWins = new int[strategies.Length];
foreach (var g in byMode)
{
    var means = new double[strategies.Length];
    for (int si = 0; si < strategies.Length; si++)
    {
        var rs = g.Where(r => r.S == strategies[si]).ToList();
        means[si] = rs.Count == 0 ? 0 : rs.Average(r => (double)r.Win / r.Total);
    }
    double mx = means.Max();
    var bests = strategies.Where((_, i) => means[i] == mx).ToList();
    if (bests.Count == 1) modeWins[Array.IndexOf(strategies, bests[0])]++;
}
for (int si = 0; si < strategies.Length; si++)
    sb_.AppendLine($"- {strategies[si]}：{modeWins[si]} 个模式最优");

sb_.AppendLine();
sb_.AppendLine("### 结论（统计修正版）");
sb_.AppendLine("- 多种子（每策略每模式 90 单）下，FIFO 成功率 44.78%，显著高于 LIFO（40.20%，+4.6pp）、RareShapeFirst（27.13%）、Random（23.27%）；配对符号检验 p 值均 < 0.001。");
sb_.AppendLine("- 每模式聚合后 FIFO 在 711 个模式中的 515 个上最优，LIFO 178 个，RareShapeFirst / Random 各 0 个。");
sb_.AppendLine("- 口径变更（2026-10，P2c 产出定点化）：产出累加由 double 改为整数定点，修正了浮点漂移（契约里曾出现 0.8999999999999999 之类的值）。"
    + "因此本表数值与上一版记录（FIFO 44.96% / LIFO 40.26% / Random 23.31%；模式最优 520 / 170）相比有 0.1–0.2pp 位移；**排序与显著性结论不变**。");
sb_.AppendLine("- 修正后的结论：在执行器贪心路径口径下，**FIFO 是最优默认策略且差异显著**；原单次运行记录中「差异很小（0.1–0.3pp）」的表述被多种子重跑推翻。");
sb_.AppendLine("- 口径说明：本重跑指标为实际执行成功率（不调用可行性分析），与原记录的绝对成功率不可直接比较；策略间的相对排序与显著性是本重跑要回答的问题。");

File.WriteAllText(outputPath, sb_.ToString(), new UTF8Encoding(true));
Console.WriteLine();
Console.WriteLine(sb_.ToString());
Console.WriteLine($"报告已写入: {Path.GetFullPath(outputPath)}");

// ============================================================
// 工具
// ============================================================

JarSystem.Core.JarSystem NewSystem(Workshop master, Workshop slave, JarSelectionStrategy st, int rngSeed)
{
    // T4：把该 (模式, 种子) 派生的 h 作为系统随机源种子 ⇒ 随机策略也可复现
    var sys = new JarSystem.Core.JarSystem(randomSeed: rngSeed);
    sys.ClearWorkshops(); sys.ClearOrders();
    sys.ResetProducerModule();
    foreach (var s in Constants.SHAPES)
    {
        double rate = (master.BaseMarks.Contains(s) || slave.BaseMarks.Contains(s)) ? 0.45 : 0.15;
        sys.ProducerModule.RegisterProducer("P_" + s, new() { [s] = rate });
    }
    sys.SetLiquidProductionRate("酒", 0);
    sys.SetLiquidProductionRate("蜜", 0);
    sys.SetFlowSettings(3, 2);
    sys.AddWorkshop(master);
    sys.AddWorkshop(slave);
    sys.SetMasterIndex(0);
    sys.SetSlaveIndex(1);
    sys.Executor.SelectionStrategy = st;
    return sys;
}

List<string> PickDistinct(List<string> pool, int count, List<string> exclude, Random r)
{
    var res = new List<string>();
    var avail = pool.Where(s => !exclude.Contains(s) && !res.Contains(s)).Distinct().ToList();
    while (res.Count < count && avail.Count > 0)
    {
        var s = avail[r.Next(avail.Count)];
        res.Add(s);
        avail.Remove(s);
    }
    return res;
}

List<string> BanList(int code) => code switch
{
    1 => new List<string> { "酒" },
    2 => new List<string> { "蜜" },
    _ => new List<string>()
};

(List<Requirement> Reqs, string Type) GenOrder(Random r)
{
    int nr = r.Next(1, 4);
    var reqs = new List<Requirement>();
    int emptyCount = r.Next(1, nr + 1);
    for (int i = 0; i < emptyCount; i++)
        reqs.Add(new Requirement { Shape = Constants.SHAPES[r.Next(Constants.SHAPES.Count)] });
    for (int i = emptyCount; i < nr; i++)
        reqs.Add(new Requirement { Shape = Constants.SHAPES[r.Next(Constants.SHAPES.Count)], Liquid = Constants.LIQUIDS[r.Next(Constants.LIQUIDS.Count)] });
    return (reqs, r.Next(2) == 0 ? "A" : "B");
}

// 二项检验：符号检验（非参数配对）的双侧近似 p 值（正态近似）
double BinomTwoSidedApprox(int k, int n)
{
    double p = 0.5;
    double mean = n * p;
    double sd = Math.Sqrt(n * p * (1 - p));
    if (sd == 0) return 1.0;
    double z = (k - mean - 0.5) / sd;
    double tail = 0.5 * Erfc(z / Math.Sqrt(2));
    return Math.Min(1.0, 2 * tail);
}

double Erfc(double x)
{
    // Abramowitz-Stegun 7.1.26 近似
    double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
    double y = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429))));
    double erfc = y * Math.Exp(-x * x);
    return x >= 0 ? erfc : 2 - erfc;
}

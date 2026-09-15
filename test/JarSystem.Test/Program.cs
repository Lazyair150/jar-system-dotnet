using JarSystem.Core;
using JarSystem.Core.Commands;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

// ============================================================
// 契约快照模式（重构专用，不影响默认测试）：
//   dotnet run --project test\JarSystem.Test -c Release -- --dump-contract
// 用固定场景导出 GetState() 的全部键路径与值到 重构报告/_contract/state_keys.txt，
// 供重构前后 diff（时间戳类字段记为 <volatile>）。
// ============================================================
if (args.Length > 0 && args[0] == "--dump-contract")
{
    DumpContract();
    return;
}

// 契约 Schema 导出模式（模块边界产物）：
//   dotnet run --project test\JarSystem.Test -c Release -- --dump-schema
if (args.Length > 0 && args[0] == "--dump-schema")
{
    DumpSchema();
    return;
}

var rand = new Random(42);
int tests = 2000, bugs = 0;
var bugReports = new List<string>();

for (int t = 0; t < tests; t++)
{
    try
    {
        var (master, slave, orderSpec) = Gen(rand);
        var sys = NewSystem(master, slave, new Order(orderSpec.Reqs, orderSpec.Type));

        int jarsBefore = master.Jars.Count + slave.Jars.Count;

        var fr = sys.CheckFeasibility(0);
        bool feasible = fr?.Feasible == true;
        var exec = sys.ExecuteOrder(0);

        // 双向一致性：可行必须能执行成功；不可行执行成功同样是 bug
        if (feasible && !exec.Success)
        {
            bugReports.Add($"BUG t={t}: feasible, exec failed ({exec.Reason}). Order={DescribeOrder(orderSpec)} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())} Method={fr?.CheckMethod}");
            bugs++;
        }
        else if (!feasible && exec.Success)
        {
            bugReports.Add($"BUG-REVERSE t={t}: not feasible, exec succeeded. Order={DescribeOrder(orderSpec)} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
            bugs++;
        }

        // 罐子守恒：成功交付 reqs.Count 个罐子，失败则全部归还
        int jarsAfter = master.Jars.Count + slave.Jars.Count;
        int expectedJarsAfter = jarsBefore - (exec.Success ? orderSpec.Reqs.Count : 0);
        if (jarsAfter != expectedJarsAfter)
        {
            bugReports.Add($"CONSERVATION t={t}: jars {jarsBefore} -> {jarsAfter}, expected {expectedJarsAfter} (success={exec.Success}, reqs={orderSpec.Reqs.Count}). M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
            bugs++;
        }

        // 液体不允许出现负库存
        bool liquidsValid = master.LiquidStorage.Values.All(v => v >= 0) && slave.LiquidStorage.Values.All(v => v >= 0);
        if (!liquidsValid)
        {
            bugReports.Add($"NEGATIVE-LIQUID t={t}: M={string.Join(",", master.LiquidStorage.Select(kv => kv.Key + ":" + kv.Value))} S={string.Join(",", slave.LiquidStorage.Select(kv => kv.Key + ":" + kv.Value))}");
            bugs++;
        }
    }
    catch (Exception ex) { bugReports.Add($"CRASH t={t}: {ex.Message}"); bugs++; }
}

// 容量压力：小罐位/小液位 + 从台资源，专打选择性转移预算、转移清单与容量截断路径
int capTests = 3000, capBugs = 0;
var capRand = new Random(2026);
for (int t = 0; t < capTests; t++)
{
    try
    {
        var (master, slave, orderSpec) = GenCap(capRand);
        var sys = NewSystem(master, slave, new Order(orderSpec.Reqs, orderSpec.Type));
        int jarsBefore = master.Jars.Count + slave.Jars.Count;
        var fr = sys.CheckFeasibility(0);
        bool feasible = fr?.Feasible == true;
        var exec = sys.ExecuteOrder(0);
        int jarsAfter = master.Jars.Count + slave.Jars.Count;
        int expected = jarsBefore - (exec.Success ? orderSpec.Reqs.Count : 0);

        if (feasible && !exec.Success)
        {
            bugReports.Add($"CAP-BUG t={t}: feasible, exec failed ({exec.Reason}). Order={DescribeOrder(orderSpec)} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())} Method={fr?.CheckMethod} Transfer={fr?.TransferJarIds?.Count}");
            capBugs++;
        }
        else if (!feasible && exec.Success)
        {
            bugReports.Add($"CAP-REVERSE t={t}: not feasible, exec ok. Order={DescribeOrder(orderSpec)} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
            capBugs++;
        }
        if (jarsAfter != expected)
        {
            bugReports.Add($"CAP-CONSERVATION t={t}: jars {jarsBefore} -> {jarsAfter}, expected {expected} (success={exec.Success}, reqs={orderSpec.Reqs.Count})");
            capBugs++;
        }
        if (master.LiquidStorage.Values.Any(v => v < 0) || slave.LiquidStorage.Values.Any(v => v < 0))
        {
            bugReports.Add($"CAP-NEGATIVE-LIQUID t={t}: M={string.Join(",", master.LiquidStorage.Select(kv => kv.Key + ":" + kv.Value))} S={string.Join(",", slave.LiquidStorage.Select(kv => kv.Key + ":" + kv.Value))}");
            capBugs++;
        }
    }
    catch (Exception ex) { bugReports.Add($"CAP-CRASH t={t}: {ex.Message}"); capBugs++; }
}

// 确定性回归：对应「一一解决方案.txt」第 1、2、3 条
int detCount = 0;

Run("S1-满罐改形路径可执行(问题1)", () =>
{
    // 主台基础[高罐]，只有一个满罐(方罐,酒)，无游离液体：空罐(高罐)只能靠满罐改形
    var master = new Workshop("M", new() { "高罐" }, new(), new(), 20, 30);
    var slave = new Workshop("S", new() { "圆罐" }, new(), new(), 20, 30);
    master.AddJar(new Jar("方罐", "酒"));
    var sys = NewSystem(master, slave, new Order(new() { new Requirement { Shape = "高罐" } }, "A"));
    var fr = sys.CheckFeasibility(0);
    var exec = sys.ExecuteOrder(0);
    bool ok = fr?.Feasible == true && exec.Success
        && master.Jars.Count == 0 && slave.Jars.Count == 0;
    return (ok, $"feasible={fr?.Feasible} exec={exec.Success} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())} reason={exec.Reason}");
});

Run("S2-从台自有资源+转移后索引(问题2)", () =>
{
    // 主台有罐子（转移后从台列表前插），订单仅从台可满足（slave_only）
    var master = new Workshop("M", new() { "圆罐" }, new(), new(), 20, 30);
    var slave = new Workshop("S", new() { "方罐" }, new(), new(), 20, 30);
    master.AddJar(new Jar("圆罐", "酒"));
    slave.AddJar(new Jar("方罐"));
    var sys = NewSystem(master, slave, new Order(new() { new Requirement { Shape = "方罐" } }, "B"));
    var fr = sys.CheckFeasibility(0);
    var exec = sys.ExecuteOrder(0);
    // 交付从台方罐；主台圆罐/酒交付后应回流主台
    bool ok = fr?.Feasible == true && exec.Success
        && master.Jars.Count == 1 && master.Jars[0].Shape == "圆罐" && master.Jars[0].Liquid == "酒"
        && slave.Jars.Count == 0;
    return (ok, $"feasible={fr?.Feasible} exec={exec.Success} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())} reason={exec.Reason}");
});

Run("S3-倒出回补液体账目(问题3)", () =>
{
    // 目标空罐(鼓罐)是从台附加标记：满罐(方罐,蜜)无法满罐改形为鼓罐（附加→基础），
    // 倒出方罐又不会直接满足鼓罐需求，只能靠「倒出获得蜜 → 圆罐改形为鼓罐」。
    // 无游离液体，路径必然含 POUR_OUT + RESHAPE(空)，验证液体账目与路径执行。
    var master = new Workshop("M", new() { "圆罐" }, new() { "鼓罐" }, new(), 20, 30);
    var slave = new Workshop("S", new() { "圆罐" }, new() { "鼓罐", "方罐" }, new(), 20, 30);
    master.AddJar(new Jar("圆罐"));
    master.AddJar(new Jar("方罐", "蜜"));
    var sys = NewSystem(master, slave, new Order(new() { new Requirement { Shape = "鼓罐" } }, "B"));
    var fr = sys.CheckFeasibility(0);
    var exec = sys.ExecuteOrder(0);
    // 圆罐→鼓罐被交付；方罐(已倒空)回流主台
    bool ok = fr?.Feasible == true && exec.Success
        && master.Jars.Count == 1 && master.Jars[0].Shape == "方罐" && master.Jars[0].IsEmpty()
        && slave.Jars.Count == 0;
    return (ok, $"feasible={fr?.Feasible} exec={exec.Success} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())} reason={exec.Reason} method={fr?.CheckMethod}");
});

// 工作台切换事件（阶段 4：问题 6、7、8、9）
Run("S4-旧主台仅液体也触发切换(问题6)", () =>
{
    var a = new Workshop("A", new() { "圆罐" }, new(), new(), 20, 30);
    var b = new Workshop("B", new() { "圆罐" }, new(), new(), 20, 30);
    var c = new Workshop("C", new() { "圆罐" }, new(), new(), 20, 30);
    a.AddLiquid("酒", 2); // 无罐子，只有液体
    var sys = NewSwitchSystem(a, b, c);
    sys.SetMasterIndex(0); sys.SetSlaveIndex(1);
    var res = sys.SetMaster(2);
    bool ok = (res.TryGetValue("success", out var sv) && sv is bool s1 && s1)
        && (res.TryGetValue("switched", out var swv) && swv is bool s2 && s2)
        && sys.CurrentMaster == 2 && sys.CurrentSlave == 1
        && a.LiquidStorage["酒"] == 0 && c.LiquidStorage["酒"] == 2;
    return (ok, $"res={string.Join(";", res.Select(kv => kv.Key + "=" + kv.Value))} A={DescribeWS(a.CreateSnapshot())} B={DescribeWS(b.CreateSnapshot())} C={DescribeWS(c.CreateSnapshot())}");
});

Run("S5-把当前从台设为主自动解除从台(问题9)", () =>
{
    var a = new Workshop("A", new() { "圆罐" }, new(), new(), 20, 30);
    var b = new Workshop("B", new() { "方罐" }, new(), new(), 20, 30);
    a.AddJar(new Jar("圆罐", "酒"));
    var sys = NewSwitchSystem(a, b);
    sys.SetMasterIndex(0); sys.SetSlaveIndex(1);
    var res = sys.SetMaster(1);
    bool ok = (res.TryGetValue("success", out var sv) && sv is bool s1 && s1)
        && sys.CurrentMaster == 1 && sys.CurrentSlave == null
        && b.Jars.Count == 1 && b.Jars[0].Shape == "圆罐" && b.Jars[0].Liquid == "酒"
        && a.Jars.Count == 0;
    return (ok, $"res={string.Join(";", res.Select(kv => kv.Key + "=" + kv.Value))} M={sys.CurrentMaster} S={sys.CurrentSlave} A={DescribeWS(a.CreateSnapshot())} B={DescribeWS(b.CreateSnapshot())}");
});

Run("S6-Case1零游离液体利用满罐(问题7)", () =>
{
    var a = new Workshop("A", new() { "圆罐" }, new(), new(), 20, 30);
    var b = new Workshop("B", new() { "方罐" }, new(), new(), 20, 30);
    a.AddJar(new Jar("圆罐"));       // 基础形状空罐
    a.AddJar(new Jar("方罐", "蜜"));  // 非基础形状满罐，无游离液体
    var sys = NewSwitchSystem(a, b);
    sys.SetMasterIndex(0);
    var res = sys.SetMaster(1);
    bool ok = (res.TryGetValue("success", out var sv) && sv is bool s1 && s1)
        && b.Jars.Count == 2
        && b.Jars.Any(j => j.Shape == "圆罐" && j.Liquid == "蜜")
        && b.Jars.Any(j => j.Shape == "方罐" && j.IsEmpty())
        && a.Jars.Count == 0;
    return (ok, $"A={DescribeWS(a.CreateSnapshot())} B={DescribeWS(b.CreateSnapshot())}");
});

Run("S7-Case2保留新主台可用液体(问题8)", () =>
{
    var a = new Workshop("A", new() { "圆罐" }, new() { "方罐" }, new() { "酒" }, 20, 30);
    var b = new Workshop("B", new() { "方罐" }, new(), new(), 20, 30);
    a.AddJar(new Jar("圆罐"));
    a.AddLiquid("蜜", 3);
    var sys = NewSwitchSystem(a, b);
    sys.SetMasterIndex(0);
    var res = sys.SetMaster(1);
    // 改形消耗 1 蜜（圆罐→方罐），剩余 2 蜜新主台可用，应被转移而非无条件清空
    bool ok = (res.TryGetValue("success", out var sv) && sv is bool s1 && s1)
        && b.LiquidStorage["蜜"] == 2
        && b.Jars.Count == 1 && b.Jars[0].Shape == "方罐" && b.Jars[0].IsEmpty()
        && a.LiquidStorage["蜜"] == 0 && a.Jars.Count == 0;
    return (ok, $"A={DescribeWS(a.CreateSnapshot())} B={DescribeWS(b.CreateSnapshot())}");
});

// 阶段 5：状态与 ID（问题 12、13、14）
Run("S8-A类订单无需从台(问题13)", () =>
{
    var master = new Workshop("M", new() { "圆罐" }, new(), new(), 20, 30);
    var slave = new Workshop("S", new() { "方罐" }, new(), new(), 20, 30);
    master.AddJar(new Jar("圆罐"));
    var sys = NewSystem(master, slave, new Order(new() { new Requirement { Shape = "圆罐" } }, "A"));
    sys.SetSlaveIndex(null); // 只选主台
    var fr = sys.CheckFeasibility(0);
    var exec = sys.ExecuteOrder(0);
    bool ok = fr?.Feasible == true && exec.Success && master.Jars.Count == 0;
    return (ok, $"feasible={fr?.Feasible} exec={exec.Success} reason={exec.Reason} M={DescribeWS(master.CreateSnapshot())}");
});

Run("S9-从台容量不足明确失败(问题14)", () =>
{
    var master = new Workshop("M", new() { "圆罐" }, new(), new(), 20, 30);
    var slave = new Workshop("S", new() { "方罐" }, new(), new(), 1, 30); // 容量 1
    master.AddJar(new Jar("圆罐", "酒"));
    master.AddJar(new Jar("圆罐", "蜜"));
    master.AddJar(new Jar("方罐"));
    var sys = NewSystem(master, slave, new Order(new() { new Requirement { Shape = "方罐" } }, "B"));
    var exec = sys.ExecuteOrder(0);
    bool ok = !exec.Success
        && exec.Reason != null && exec.Reason.Contains("容量")
        && master.Jars.Count == 3 && slave.Jars.Count == 0;
    return (ok, $"exec={exec.Success} reason={exec.Reason} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
});

Run("S10-删单后订单Id稳定(问题12)", () =>
{
    var sys = new JarSystem.Core.JarSystem();
    sys.ClearWorkshops(); sys.ClearOrders();
    sys.CreateOrder(new() { new Requirement { Shape = "圆罐" } }, "A");
    sys.CreateOrder(new() { new Requirement { Shape = "方罐" } }, "A");
    sys.CreateOrder(new() { new Requirement { Shape = "高罐" } }, "A");
    var ids = sys.Orders.Select(o => o.Id).ToList();
    sys.DeleteOrder(0);
    var remain = sys.Orders.Select(o => o.Id).ToList();
    bool ok = ids.Count == 3 && remain.Count == 2
        && remain[0] == ids[1] && remain[1] == ids[2];
    return (ok, $"ids={string.Join(",", ids)} remain={string.Join(",", remain)}");
});

// 阶段 6：规则统一（问题 16、17）
Run("S11-形状转换规则三处实现一致(问题16)", () =>
{
    var ws = new Workshop("W", new() { "圆罐", "方罐" }, new() { "三角罐" }, new(), 20, 30);
    var info = new JarSystem.Core.Solver.WorkshopInfo
    {
        BaseMarks = new() { "圆罐", "方罐" },
        AdditionalMarks = new() { "三角罐" },
        Ban = new(),
        MaxLiquids = 30
    };
    bool ok = true;
    var detail = "";
    foreach (var shape in Constants.SHAPES)
    {
        var a = ws.GetPossibleTransforms(shape).OrderBy(x => x).ToList();
        var b = info.GetPossibleTransforms(shape).OrderBy(x => x).ToList();
        if (!a.SequenceEqual(b))
        {
            ok = false;
            detail = $"shape={shape} ws=[{string.Join(",", a)}] info=[{string.Join(",", b)}]";
            break;
        }
    }
    return (ok, detail);
});

Run("S12-禁令规范化(问题17)", () =>
{
    var ws = new Workshop("W", new() { "圆罐" }, new(), new List<string> { "酒", "禁蜜" }, 20, 30);
    bool ok = ws.Ban.Count == 2 && ws.Ban.Contains("酒") && ws.Ban.Contains("蜜")
        && !ws.Ban.Contains("禁蜜")
        && ws.IsBanned("酒") && ws.IsBanned("蜜");
    return (ok, $"ban=[{string.Join(",", ws.Ban)}]");
});

// 2026-08-01 第二轮：禁液令不影响满罐转移 / Case2 剩余禁液不回仓库
Run("S13-回滚保留禁液满罐(禁液令不影响满罐转移)", () =>
{
    var master = new Workshop("M", new() { "圆罐" }, new(), new() { "酒" }, 20, 30); // 主台禁酒
    var slave = new Workshop("S", new() { "方罐" }, new(), new(), 20, 30);
    master.AddJar(new Jar("圆罐", "酒")); // 满罐(酒)在禁酒主台上，转移不受禁令影响
    var sys = NewSystem(master, slave, new Order(new() { new Requirement { Shape = "方罐", Liquid = "蜜" } }, "B"));
    var exec = sys.ExecuteOrder(0);
    // 订单必然失败（无蜜），转移后回滚：满罐(圆罐,酒)应原样归还主台，不清空液体
    bool ok = !exec.Success
        && master.Jars.Count == 1 && master.Jars[0].Shape == "圆罐" && master.Jars[0].Liquid == "酒"
        && slave.Jars.Count == 0;
    return (ok, $"exec={exec.Success} reason={exec.Reason} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
});

Run("S14-Case2剩余禁液丢弃且不回流仓库", () =>
{
    var a = new Workshop("A", new() { "圆罐" }, new() { "方罐" }, new() { "酒" }, 20, 30);
    var b = new Workshop("B", new() { "方罐" }, new(), new() { "蜜" }, 20, 30); // 新主台禁蜜
    a.AddJar(new Jar("圆罐"));
    a.AddLiquid("蜜", 3);
    var sys = NewSwitchSystem(a, b);
    sys.SetMasterIndex(0);
    int warehouseBefore = sys.ProducerModule.LiquidWarehouse["蜜"];
    var res = sys.SetMaster(1);
    // 改形消耗 1 蜜（圆罐→方罐），剩余 2 蜜被新主台禁 → 丢弃，绝不能回流中央仓库
    bool ok = (res.TryGetValue("success", out var sv) && sv is bool s1 && s1)
        && b.LiquidStorage["蜜"] == 0
        && a.LiquidStorage["蜜"] == 0
        && sys.ProducerModule.LiquidWarehouse["蜜"] == warehouseBefore
        && b.Jars.Count == 1 && b.Jars[0].Shape == "方罐" && b.Jars[0].IsEmpty();
    return (ok, $"A={DescribeWS(a.CreateSnapshot())} B={DescribeWS(b.CreateSnapshot())} warehouse蜜={sys.ProducerModule.LiquidWarehouse["蜜"]}(before={warehouseBefore})");
});

// 2026-08-01 第三轮：容量纳入可行性资源管理 + 主台交付罐跨阶段保留
Run("S15-重复需求跨阶段罐子保留(可行性=可执行)", () =>
{
    // 两个相同的满罐需求：主台制造一个后，从台阶段不得再次匹配/消耗该罐子，
    // 否则交付时同一罐子被用两次而失败（旧实现的假阳性场景）。
    var master = new Workshop("M", new() { "圆罐" }, new(), new(), 20, 30);
    var slave = new Workshop("S", new() { "圆罐" }, new(), new(), 20, 30);
    master.AddJar(new Jar("圆罐"));
    master.AddJar(new Jar("圆罐"));
    master.AddLiquid("酒", 2);
    var sys = NewSystem(master, slave, new Order(new()
    {
        new Requirement { Shape = "圆罐", Liquid = "酒" },
        new Requirement { Shape = "圆罐", Liquid = "酒" }
    }, "B"));
    var fr = sys.CheckFeasibility(0);
    var exec = sys.ExecuteOrder(0);
    bool ok = fr?.Feasible == true && exec.Success
        && master.Jars.Count == 0 && slave.Jars.Count == 0;
    return (ok, $"feasible={fr?.Feasible} exec={exec.Success} reason={exec.Reason} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
});

Run("S16-从台罐子容量纳入可行性(转移不足即不可行)", () =>
{
    // 用户场景：主 5/7 罐 + 液体 8/8，从 0/3 罐 + 0/5 液，禁令一致。
    // 选择性转移下：从台只需接收订单需要的罐子（此处 1 个），其余 4 个留在主台。
    var master = new Workshop("M", new() { "圆罐", "方罐" }, new(), new(), 7, 8);
    var slave = new Workshop("S", new() { "方罐" }, new(), new(), 3, 5);
    for (int i = 0; i < 5; i++) master.AddJar(new Jar("圆罐"));
    master.AddLiquid("蜜", 4); master.AddLiquid("酒", 4);
    var sys = NewSystem(master, slave, new Order(new() { new Requirement { Shape = "方罐" } }, "B"));
    var fr = sys.CheckFeasibility(0);
    var exec = sys.ExecuteOrder(0);
    bool ok = fr?.Feasible == true && exec.Success
        && fr?.TransferJarIds != null && fr.TransferJarIds.Count == 1
        && master.Jars.Count == 4 && slave.Jars.Count == 0;
    return (ok, $"feasible={fr?.Feasible} exec={exec.Success} transfer={fr?.TransferJarIds?.Count} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
});

Run("S17-液体只转移从台需要的量(2b)", () =>
{
    // 从台液体上限 3：主台 4 蜜 4 酒，但从台阶段只需要 1 酒（改形成本，取第一个可用液体）。
    // 2b 语义：只转移需要的量（酒×1），其余（酒×3、蜜×4）全部留在主台，不产生截断日志。
    var master = new Workshop("M", new() { "圆罐", "方罐" }, new(), new(), 20, 8);
    var slave = new Workshop("S", new() { "方罐" }, new(), new(), 20, 3);
    master.AddJar(new Jar("圆罐"));
    master.AddLiquid("蜜", 4); master.AddLiquid("酒", 4);
    var sys = NewSystem(master, slave, new Order(new() { new Requirement { Shape = "方罐" } }, "B"));
    var fr = sys.CheckFeasibility(0);
    var exec = sys.ExecuteOrder(0);
    bool sawClamp = exec.Operations?.Any(o => o.Action == "transfer_liquid_failed") == true;
    bool ok = fr?.Feasible == true && exec.Success
        && fr?.TransferLiquids != null
        && fr.TransferLiquids["酒"] == 1 && fr.TransferLiquids["蜜"] == 0
        && master.LiquidStorage["酒"] == 3 && master.LiquidStorage["蜜"] == 4
        && slave.LiquidStorage["酒"] == 0 && slave.LiquidStorage["蜜"] == 0
        && !sawClamp;
    return (ok, $"feasible={fr?.Feasible} exec={exec.Success} sawClamp={sawClamp} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
});

// 2026-08-01 第四轮：切换事件罐子价值优先级
Run("S18-切换转移按罐子价值保留", () =>
{
    // 新主台仅 3 个罐位：应保留 价值0 同液满罐(圆罐,酒) + 价值1 同形空罐(方罐) +
    // 价值2 基础/附加之外的异形空罐(高罐)；丢弃 价值3 附加标记空罐(三角罐) 与 价值4 异液满罐(长罐,蜜)。
    var a = new Workshop("A", new() { "圆罐" }, new(), new() { "酒" }, 20, 30);
    var b = new Workshop("B", new() { "方罐" }, new() { "三角罐" }, new() { "蜜" }, 3, 30);
    a.AddJar(new Jar("圆罐", "酒"));
    a.AddJar(new Jar("方罐"));
    a.AddJar(new Jar("高罐"));
    a.AddJar(new Jar("三角罐"));
    a.AddJar(new Jar("长罐", "蜜"));
    var sys = NewSwitchSystem(a, b);
    sys.SetMasterIndex(0);
    var res = sys.SetMaster(1);
    bool kept = b.Jars.Count == 3
        && b.Jars.Any(j => j.Shape == "圆罐" && j.Liquid == "酒")
        && b.Jars.Any(j => j.Shape == "方罐" && j.IsEmpty())
        && b.Jars.Any(j => j.Shape == "高罐" && j.IsEmpty());
    var lastEvent = sys.EventHistory.LastOrDefault();
    int discards = 0;
    if (lastEvent != null && lastEvent.TryGetValue("operations", out var opsObj) && opsObj is System.Collections.IList ol)
        discards = ol.Cast<object>().Count(o => o is Dictionary<string, object?> d && d.TryGetValue("action", out var av) && av as string == "discard_jar");
    bool ok = (res.TryGetValue("success", out var sv) && sv is bool s1 && s1) && kept && discards == 2 && a.Jars.Count == 0;
    return (ok, $"A={DescribeWS(a.CreateSnapshot())} B={DescribeWS(b.CreateSnapshot())} discards={discards}");
});

Run("S19-工具罐转移并回流主台(选择性转移)", () =>
{
    // 用户场景：主台禁酒（自己倒不出圆罐中的酒）、从台禁蜜（酒在从台可用）。
    // 主台无游离酒，但有 满罐(圆罐,酒) 与 空方罐；从台基础[方罐]附加[三角罐]；B 订单 空三角罐。
    // 两个罐子都必须进入从台：圆罐/酒 是「工具罐」（倒出酒支付改形），方罐被改形成三角罐（交付）。
    // 订单结束后，空圆罐回流主台。
    var master = new Workshop("M", new() { "圆罐" }, new(), new() { "酒" }, 20, 30);
    var slave = new Workshop("S", new() { "方罐" }, new() { "三角罐" }, new() { "蜜" }, 20, 30);
    master.AddJar(new Jar("圆罐", "酒"));
    master.AddJar(new Jar("方罐"));
    var sys = NewSystem(master, slave, new Order(new() { new Requirement { Shape = "三角罐" } }, "B"));
    var fr = sys.CheckFeasibility(0);
    var exec = sys.ExecuteOrder(0);
    bool ok = fr?.Feasible == true && exec.Success
        && fr?.TransferJarIds != null && fr.TransferJarIds.Count == 2
        && master.Jars.Count == 1 && master.Jars[0].Shape == "圆罐" && master.Jars[0].IsEmpty()
        && slave.Jars.Count == 0;
    return (ok, $"feasible={fr?.Feasible} exec={exec.Success} transfer={fr?.TransferJarIds?.Count} M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
});

// 2026-08 批次3：PourInCommand 撤销账目修正（继承自 Node 版的历史错误；该方法此前不可达，
// 修正前若被调用会：先被 PourOut 的「罐子已被操作」拒绝 → 撤销失败；即便成功还会重复返还液体）
Run("S20-倒入命令撤销后液体与标记复原", () =>
{
    var ws = new Workshop("W", new() { "圆罐" }, new(), new(), 20, 30);
    var jar = new Jar("圆罐");
    ws.AddJar(jar);
    ws.AddLiquid("酒", 2);

    var cmd = new PourInCommand(jar, "酒");
    cmd.Execute(ws, null);
    bool afterExecute = jar.IsFull() && jar.Liquid == "酒" && jar.IsMarked && ws.LiquidStorage["酒"] == 1;

    var undone = cmd.Undo(ws, null) as CommandResult;
    bool ok = afterExecute && undone?.Success == true
        && jar.IsEmpty() && !jar.IsMarked && ws.LiquidStorage["酒"] == 2;
    return (ok, $"afterExecute={afterExecute} undo={undone?.Success} 酒={ws.LiquidStorage["酒"]} marked={jar.IsMarked} empty={jar.IsEmpty()}");
});

Console.WriteLine($"=== {bugs} bugs / {tests} random + {capBugs} bugs / {capTests} capacity + {detCount} deterministic ===");
foreach (var b in bugReports) Console.WriteLine(b);

// ============================================================
// 工具函数
// ============================================================

JarSystem.Core.JarSystem NewSystem(Workshop master, Workshop slave, Order order)
{
    var sys = new JarSystem.Core.JarSystem();
    sys.ClearWorkshops(); sys.ClearOrders();
    sys.AddWorkshop(master); sys.AddWorkshop(slave);
    sys.SetMasterIndex(0); sys.SetSlaveIndex(1);
    sys.AddOrder(order);
    return sys;
}

JarSystem.Core.JarSystem NewSwitchSystem(params Workshop[] workshops)
{
    var sys = new JarSystem.Core.JarSystem();
    sys.ClearWorkshops();
    foreach (var w in workshops) sys.AddWorkshop(w);
    return sys;
}

void Run(string name, Func<(bool Ok, string Detail)> scenario)
{
    detCount++;
    try
    {
        var (ok, detail) = scenario();
        if (ok) Console.WriteLine($"PASS {name}");
        else { Console.WriteLine($"FAIL {name}: {detail}"); bugs++; bugReports.Add($"DET {name}: {detail}"); }
    }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex.Message}"); bugs++; bugReports.Add($"DET-CRASH {name}: {ex.Message}"); }
}

string DescribeOrder(OrderSpec o) => $"{o.Type} [{string.Join(",", o.Reqs.Select(r => r.Liquid != null ? $"{r.Shape}+{r.Liquid}" : r.Shape))}]";
string DescribeWS(WorkshopSnapshot s) => $"BM=[{string.Join(",", s.BaseMarks)}] AM=[{string.Join(",", s.AdditionalMarks)}] Ban=[{string.Join(",", s.Ban)}] J=[{string.Join(",", s.Jars.Select(j => j.GetDescription()))}] L=[{string.Join(",", s.Liquids.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}:{kv.Value}"))}]";

(Workshop, Workshop, OrderSpec) Gen(Random r)
{
    var sh = Constants.SHAPES; var li = Constants.LIQUIDS;
    List<string> P(int min, int max, List<string>? ex = null)
    {
        var res = new List<string>();
        int n = r.Next(min, max + 1);
        var pool = sh.Where(s => ex == null || !ex.Contains(s)).ToList();
        while (res.Count < n && pool.Count > 0) { int i = r.Next(pool.Count); res.Add(pool[i]); pool.RemoveAt(i); }
        return res;
    }
    string N() => new[] { "A", "B", "C", "D", "E", "F", "G" }[r.Next(7)] + new[] { "1", "2", "3", "4" }[r.Next(4)];

    var bm = P(1, 2); var am = P(0, 2, bm);
    var ban = new List<string>();
    if (r.Next(3) == 0) ban.Add(li[r.Next(2)]);
    if (r.Next(5) == 0) { var l = li[r.Next(2)]; if (!ban.Contains(l)) ban.Add(l); }
    var m = new Workshop("M_" + N(), bm, am, ban, 30, 30);

    var sbm = P(1, 2); var sam = P(0, 2, sbm);
    var sban = new List<string>();
    if (r.Next(4) == 0) sban.Add(li[r.Next(2)]);
    var sl = new Workshop("S_" + N(), sbm, sam, sban, 30, 30);

    // 主台资源
    int jc = r.Next(1, 7);
    for (int i = 0; i < jc; i++)
    {
        var s = sh[r.Next(sh.Length)];
        string? l = null;
        if (r.Next(2) == 0) { var lq = li[r.Next(2)]; if (!m.IsBanned(lq)) l = lq; }
        m.AddJar(new Jar(s, l));
    }
    foreach (var l in li)
        if (!m.IsBanned(l))
            for (int i = 0; i < r.Next(0, 5); i++) m.AddLiquid(l);

    // 从台资源（新增：覆盖 slave_only / combined / 回滚等路径）
    int sjc = r.Next(0, 5);
    for (int i = 0; i < sjc; i++)
    {
        var s = sh[r.Next(sh.Length)];
        string? l = null;
        if (r.Next(2) == 0) { var lq = li[r.Next(2)]; if (!sl.IsBanned(lq)) l = lq; }
        sl.AddJar(new Jar(s, l));
    }
    foreach (var l in li)
        if (!sl.IsBanned(l))
            for (int i = 0; i < r.Next(0, 4); i++) sl.AddLiquid(l);

    int nr = r.Next(1, 4);
    var reqs = new List<Requirement>();
    for (int i = 0; i < nr; i++)
    {
        string? liqu = r.Next(2) == 0 ? li[r.Next(2)] : null;
        reqs.Add(new Requirement { Shape = sh[r.Next(sh.Length)], Liquid = liqu });
    }
    reqs = reqs.OrderBy(rr => rr.Liquid != null ? 1 : 0).ToList();
    return (m, sl, new OrderSpec(reqs, r.Next(2) == 0 ? "A" : "B"));
}

(Workshop, Workshop, OrderSpec) GenCap(Random r)
{
    var sh = Constants.SHAPES; var li = Constants.LIQUIDS;
    List<string> P(int min, int max, List<string>? ex = null)
    {
        var res = new List<string>();
        int n = r.Next(min, max + 1);
        var pool = sh.Where(s => ex == null || !ex.Contains(s)).ToList();
        while (res.Count < n && pool.Count > 0) { int i = r.Next(pool.Count); res.Add(pool[i]); pool.RemoveAt(i); }
        return res;
    }
    string N() => new[] { "A", "B", "C", "D", "E", "F", "G" }[r.Next(7)] + new[] { "1", "2", "3", "4" }[r.Next(4)];

    // 小容量：罐位 1-4、液位 1-3（每种液体）
    var bm = P(1, 2); var am = P(0, 2, bm);
    var sbm = P(1, 2); var sam = P(0, 2, sbm);
    var m = new Workshop("M_" + N(), bm, am, GenBan(r, 3), r.Next(1, 5), r.Next(1, 4));
    var sl = new Workshop("S_" + N(), sbm, sam, GenBan(r, 4), r.Next(1, 4), r.Next(1, 3));

    for (int i = 0; i < r.Next(1, 7); i++)
    {
        var s = sh[r.Next(sh.Length)];
        string? l = null;
        if (r.Next(2) == 0) { var lq = li[r.Next(2)]; if (!m.IsBanned(lq)) l = lq; }
        if (!m.AddJar(new Jar(s, l))) break;
    }
    foreach (var l in li)
        if (!m.IsBanned(l))
            for (int i = 0; i < r.Next(0, 4); i++) m.AddLiquid(l);

    for (int i = 0; i < r.Next(0, 5); i++)
    {
        var s = sh[r.Next(sh.Length)];
        string? l = null;
        if (r.Next(2) == 0) { var lq = li[r.Next(2)]; if (!sl.IsBanned(lq)) l = lq; }
        if (!sl.AddJar(new Jar(s, l))) break;
    }
    foreach (var l in li)
        if (!sl.IsBanned(l))
            for (int i = 0; i < r.Next(0, 3); i++) sl.AddLiquid(l);

    int nr = r.Next(1, 4);
    var reqs = new List<Requirement>();
    for (int i = 0; i < nr; i++)
    {
        string? liqu = r.Next(2) == 0 ? li[r.Next(2)] : null;
        reqs.Add(new Requirement { Shape = sh[r.Next(sh.Length)], Liquid = liqu });
    }
    reqs = reqs.OrderBy(rr => rr.Liquid != null ? 1 : 0).ToList();
    return (m, sl, new OrderSpec(reqs, r.Next(2) == 0 ? "A" : "B"));
}

List<string> GenBan(Random r, int chance)
{
    var ban = new List<string>();
    if (r.Next(chance) == 0) ban.Add(Constants.LIQUIDS[r.Next(2)]);
    if (r.Next(chance * 2) == 0) { var l = Constants.LIQUIDS[r.Next(2)]; if (!ban.Contains(l)) ban.Add(l); }
    return ban;
}

// ============================================================
// 契约快照工具（仅 --dump-contract 模式使用）
// ============================================================

void DumpContract()
{
    // 固定场景：默认世界 + 主从选择 + 三个订单（A/B 各含满罐与空罐需求）+ 推进 + 一次执行
    var sys = new JarSystem.Core.JarSystem();
    sys.SetMasterIndex(0);
    sys.SetSlaveIndex(1);
    sys.CreateOrder(new() { new Requirement { Shape = "圆罐" } }, "A");
    sys.CreateOrder(new() { new Requirement { Shape = "方罐", Liquid = "蜜" }, new Requirement { Shape = "高罐" } }, "B");
    sys.CreateOrder(new() { new Requirement { Shape = "圆罐", Liquid = "酒" } }, "A");
    sys.CheckFeasibility(0);
    sys.CheckFeasibility(1);
    sys.Tick(3);
    sys.ExecuteOrder(0);
    sys.SetSlave(2);

    var lines = new List<string>();
    Flatten("state", sys.GetState(), lines);

    var dir = Path.Combine("重构报告", "_contract");
    Directory.CreateDirectory(dir);
    var file = Path.Combine(dir, "state_keys.txt");
    File.WriteAllLines(file, lines);
    Console.WriteLine($"contract dumped: {file} ({lines.Count} lines)");
}

// ============================================================
// 契约 Schema 导出（仅 --dump-schema 模式使用）
//
// 用官方 JsonSchemaExporter（.NET 9 起内置在 System.Text.Json，**不需要第三方包**）
// 把对外 DTO 的**结构**导出成 JSON Schema，作为"模块间接口产物"落盘。
//
// 与 --dump-contract 的文本基线相比：
//   1. schema 描述结构而非某次运行的数据 ⇒ 不受固定场景与 <volatile> 时间戳影响；
//   2. 它天然覆盖**路径步骤的 14 个字段**（文本基线因为固定场景里路径为空而没覆盖）；
//   3. 它把"弱类型逃逸"变成可量化指标：Dictionary<string,object> / List<object> 处会退化成
//      什么都允许的节点（本工具输出 loose=N 计数），那就是契约里没有类型的空洞。
//
// 每个类型独立导出：某个类型失败只记录 $error，不影响其它类型，也不让 verify.bat 硬失败。
// ============================================================

void DumpSchema()
{
    var dir = Path.Combine("重构报告", "_contract");
    Directory.CreateDirectory(dir);
    var file = Path.Combine(dir, "state.schema.json");

    // 注意：必须用**拷贝构造** JsonSerializerOptions.Default 再改属性。
    // 直接 new JsonSerializerOptions{...} 没有 TypeInfoResolver，
    // JsonSchemaExporter 会抛 "JsonSerializerOptions instance must specify a TypeInfoResolver
    // setting before being marked as read-only"（官方示例用的正是拷贝构造写法）。
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default)
    {
        // 与 Web 层序列化策略对齐（DTO 上的 [JsonPropertyName] 优先；策略只作用于未标注成员）
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        // 锁死"不许偷偷加字段"：生成的 schema 会带 "additionalProperties": false
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    var exporterOptions = new System.Text.Json.Schema.JsonSchemaExporterOptions
    {
        TreatNullObliviousAsNonNullable = true
    };

    var targets = new (string Name, Type Type)[]
    {
        ("StateDto", typeof(JarSystem.Core.Services.StateDto)),
        ("WorkshopStateDto", typeof(JarSystem.Core.Services.WorkshopStateDto)),
        ("JarStateDto", typeof(JarSystem.Core.Services.JarStateDto)),
        ("OrderStateDto", typeof(JarSystem.Core.Services.OrderStateDto)),
        ("FeasibilityStateDto", typeof(JarSystem.Core.Services.FeasibilityStateDto)),
        ("WarehouseStateDto", typeof(JarSystem.Core.Services.WarehouseStateDto)),
        ("GlobalStateDto", typeof(JarSystem.Core.Services.GlobalStateDto)),
        ("SolverStepDto", typeof(JarSystem.Core.Orders.SolverStepDto))
    };

    var types = new System.Text.Json.Nodes.JsonObject();
    Console.WriteLine($"schema file: {file}");
    int looseTotal = 0;
    foreach (var (name, type) in targets)
    {
        try
        {
            var node = System.Text.Json.Schema.JsonSchemaExporter.GetJsonSchemaAsNode(options, type, exporterOptions);
            types[name] = node;
            int loose = CountLooseNodes(node);
            looseTotal += loose;
            Console.WriteLine($"schema {name}: OK loose={loose}");
        }
        catch (Exception ex)
        {
            // 关键诊断：导出失败本身就是结论 —— 该类型含无法描述的结构
            types[name] = new System.Text.Json.Nodes.JsonObject
            {
                ["$error"] = ex.GetType().Name + ": " + ex.Message
            };
            Console.WriteLine($"schema {name}: FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    var root = new System.Text.Json.Nodes.JsonObject
    {
        ["generatedBy"] = "JsonSchemaExporter (System.Text.Json, .NET 10) - dotnet run -- --dump-schema",
        ["note"] = "每个类型独立导出；出现 $error 表示该类型含无法描述的结构（弱类型逃逸）",
        ["types"] = types
    };
    // 同样用拷贝构造：没有 TypeInfoResolver 的 options 连 JsonNode 序列化都会抛同样的异常
    File.WriteAllText(file, root.ToJsonString(
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default) { WriteIndented = true }));
    Console.WriteLine($"schema total loose={looseTotal}");
}

// 统计"什么都能通过"的 schema 节点（布尔 true / 空对象）：
// 它们就是契约里**没有类型**的空洞，来自 Dictionary<string,object> / List<object> 这类弱类型逃逸。
int CountLooseNodes(System.Text.Json.Nodes.JsonNode node)
{
    switch (node)
    {
        case System.Text.Json.Nodes.JsonValue v when v.TryGetValue<bool>(out var b):
            return b ? 1 : 0;
        case System.Text.Json.Nodes.JsonObject o:
            if (o.Count == 0) return 1;
            int n = 0;
            foreach (var kv in o)
                if (kv.Value != null) n += CountLooseNodes(kv.Value);
            return n;
        case System.Text.Json.Nodes.JsonArray a:
            int m = 0;
            foreach (var item in a)
                if (item != null) m += CountLooseNodes(item);
            return m;
        default:
            return 0;
    }
}

void Flatten(string path, object? node, List<string> lines)
{
    switch (node)
    {
        case null:
            lines.Add(path + " = null");
            break;
        case System.Collections.IDictionary dict:
            foreach (System.Collections.DictionaryEntry kv in dict)
                Flatten(path + "." + kv.Key, kv.Value, lines);
            break;
        case System.Collections.IEnumerable list when node is not string:
            int i = 0;
            foreach (var item in list) { Flatten(path + "[" + i + "]", item, lines); i++; }
            break;
        default:
            var type = node.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
            {
                // 时间戳类字段在每次运行中都会变化，快照里统一记为 <volatile>
                var leaf = path[(path.LastIndexOf('.') + 1)..];
                lines.Add(path + " = " + (leaf.Equals("timestamp", StringComparison.OrdinalIgnoreCase) ? "<volatile>" : node.ToString()));
                break;
            }
            foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                Flatten(path + "." + JsonName(p), p.GetValue(node), lines);
            break;
    }
}

// 快照里用「对外 JSON 键名」而不是 C# 属性名：优先取 [JsonPropertyName]，否则按序列化策略转 camelCase。
// 这样快照 diff 才真正等价于前端所见的 JSON 契约。
string JsonName(System.Reflection.PropertyInfo p)
{
    var attr = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>(p);
    if (attr != null && !string.IsNullOrEmpty(attr.Name)) return attr.Name;
    var n = p.Name;
    if (n.Length <= 1) return n.ToLowerInvariant();
    return char.ToLowerInvariant(n[0]) + n.Substring(1);
}

record OrderSpec(List<Requirement> Reqs, string Type);

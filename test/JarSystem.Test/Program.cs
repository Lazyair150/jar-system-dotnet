using JarSystem.Core;
using JarSystem.Core.Commands;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Sessions;
using JarSystem.Core.Services;
using JarSystem.Test;   // ReferenceSolver（差分测试用的独立参考实现）

// 别名：本文件里 schema 节点操作很密集，短名可读性明显更好
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using JsonValue = System.Text.Json.Nodes.JsonValue;

// ============================================================
// ============================================================
// 协议契约清单（命令名 / 消息名）——**唯一来源**，被两处使用：
//   ① CheckProtocol：核对 Web 层源码里实际使用的名字与本清单一致；
//   ② DumpSchema：生成前端类型产物 contract.d.ts 时把名字联成联合类型。
// 清单刻意硬编码：加命令/消息必须显式改这里 ⇒ 逼出一次"这是有意的契约变更"。
// ============================================================
string[] ProtocolCommands =
[
    "getState", "createOrder", "createRandomOrder", "createWorkshop", "createRandomWorkshop",
    "updateWorkshopCapacity", "setMaster", "setSlave", "executeOrder", "deleteOrder",
    "checkFeasibility", "checkAllFeasibility", "manualTick", "reset", "updateProducerRate",
    "startTick", "stopTick", "setAutoCheck"
];

string[] ProtocolMessages =
[
    "init", "state", "orderExecuted", "workshopSwitched", "feasibilityResult",
    "allFeasibilityResults", "autoCheckResults", "autoCheckChanged", "error", "world"
];

// ============================================================
// 服务端 → 客户端 的**匿名对象**载荷声明（键集合）。
//
// 为什么需要第二份守卫：state / init / world / allFeasibilityResults / autoCheckResults 的载荷是 DTO，
// 由 --dump-schema 的结构守卫覆盖；但其余消息的载荷仍是**匿名对象**，键名只活在源码字面量里 ——
// 拼错一个字母照样编译通过、测试通过、快照通过，客户端只是静默少一个字段（历史真事故：
// 首轮守卫只核对命令名/消息名/客户端载荷键，回包键没有任何机器检查）。
// 因此这里把回包键逐个列出，并由 CheckProtocol 用正则从 CommandDispatcher.cs **反向提取**后比对。
// 注意：只声明 `data = new { ... }` 形式的消息；`data = sys.GetState()` 这类 DTO 载荷由 schema 守卫负责。
// ============================================================
(string Message, string[] Keys)[] MessagePayloads =
[
    ("orderExecuted", ["success", "reason", "operations", "transferOccurred"]),
    ("workshopSwitched", ["fromIndex", "toIndex", "caseType", "operations"]),
    ("feasibilityResult", ["orderId", "result"]),
    ("autoCheckChanged", ["enabled"]),
    ("error", ["message"])
];

// ============================================================
// 客户端 → 服务端 的命令载荷声明（键 + TS 类型）。
// 键集合取自 CommandDispatcher 的 JsonArgs 读取点；CheckProtocol 会核对
// **每个声明的键确实出现在 CommandDispatcher.cs 里** ⇒ 改名即报警（防"两套规格漂移"）。
// 空数组 = 该命令不带载荷。
// ============================================================
(string Command, string Interface, (string Key, string TsType)[] Fields)[] CommandPayloads =
[
    ("createOrder", "CreateOrderPayload", [("requirements", "RequirementPayload[]"), ("orderType", "\"A\" | \"B\"")]),
    ("createWorkshop", "CreateWorkshopPayload", [("name", "string"), ("baseMarks", "string[]"), ("additionalMarks", "string[]"), ("ban", "string[]")]),
    ("updateWorkshopCapacity", "UpdateWorkshopCapacityPayload", [("index", "number"), ("maxJars", "number | null"), ("maxLiquids", "number | null")]),
    ("setMaster", "SetMasterPayload", [("index", "number")]),
    ("setSlave", "SetSlavePayload", [("index", "number")]),
    ("executeOrder", "ExecuteOrderPayload", [("orderId", "number"), ("orderIndex", "number")]),
    ("checkFeasibility", "CheckFeasibilityPayload", [("orderId", "number"), ("orderIndex", "number")]),
    ("deleteOrder", "DeleteOrderPayload", [("orderId", "number"), ("orderIndex", "number")]),
    ("updateProducerRate", "UpdateProducerRatePayload", [("producerName", "string"), ("shape", "string"), ("rate", "number")]),
    ("setAutoCheck", "SetAutoCheckPayload", [("enabled", "boolean")]),
    ("createRandomOrder", "CreateRandomOrderPayload", []),
    ("createRandomWorkshop", "CreateRandomWorkshopPayload", []),
    ("getState", "GetStatePayload", []),
    ("checkAllFeasibility", "CheckAllFeasibilityPayload", []),
    ("manualTick", "ManualTickPayload", []),
    ("reset", "ResetPayload", []),
    ("startTick", "StartTickPayload", []),
    ("stopTick", "StopTickPayload", [])
];

// 契约快照模式（重构专用，不影响默认测试）：
//   dotnet run --project test\JarSystem.Test -c Release -- --dump-contract
// 用固定场景导出 GetState() 的全部键路径与值到 重构报告/_contract/state_keys.txt，
// 供重构前后 diff（时间戳类字段记为 <volatile>）。
// ============================================================
if (args.Length > 0 && args[0] == "--dump-contract")
{
    // 可选：--config <path> ⇒ 从世界配置装配（用于证明"配置装配 == 内置世界"）
    DumpContract(ArgValue("--config"));
    return;
}

// 取 "--name value" 形式的参数值（找不到返回 null）
string? ArgValue(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

// 契约 Schema 导出模式（模块边界产物）：
//   dotnet run --project test\JarSystem.Test -c Release -- --dump-schema
if (args.Length > 0 && args[0] == "--dump-schema")
{
    DumpSchema();
    return;
}

// 协议契约守卫：核对 Web 层源码里的**命令名/消息名**与本文列出的清单一致：
//   dotnet run --project test\JarSystem.Test -c Release -- --check-protocol
if (args.Length > 0 && args[0] == "--check-protocol")
{
    CheckProtocol();
    return;
}

// 求解器差分测试（让"换算法"这件事**有验收门**）：
//   dotnet run --project test\JarSystem.Test -c Release -- --check-solver
// 同一批场景分别喂给"生产求解器"与"独立参考求解器"（BFS + 精确状态键 + 不做剪枝），
// 比对判定结论。若生产实现的剪枝漏掉真解，这里会报反例。
if (args.Length > 0 && args[0] == "--check-solver")
{
    CheckSolverDifferential();
    return;
}

// 会话回放（Event Sourcing 切片）：
//   dotnet run --project test\JarSystem.Test -c Release -- --check-replay
// 跑一段确定性会话 → 把命令日志导成 JSON → 读回 → 重放进**全新系统** → 比对两次 GetState() 的 JSON 是否逐字节相同。
if (args.Length > 0 && args[0] == "--check-replay")
{
    CheckSessionReplay();
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
        // S1：能力参数改由 WorkshopCapability 承载（与 Workshop 共用同一份不可变对象）
        Capability = new JarSystem.Core.Domain.WorkshopCapability(
            new[] { "圆罐", "方罐" }, new[] { "三角罐" }),
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
    int warehouseBefore = sys.ProducerModule.Warehouse.LiquidWarehouse["蜜"];
    var res = sys.SetMaster(1);
    // 改形消耗 1 蜜（圆罐→方罐），剩余 2 蜜被新主台禁 → 丢弃，绝不能回流中央仓库
    bool ok = (res.TryGetValue("success", out var sv) && sv is bool s1 && s1)
        && b.LiquidStorage["蜜"] == 0
        && a.LiquidStorage["蜜"] == 0
        && sys.ProducerModule.Warehouse.LiquidWarehouse["蜜"] == warehouseBefore
        && b.Jars.Count == 1 && b.Jars[0].Shape == "方罐" && b.Jars[0].IsEmpty();
    return (ok, $"A={DescribeWS(a.CreateSnapshot())} B={DescribeWS(b.CreateSnapshot())} warehouse蜜={sys.ProducerModule.Warehouse.LiquidWarehouse["蜜"]}(before={warehouseBefore})");
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

// 2026-09 批次：**求解器注入缝**（"策略模式要能换算法，先得有可替换的入口"）。
// 原先是 Checker { get; } = new()，门面把缝焊死了；现在构造可注入 ISolver。
// 本用例注入一个**替身**（记录调用次数后委托真实实现）：既证明注入真的贯通，
// 又保持行为不变，因此可以安全放进基线。将来换算法时，这条用例就是"缝还在"的守卫。
Run("S21-求解器注入缝贯通(策略模式可替换)", () =>
{
    var spy = new CountingSolver();
    var sys = new JarSystem.Core.JarSystem(randomSeed: 7, solver: spy);
    sys.SetMasterIndex(0);
    var master = sys.Workshops[0];
    master.AddJar(new Jar("圆罐"));
    sys.CreateOrder(new() { new Requirement { Shape = "圆罐" } }, "A");

    var fr = sys.CheckFeasibility(0);
    var exec = sys.ExecuteOrder(0);
    bool ok = spy.Calls > 0 && fr?.Feasible == true && exec.Success && master.Jars.Count == 0;
    return (ok, $"替身被调用={spy.Calls} 可行={fr?.Feasible} 执行={exec.Success} 主台剩余罐={master.Jars.Count}");
});

Console.WriteLine($"=== {bugs} bugs / {tests} random + {capBugs} bugs / {capTests} capacity + {detCount} deterministic ===");
foreach (var b in bugReports) Console.WriteLine(b);

// ============================================================
// 工具函数
// ============================================================

// solver 可选：差分测试用它把"判定算法"整体换掉（门面注入缝，见 S21）
JarSystem.Core.JarSystem NewSystem(Workshop master, Workshop slave, Order order,
    JarSystem.Core.Abstractions.ISolver? solver = null)
{
    var sys = new JarSystem.Core.JarSystem(solver: solver);
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

// ============================================================
// 求解器差分测试（--check-solver）
//
// 目的：让"换算法"有**验收门**，而不是只靠"缝能注入"。做法是同一批场景跑两遍：
//   ① 生产链路（记忆化 DFS + 同构剪枝 + 工作预算）；
//   ② 同一链路但把 ISolver 换成 ReferenceSolver（BFS + 精确状态键 + 不做剪枝 + 不设预算）。
// 两者判定结论必须一致 —— 不一致说明**生产实现的剪枝剪掉了真解**（或参考实现有 bug）。
//
// 输出标记保持 ASCII（会被 verify.bat 的 findstr 回显到 GBK 控制台）：
//   SOLVER-OK (compared=…, agree=…, feasible=…, undetermined=…)
//   SOLVER-MISMATCH (n)
// 明细行用 "mismatch detail:" 前缀（不含 SOLVER- 字样，因此不会被 findstr 回显成乱码）。
// ============================================================
// ============================================================
// 会话回放检查（--check-replay）
//
// 判据（**逐字节**）：同一份世界配置 + 同一个种子 + 全新 ID 源 ⇒
// 「跑一遍会话」与「把命令日志 JSON 读回来后重放」必须得到**完全相同的 GetState() JSON**，
// 并且重放系统自己记录出来的日志也要与原日志逐条相同（回放走的是同一套意图方法，不绕后门）。
//
// 输出标记保持 ASCII：`REPLAY-OK (…)` / `REPLAY-MISMATCH`。
// ============================================================
void CheckSessionReplay()
{
    const int seed = 20260916;
    var jsonOptions = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase   // 与 Web 宿主同一套序列化口径
    };

    // ---- ① 原次会话：全新 ID 源 + 固定种子 ----
    var original = new JarSystem.Core.JarSystem(
        randomSeed: seed, world: WorldOptions.Default,
        jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());
    RunScriptedSession(original);

    string stateJsonA = NormalizeState(System.Text.Json.JsonSerializer.Serialize(original.GetState(), jsonOptions));
    string logJsonA = WorldCommandJson.ToJson(original.CommandLog);

    // ---- ② 日志落盘/读回（真正的"存档"路径）----
    var commands = WorldCommandJson.FromJson(logJsonA);
    var replay = new JarSystem.Core.JarSystem(
        randomSeed: seed, world: WorldOptions.Default,
        jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());
    int applied = SessionReplay.Into(replay, commands);

    string stateJsonB = NormalizeState(System.Text.Json.JsonSerializer.Serialize(replay.GetState(), jsonOptions));
    string logJsonB = WorldCommandJson.ToJson(replay.CommandLog);

    bool stateEqual = stateJsonA == stateJsonB;
    bool logEqual = logJsonA == logJsonB;
    Console.WriteLine(stateEqual && logEqual
        ? $"REPLAY-OK (commands={applied}, stateBytes={stateJsonA.Length}, logBytes={logJsonA.Length})"
        : $"REPLAY-MISMATCH (stateEqual={stateEqual}, logEqual={logEqual}, commands={applied})");
    if (!stateEqual) Console.WriteLine("  state diff at: " + FirstDifference(stateJsonA, stateJsonB));
    if (!logEqual) Console.WriteLine("  log   diff at: " + FirstDifference(logJsonA, logJsonB));
    if (!stateEqual || !logEqual) Environment.ExitCode = 1;

    string FirstDifference(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i])
                return $"字符 {i}: A=…{a[Math.Max(0, i - 40)..Math.Min(a.Length, i + 40)]}… / B=…{b[Math.Max(0, i - 40)..Math.Min(b.Length, i + 40)]}…";
        return $"长度不同: A={a.Length} B={b.Length}";
    }

    // `feasibilityResult.timestamp` 装的是**分析耗时毫秒**（不是时间点），属于墙面时间测得值，
    // 回放不可能复现 ⇒ 与契约快照同一口径，把它记为 <volatile> 再比对。
    // 注意：**只**排除这种纯数字耗时；`eventHistory[].timestamp` 是 ISO 墙钟时间，
    // 已由"命令日志带时间"（WorldCommand.Time）保证可复现，仍然逐字节比对。
    string NormalizeState(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"timestamp\":\"\\d+\"", "\"timestamp\":\"<volatile>\"");
}

// 一段覆盖尽可能多意图方法的确定性会话。注意两处**宿主约定**：
// 注入工作台/订单时必须用系统自己的 ID 源（JarIds/OrderIds），否则回放的 ID 无法一致。
void RunScriptedSession(JarSystem.Core.JarSystem sys)
{
    sys.SetMasterIndex(0);
    sys.SetSlaveIndex(1);
    sys.Tick(3);                                     // 仓库 → 主台流入
    sys.CreateRandomOrder();                         // 随机（种子固定 ⇒ 可复现）
    sys.CheckFeasibility(sys.Orders.Count - 1);
    sys.ExecuteOrder(sys.Orders.Count - 1);
    sys.SetMaster(2);                                // 主从切换（旧主台有资源 ⇒ 会跑切换事件）
    sys.SetSlave(3);
    sys.CreateWorkshop("工坊X", new() { "圆罐" }, new() { "方罐" }, new() { "酒" });
    sys.UpdateWorkshopCapacity(sys.Workshops.Count - 1, 25, 30);
    sys.UpdateProducerRate("陶工甲", "圆罐", 0.5);
    sys.UpdateLiquidProductionRate("酒", 2);
    sys.CreateOrder(new() { new Requirement { Shape = "圆罐" }, new Requirement { Shape = "方罐", Liquid = "蜜" } }, "B");
    sys.CheckAllFeasibility();
    sys.SetAutoCheck(true);
    sys.Tick(2);
    sys.DeleteOrder(0);
    sys.SetFlowSettings(4, 3);
    sys.CreateRandomWorkshop();
    sys.ResetProducerModule();

    // 注入类 API（宿主/测试用）——注意用系统自己的 ID 源
    sys.ClearWorkshops();
    sys.AddWorkshop(new Workshop("裸台", new() { "圆罐" }, new() { "方罐" }, new(), 20, 30,
        sys.JarIds, sys.Vocabulary));
    sys.SetMasterIndex(0);
    sys.Tick(1);
    sys.ClearOrders();
    sys.AddOrder(new Order(new() { new Requirement { Shape = "圆罐" } }, "A", sys.OrderIds, sys.Vocabulary));

    sys.Reset();                                     // 重置世界（日志继续累积）
    sys.SetMasterIndex(0);
    sys.Tick(1);
}

void CheckSolverDifferential()
{
    const int perKind = 2000;                 // 每类 2000 个场景（随机 + 容量）
    int compared = 0, agree = 0, feasibleCount = 0, undetermined = 0, mismatches = 0;

    var randGen = new Random(42);             // 与主测试同一批种子，便于对照
    var randCap = new Random(2026);

    for (int t = 0; t < perKind; t++)
    {
        var (m1, s1, spec1) = Gen(randGen);
        Compare("random", t, m1, s1, spec1);

        var (m2, s2, spec2) = GenCap(randCap);
        Compare("capacity", t, m2, s2, spec2);
    }

    // ------------------------------------------------------------------
    // 定向反例集：随机场景对"剪枝假设"是**盲的**（实测：故意把去重键退化成"只按形状"，
    // 4000 个随机场景一个都没抓到）。因此这里按"每条剪枝假设"手写反例：
    //   T1 攻击「同构去重」的健全性：**两个同形不同液体的满罐** —— 若去重丢掉液体维度，
    //      就会只倒出一种液体，另一条真解被吃掉。
    //   T2 同形同液体（真同构）仍必须可行 —— 防止"剪太狠"。
    //   T3 满罐自耗改形路径（RESHAPE full）必须可行。
    //
    // ⚠️ **敏感度边界（实测，务必记住）**：本门**抓到过一个真实缺陷**（求解器不填终态快照 ⇒
    // B 类静默算错），但下面这三种"故意破坏"**没被抓到**：
    //   ① 记忆化键里去掉"是否已标记"位；② 把"已访问"当成"已证无解"记入；
    //   ③ 去重键退化成"只按形状"（丢掉液体）。
    // 逐个分析后确认：这三种在本系统的结构下**恰好是健全变换**（候选列表每步重新生成 ⇒
    // 同列表内去重不损失可达性；DFS 里"进入时记录"与"失败时记录"等价），因此不是门的漏报。
    // **但结论仍要记牢**：别据此认为门能挡住一切 —— 凡改动记忆化键/去重规则，
    // 必须**同时补一个针对性反例**（就像 T1~T3 那样手写），否则随机场景集对这类假设是盲的。
    // ------------------------------------------------------------------
    Targeted("T1-同形不同液体的两个满罐", () =>
    {
        var m = new Workshop("T1M", new() { "圆罐", "方罐" }, new(), new(), 30, 30);
        m.AddJar(new Jar("圆罐", "酒"));
        m.AddJar(new Jar("圆罐", "蜜"));
        m.AddJar(new Jar("圆罐"));                       // 待改形的空罐
        var s = new Workshop("T1S", new() { "圆罐" }, new(), new(), 30, 30);
        // 需要 满罐(方罐,蜜)：空罐改形要燃料（倒出任一满罐获得），倒液入罐必须是蜜
        return (m, s, new List<Requirement> { new Requirement { Shape = "方罐", Liquid = "蜜" } }, "A");
    });

    Targeted("T2-同形同液体的真同构罐子", () =>
    {
        var m = new Workshop("T2M", new() { "圆罐" }, new(), new(), 30, 30);
        m.AddJar(new Jar("圆罐", "酒"));
        m.AddJar(new Jar("圆罐", "酒"));
        var s = new Workshop("T2S", new() { "圆罐" }, new(), new(), 30, 30);
        return (m, s, new List<Requirement> { new Requirement { Shape = "圆罐", Liquid = "酒" } }, "A");
    });

    Targeted("T3-满罐自耗改形", () =>
    {
        var m = new Workshop("T3M", new() { "圆罐" }, new() { "方罐" }, new(), 30, 30);
        m.AddJar(new Jar("圆罐", "酒"));
        var s = new Workshop("T3S", new() { "圆罐" }, new(), new(), 30, 30);
        return (m, s, new List<Requirement> { new Requirement { Shape = "方罐" } }, "A");
    });

    // 在这个规模的场景集上，"未判定"不该出现：要么是预算异常耗尽，要么是求解器违约。
    // 因此把它也算作失败（否则契约缺口会被"跳过比较"悄悄吞掉）。
    Console.WriteLine(mismatches == 0 && undetermined == 0
        ? $"SOLVER-OK (compared={compared}, agree={agree}, feasible={feasibleCount}, undetermined={undetermined}, targeted=3)"
        : mismatches > 0
            ? $"SOLVER-MISMATCH ({mismatches})"
            : $"SOLVER-MISMATCH ({undetermined} undetermined: 预算异常或求解器违约)");
    if (mismatches > 0 || undetermined > 0) Environment.ExitCode = 1;

    void Compare(string kind, int index, Workshop master, Workshop slave, OrderSpec spec)
    {
        CompareCore(kind + " #" + index, master, slave, spec.Reqs, spec.Type);
    }

    void Targeted(string label, Func<(Workshop M, Workshop S, List<Requirement> Reqs, string Type)> build)
    {
        var (m, s, reqs, type) = build();
        CompareCore(label, m, s, reqs, type);
    }

    void CompareCore(string label, Workshop master, Workshop slave, List<Requirement> reqs, string orderType)
    {
        var production = NewSystem(master, slave, new Order(reqs, orderType));
        var reference = NewSystem(master, slave, new Order(reqs, orderType), new ReferenceSolver());

        var a = production.CheckFeasibility(0);
        var b = reference.CheckFeasibility(0);
        compared++;
        if (a == null || b == null) return;

        // 任一侧"未判定"（预算耗尽 / 求解器违约）⇒ 无可比结论，单独计数（并在汇总处判失败）
        if (a.Undetermined || b.Undetermined) { undetermined++; return; }
        if (a.Feasible == b.Feasible) { agree++; if (a.Feasible) feasibleCount++; return; }

        mismatches++;
        Console.WriteLine($"mismatch detail: [{label}] 需求={string.Join(",", reqs.Select(r => r.Liquid == null ? "空罐(" + r.Shape + ")" : "满罐(" + r.Shape + "," + r.Liquid + ")"))} "
            + $"生产={a.Feasible}({a.CheckMethod}) 参考={b.Feasible} "
            + $"M={DescribeWS(master.CreateSnapshot())} S={DescribeWS(slave.CreateSnapshot())}");
        Console.WriteLine("  参考-主台路径: " + DescribePath(b.MasterPath) + " ；转移罐="
            + (b.TransferJarIds == null ? "-" : string.Join(",", b.TransferJarIds)));
        Console.WriteLine("  参考-从台路径: " + DescribePath(b.SolverPath));
    }

    string DescribePath(List<JarSystem.Core.Orders.SolverStepDto>? path)
    {
        if (path == null || path.Count == 0) return "(空)";
        return string.Join(" → ", path.Select(s =>
            $"{s.Type}@{(s.JarId.HasValue ? "#" + s.JarId : "idx" + s.JarIndex)}"
            + (s.TargetShape != null ? "→" + s.TargetShape : "")
            + (s.TargetLiquid != null ? "+" + s.TargetLiquid : "")
            + (s.CostLiquid != null ? "(耗" + s.CostLiquid + ")" : "")));
    }
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
// 协议契约守卫（--check-protocol）
//
// 为什么需要它：命令名/消息名是**对外契约**，但它们只是 Web 层源码里的字符串字面量 ——
// 把 "getState" 打错成 "getstate"，编译通过、测试通过、快照也没变化，**前端却静默失效**。
//（`verify.bat` 不跑 WebSocket，所以没有任何一步能发现这种错。）
//
// 做法：把 Web 层源码当**文本**扫描，取出 `case "xxx":` 的命令名与 `type = "xxx"` 的消息名，
// 与下面这份**清单**（= 契约本身）比对。清单是刻意硬编码的：
// 将来加命令/消息必须**显式改这里**，从而逼出一次"确认这是有意的契约变更"。
// ============================================================
void CheckProtocol()
{
    var expectedCommands = ProtocolCommands;   // 清单在文件顶部（顶部局部变量，与 DumpSchema 共用）
    var expectedMessages = ProtocolMessages;

    var dispatcher = ReadSource(Path.Combine("src", "JarSystem.Web", "CommandDispatcher.cs"));
    var hub = ReadSource(Path.Combine("src", "JarSystem.Web", "WebSocketHub.cs"));

    var commands = Matches(dispatcher, "case \"([A-Za-z]+)\":");
    var messages = Matches(dispatcher, "type = \"([A-Za-z]+)\"").Concat(Matches(hub, "type = \"([A-Za-z]+)\"")).ToList();

    int failures = 0;
    failures += Compare("command", expectedCommands, commands);
    failures += Compare("message", expectedMessages, messages);

    // 载荷键核对：声明的每个键都必须能在**读取点**找到 ——
    //   · CommandDispatcher.cs：直接读的键（name/index/enabled/…）
    //   · JsonArgs.cs：通过 ParseReqs/JS/JI… 间接读的键（requirements 就在 ParseReqs 里）
    // 注意：错误信息保持**纯 ASCII** —— 它们会进日志并被 findstr 回显到 GBK 控制台，
    // 含中文会导致 findstr "写入错误"（真实踩过）。
    var payloadSources = dispatcher + ReadSource(Path.Combine("src", "JarSystem.Web", "JsonArgs.cs"));
    var payloadKeys = CommandPayloads.SelectMany(c => c.Fields.Select(f => f.Key)).Distinct().ToList();
    var unknownPayloadKeys = payloadKeys.Where(k => !payloadSources.Contains("\"" + k + "\"")).ToList();
    foreach (var k in unknownPayloadKeys)
        Console.WriteLine("protocol payload: KEY-NOT-FOUND " + k + " (not read in CommandDispatcher/JsonArgs)");
    failures += unknownPayloadKeys.Count > 0 ? 1 : 0;

    // 命令载荷表必须覆盖全部命令（不多不少）
    var payloadCommands = CommandPayloads.Select(c => c.Command).ToList();
    var uncoveredCommands = expectedCommands.Where(c => !payloadCommands.Contains(c)).ToList();
    var extraCommands = payloadCommands.Where(c => !expectedCommands.Contains(c)).ToList();
    foreach (var c in uncoveredCommands) Console.WriteLine("protocol payload: COMMAND-WITHOUT-PAYLOAD " + c);
    foreach (var c in extraCommands) Console.WriteLine("protocol payload: PAYLOAD-NOT-IN-COMMANDS " + c);
    failures += uncoveredCommands.Count + extraCommands.Count > 0 ? 1 : 0;
    if (unknownPayloadKeys.Count == 0 && uncoveredCommands.Count == 0 && extraCommands.Count == 0)
        Console.WriteLine($"protocol payload: OK ({CommandPayloads.Length} commands, {payloadKeys.Count} keys)");

    // 回包（服务端 → 客户端）载荷键核对：从源码反向提取 `type = "X", data = new { ... }` 的键集合，
    // 与顶部 MessagePayloads 清单逐项比对。这样"回包键改名/拼错"也会被机器拦下。
    var replyFailures = 0;
    var replyKeyTotal = 0;
    foreach (var (message, expectedKeys) in MessagePayloads)
    {
        replyKeyTotal += expectedKeys.Length;
        var actualKeys = ExtractPayloadKeys(dispatcher, message);
        if (actualKeys == null)
        {
            Console.WriteLine("protocol reply-payload: NOT-FOUND " + message);
            replyFailures++;
            continue;
        }
        replyFailures += Compare("reply-payload " + message, expectedKeys, actualKeys);
    }
    failures += replyFailures > 0 ? 1 : 0;
    if (replyFailures == 0)
        Console.WriteLine($"protocol reply-payload: OK ({MessagePayloads.Length} messages, {replyKeyTotal} keys)");

    // 顺带守一条本项目硬规则：verify.bat 必须**纯 ASCII**。
    // 原因（真实事故）：cmd 在 GBK 代码页下解析 UTF-8 中文会报语法错 —— 双击后窗口一闪即退，看起来就是"打不开"。
    var batPath = Path.Combine("重构报告", "verify.bat");
    if (File.Exists(batPath))
    {
        var bytes = File.ReadAllBytes(batPath);
        int badIndex = -1;
        for (int i = 0; i < bytes.Length; i++) if (bytes[i] >= 0x80) { badIndex = i; break; }
        if (badIndex < 0) Console.WriteLine("script ascii: OK (verify.bat)");
        else
        {
            Console.WriteLine($"script ascii: SCRIPT-NON-ASCII at byte {badIndex} (verify.bat)");
            failures++;
        }
    }

    Console.WriteLine(failures == 0
        ? $"PROTOCOL-OK (commands={expectedCommands.Length}, messages={expectedMessages.Length})"
        : $"PROTOCOL-MISMATCH ({failures})");
    if (failures > 0) Environment.ExitCode = 1;
}

// 比对集合并打印差异（信息保持 ASCII：会被 verify.bat 的 findstr 回显到 GBK 控制台）
int Compare(string label, string[] expected, List<string> actual)
{
    var missing = expected.Where(e => !actual.Contains(e)).ToList();
    var added = actual.Where(a => !expected.Contains(a)).ToList();
    if (missing.Count == 0 && added.Count == 0)
    {
        Console.WriteLine($"protocol {label}: OK ({expected.Length})");
        return 0;
    }
    foreach (var m in missing) Console.WriteLine($"protocol {label}: MISSING " + m + " (in list, not found in source)");
    foreach (var a in added) Console.WriteLine($"protocol {label}: UNEXPECTED " + a + " (in source, not in list)");
    return 1;
}

List<string> Matches(string text, string pattern) =>
    System.Text.RegularExpressions.Regex.Matches(text, pattern)
        .Select(m => m.Groups[1].Value)
        .Distinct()
        .OrderBy(s => s)
        .ToList();

// 从 `type = "<message>", data = new { ... }` 里提取键名（匿名对象体内没有嵌套花括号）。
// 键名判定：片段开头的标识符后面跟 `=`（`key = value`）或紧跟逗号/结尾（`key` 简写，如 `new { orderId, result }`）。
// 返回 null 表示源码里找不到该消息的匿名对象载荷（消息名写错、或载荷改成了 DTO/方法调用）。
List<string>? ExtractPayloadKeys(string source, string message)
{
    var m = System.Text.RegularExpressions.Regex.Match(
        source,
        "type = \"" + System.Text.RegularExpressions.Regex.Escape(message) + "\"\\s*,\\s*data = new\\s*\\{([^{}]*)\\}");
    if (!m.Success) return null;

    var keys = new List<string>();
    foreach (System.Text.RegularExpressions.Match k in System.Text.RegularExpressions.Regex.Matches(
        m.Groups[1].Value, @"(?:^|,)\s*([A-Za-z][A-Za-z0-9]*)\s*(?:=|(?=,|$))"))
        if (!keys.Contains(k.Groups[1].Value)) keys.Add(k.Groups[1].Value);
    return keys;
}

string ReadSource(string relativePath)
{
    if (!File.Exists(relativePath))
    {
        // 用 ASCII 标记（verify.bat 要按它判失败；输出会被 findstr 回显，中文会导致写入错误）
        Console.WriteLine("protocol: SOURCE-NOT-FOUND " + relativePath + " (run from repo root)");
        Environment.ExitCode = 1;
        return "";
    }
    return File.ReadAllText(relativePath);
}

// ============================================================
// 契约快照工具（仅 --dump-contract 模式使用）
// ============================================================

/// <param name="configPath">世界配置文件（json）；null 表示用内核内置默认世界。</param>
void DumpContract(string? configPath = null)
{
    var world = configPath == null ? null : LoadWorldConfig(configPath);

    // 固定场景：默认世界 + 主从选择 + 三个订单（A/B 各含满罐与空罐需求）+ 推进 + 一次执行
    var sys = new JarSystem.Core.JarSystem(world: world);
    sys.SetMasterIndex(0);
    sys.SetSlaveIndex(1);
    sys.CreateOrder(new() { new Requirement { Shape = "圆罐" } }, "A");
    sys.CreateOrder(new() { new Requirement { Shape = "方罐", Liquid = "蜜" }, new Requirement { Shape = "高罐" } }, "B");
    sys.CreateOrder(new() { new Requirement { Shape = "圆罐", Liquid = "酒" } }, "A");

    // 顺序很关键（曾长期是错的）：**先推进再分析**。
    // 此前 Tick 在 CheckFeasibility 之后 ⇒ 主台还没有任何资源 ⇒ 三条订单全部不可行
    // ⇒ solverPath / masterPath 恒为 null ⇒ **路径步骤的 14 个键一直没被快照覆盖**。
    // 现在先 Tick(3) 让主台拿到罐子与液体，订单 0（空罐·圆罐）即可行、路径非空，
    // 快照因此覆盖 step/requirement/operation/type/jarIndex/jarId/jarShape/jarLiquid/
    // targetShape/targetLiquid/costLiquid/reshapeMode/consumed/jarsAfter。
    sys.Tick(3);
    sys.CheckFeasibility(0);
    sys.CheckFeasibility(1);
    sys.ExecuteOrder(0);
    sys.SetSlave(2);

    var lines = new List<string>();
    Flatten("state", sys.GetState(), lines);

    var dir = Path.Combine("重构报告", "_contract");
    Directory.CreateDirectory(dir);
    var file = Path.Combine(dir, configPath == null ? "state_keys.txt" : "state_keys_config.txt");
    File.WriteAllLines(file, lines);
    Console.WriteLine($"contract dumped: {file} ({lines.Count} lines)"
        + (configPath == null ? " [built-in world]" : " [from world config: " + configPath + "]"));
}

// 读世界配置并校验（校验失败即抛：宁可这一步失败，也不要拿"静默缺产出"的世界去做对比）
JarSystem.Core.Config.WorldOptions LoadWorldConfig(string path)
{
    var world = JarSystem.Core.Config.WorldOptionsJson.FromJson(File.ReadAllText(path));
    var report = world.Validate();
    foreach (var w in report.Warnings) Console.WriteLine("world warning: " + w);
    if (!report.IsValid)
    {
        foreach (var e in report.Errors) Console.WriteLine("world error: " + e);
        throw new InvalidOperationException("世界配置校验失败: " + path);
    }
    return world;
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
//      无约束节点（本工具输出 loose=N 计数），那就是契约里没有类型的空洞。
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
        // 2026-09 契约统一：state 与消息共用同一个 feasibilityResult 类型（原先分 10 键投影 + 16 键本体）
        ("FeasibilityResultData", typeof(JarSystem.Core.Orders.FeasibilityResultData)),
        ("WarehouseStateDto", typeof(JarSystem.Core.Services.WarehouseStateDto)),
        ("GlobalStateDto", typeof(JarSystem.Core.Services.GlobalStateDto)),
        ("SolverStepDto", typeof(JarSystem.Core.Orders.SolverStepDto)),
        ("AutoCheckResultDto", typeof(JarSystem.Core.Orders.AutoCheckResultDto)),
        ("WorldMetaDto", typeof(JarSystem.Core.Services.WorldMetaDto))
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
            int loose = CountUnconstrainedNodes(node);
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

    // ---- 顺带生成**前端类型产物**：后端契约 → wwwroot/js/contract.d.ts ----
    // 全局 .d.ts（不是模块）：前端仍是普通 <script>，用 JSDoc /* @type {StateDto} */ 引用即可，
    // 于是"后端改字段名 → 前端在编辑器/tsc --noEmit 阶段报错"，**不需要引入打包器**。
    WriteTypeScriptContracts(types, file);
}

// 由 JSON Schema 生成前端类型（覆盖本项目 schema 用到的子集：对象/数组/字典/可空/标量）
//
// ⚠️ 生成器必须**看懂 properties 与 $ref**（旧实现的缺陷，2026-09 修复）：
//   · 旧实现只在对象节点里认 additionalProperties ⇒ 凡是"有 properties 的嵌套对象"一律塌成
//     Record<string, unknown>，于是 StateDto.workshops 竟然是 Record<string, unknown>[]，
//     而 WorkshopStateDto / JarStateDto / OrderStateDto … 这些接口成了**无人引用的死声明**；
//   · JsonSchemaExporter 对重复出现的类型改用 $ref（本 schema 里是 SolverStepDto 的 4 处），
//     旧实现遇到只有 $ref 的节点直接输出 unknown（表现为 masterPath?: unknown[]）。
//   后果是"后端改字段名 → 前端在 checkJs 下报错"这条承诺**并不成立**（只有命令名/消息名/客户端载荷键成立）。
// 新实现：$ref 按 JSON Pointer 解引用；嵌套对象优先按**结构**还原成具名接口，否则内联对象字面量；
// 并输出**可机器判定**的 degraded 指标（本该能描述却仍塌成 unknown/Record 的节点数），由 verify.bat 拦截
// —— 度量本身也要能被反例推翻，不能再靠肉眼看一个"看起来挺全"的 .d.ts。
void WriteTypeScriptContracts(JsonObject types, string schemaFile)
{
    var tsPath = Path.Combine("src", "JarSystem.Web", "wwwroot", "js", "contract.d.ts");

    // 每个类型自己的 schema 根：$ref 的 JSON Pointer 以它（当前正在导出的类型）为基准
    var rootByName = new Dictionary<string, JsonObject>();
    foreach (var kv in types)
        if (kv.Value is JsonObject o && !o.ContainsKey("$error")) rootByName[kv.Key] = o;

    // 结构签名 → 类型名：把内联的嵌套对象还原成具名接口。
    // 签名相同者**不猜**（歧义时摘掉映射，退化为内联对象字面量，仍然强类型）。
    var nameByShape = new Dictionary<string, string>();
    var ambiguous = new List<string>();
    foreach (var kv in rootByName)
    {
        var sig = Signature(kv.Value, kv.Value);
        if (!nameByShape.TryAdd(sig, kv.Key)) ambiguous.Add(sig);
    }
    foreach (var sig in ambiguous) nameByShape.Remove(sig);

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("// 本文件由 `dotnet run --project test\\JarSystem.Test -c Release -- --dump-schema` 生成，请勿手改。");
    sb.AppendLine("// 来源：后端 DTO 的 [JsonPropertyName]（" + Path.GetFileName(schemaFile) + "）");
    sb.AppendLine("// 用法（前端仍是普通脚本，无需打包器）：在 jsconfig.json 的 checkJs 下用 JSDoc 引用，例如");
    sb.AppendLine("//   /** @type {StateDto} */ let state = null;");
    sb.AppendLine();

    int emitted = 0, degraded = 0;
    foreach (var kv in types)
    {
        if (kv.Value is not JsonObject obj) continue;
        if (obj.ContainsKey("$error")) continue;                                  // 导出失败的类型跳过
        if (!obj.TryGetPropertyValue("properties", out var propsNode) || propsNode is not JsonObject props) continue;

        var required = obj["required"] as JsonArray;
        sb.AppendLine($"interface {kv.Key} {{");
        foreach (var p in props)
        {
            bool isRequired = required != null && required.Any(r => r != null && r.GetValue<string>() == p.Key);
            var (ts, bad) = ToTsType(p.Value, obj, nameByShape);
            degraded += bad;
            sb.AppendLine($"  {p.Key}{(isRequired ? "" : "?")}: {ts};");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        emitted++;
    }

    sb.AppendLine("type CommandName = " + string.Join(" | ", ProtocolCommands.Select(c => "\"" + c + "\"")) + ";");
    sb.AppendLine("type MessageType = " + string.Join(" | ", ProtocolMessages.Select(m => "\"" + m + "\"")) + ";");
    sb.AppendLine();

    // 客户端 → 服务端 的命令载荷（键与类型来自顶部 CommandPayloads 表；守卫核对键确实被 Dispatcher 读取）
    sb.AppendLine("interface RequirementPayload { shape: string; liquid?: string | null; }");
    sb.AppendLine();
    foreach (var (command, iface, fields) in CommandPayloads)
    {
        sb.AppendLine($"/** `{command}` 的载荷 */");
        sb.AppendLine($"interface {iface} {{");
        foreach (var (key, tsType) in fields) sb.AppendLine($"  {key}?: {tsType};");
        sb.AppendLine("}");
        sb.AppendLine();
    }
    sb.AppendLine("/** 命令名 → 载荷类型 的映射（给 send(type, data) 这类帮助函数用） */");
    sb.AppendLine("interface CommandPayloadMap {");
    foreach (var (command, iface, _) in CommandPayloads) sb.AppendLine($"  \"{command}\": {iface};");
    sb.AppendLine("}");
    File.WriteAllText(tsPath, sb.ToString());
    Console.WriteLine($"types dumped: {tsPath} ({emitted + CommandPayloads.Length + 2} interfaces, {File.ReadAllLines(tsPath).Length} lines)");
    // 质量指标（机器判定）：degraded > 0 说明生成器又漏了某类节点（例如将来 schema 里出现新的 $ref 形式）
    if (degraded > 0)
    {
        Console.WriteLine($"types dts: DTS-DEGRADED ({degraded} nodes became unknown)");
        Environment.ExitCode = 1;
    }
    else
    {
        Console.WriteLine($"types dts: OK ({emitted} DTO interfaces fully typed, degraded=0)");
    }
}

// JSON Schema 节点 → TypeScript 类型。返回 (类型文本, 本次"塌掉"的节点数)。
// 塌掉 = 本该能描述（有 properties，或本来是个 $ref）却仍输出 unknown / Record<string, unknown>。
// 反例（**不算塌**）：eventHistory 的元素是"任意对象"，schema 里既无 properties 也无 additionalProperties，
// 那种情况输出 Record<string, unknown> 就是**诚实答案**（与 schema 的 loose=N 计数一一对应）。
(string Ts, int Degraded) ToTsType(JsonNode? node, JsonObject? root, Dictionary<string, string> nameByShape)
{
    if (node is JsonValue bv && bv.TryGetValue<bool>(out var b))
        return (b ? "unknown" : "never", 0);                 // 布尔 true = 任意值
    if (node is not JsonObject o) return ("unknown", 0);
    if (o.Count == 0) return ("unknown", 0);                 // 空对象 = 任意值

    // $ref：按 JSON Pointer（相对本类型根）解引用，再按结构还原成具名接口
    if (o.TryGetPropertyValue("$ref", out var refNode) && refNode is JsonValue rv && rv.TryGetValue<string>(out var pointer))
    {
        var target = ResolvePointer(root, pointer);
        if (target == null) return ("unknown", 1);           // 解不开 = 真塌了
        return ToTsType(target, root, nameByShape);
    }

    var types = new List<string>();
    if (o.TryGetPropertyValue("type", out var tn) && tn != null)
    {
        if (tn is JsonValue tv && tv.TryGetValue<string>(out var single)) types.Add(single);
        else if (tn is JsonArray ta)
            foreach (var t in ta) if (t != null) types.Add(t.GetValue<string>());
    }

    bool nullable = types.Remove("null");
    string? main = types.FirstOrDefault();
    var (result, degraded) = main switch
    {
        "object" => ObjectTs(o, root, nameByShape),
        "array" => ArrayTs(o, root, nameByShape),
        "string" => ("string", 0),
        "integer" or "number" => ("number", 0),
        "boolean" => ("boolean", 0),
        _ => ("unknown", 0)
    };
    return (nullable ? result + " | null" : result, degraded);
}

// 对象节点 → TypeScript 类型（三级优先：具名接口 → 内联对象字面量 → Record）。
(string Ts, int Degraded) ObjectTs(JsonObject o, JsonObject? root, Dictionary<string, string> nameByShape)
{
    // ① 结构与某个具名 DTO 等价 ⇒ 直接用名字（这样 WorkshopStateDto / JarStateDto … 才真的被引用）
    if (nameByShape.TryGetValue(Signature(o, root), out var named)) return (named, 0);

    // ② 有 properties 却没匹配上具名接口 ⇒ 内联对象字面量（仍然强类型，绝不塌成 Record）
    if (o.TryGetPropertyValue("properties", out var pn) && pn is JsonObject props && props.Count > 0)
    {
        var required = o["required"] as JsonArray;
        int degraded = 0;
        var parts = new List<string>();
        foreach (var p in props)
        {
            var (ts, bad) = ToTsType(p.Value, root, nameByShape);
            degraded += bad;
            bool isRequired = required != null && required.Any(r => r != null && r.GetValue<string>() == p.Key);
            parts.Add($"{p.Key}{(isRequired ? "" : "?")}: {ts}");
        }
        return ("{ " + string.Join("; ", parts) + " }", degraded);
    }

    // ③ 纯字典（additionalProperties 是对象）⇒ Record<string, T>
    if (o.TryGetPropertyValue("additionalProperties", out var ap) && ap is JsonObject apObj)
    {
        var (ts, bad) = ToTsType(apObj, root, nameByShape);
        return ($"Record<string, {ts}>", bad);
    }

    // ④ 既无 properties 也无 additionalProperties：**诚实的**弱契约（对应 schema 的 loose 计数）
    return ("Record<string, unknown>", 0);
}

// 数组节点 → TypeScript 类型。
// 注意：schema 里"没有 items 的数组"（如 List<object> 的 path）输出 unknown[] 是**诚实答案**，
// 它与"没有 properties 的对象"一样属于**后端契约自身的空洞**，已经由 schema 的 loose=N 计数覆盖；
// 这里不计入 degraded，否则会把"已知例外"误报成"生成器坏了"（两个指标必须各管各的）。
(string Ts, int Degraded) ArrayTs(JsonObject o, JsonObject? root, Dictionary<string, string> nameByShape)
{
    if (!o.TryGetPropertyValue("items", out var items) || items is null) return ("unknown[]", 0);
    var (ts, degraded) = ToTsType(items, root, nameByShape);
    return ((ts.Contains(" | ") ? "(" + ts + ")" : ts) + "[]", degraded);
}

// 结构签名：忽略 required 与"$ref vs 内联"的写法差异（$ref 先解引用），只保留"属性名 → 结构"。
// 用途有二：把内联嵌套对象还原成具名接口；发现两个 DTO 结构相同 ⇒ 不猜（退化为内联字面量）。
string Signature(JsonNode? node, JsonObject? root, int depth = 0)
{
    if (depth > 8 || node is null) return "?";
    if (node is JsonObject o)
    {
        if (o.TryGetPropertyValue("$ref", out var r) && r is JsonValue rv && rv.TryGetValue<string>(out var ptr))
            return Signature(ResolvePointer(root, ptr), root, depth + 1);
        if (o.TryGetPropertyValue("properties", out var pn) && pn is JsonObject props)
            return "{" + string.Join(",", props.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + ":" + Signature(kv.Value, root, depth + 1))) + "}";
        if (o.TryGetPropertyValue("additionalProperties", out var ap) && ap is JsonObject apObj)
            return "map<" + Signature(apObj, root, depth + 1) + ">";
        if (o.TryGetPropertyValue("type", out var tn) && tn != null) return "T:" + tn.ToJsonString();
        return "{}";
    }
    return node.ToJsonString();
}

// JSON Pointer（形如 #/properties/orders/items/properties/solverPath/items）解引用；解不开返回 null。
JsonNode? ResolvePointer(JsonObject? root, string pointer)
{
    if (root == null || !pointer.StartsWith('#')) return null;
    JsonNode? cur = root;
    foreach (var rawSeg in pointer.Substring(1).Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
        var seg = rawSeg.Replace("~1", "/").Replace("~0", "~");
        cur = cur switch
        {
            JsonObject jo when jo.TryGetPropertyValue(seg, out var next) => next,
            JsonArray ja when int.TryParse(seg, out var idx) && idx >= 0 && idx < ja.Count => ja[idx],
            _ => null
        };
        if (cur == null) return null;
    }
    return cur;
}

// 统计"无约束"的 schema 节点 —— 即"什么值都能通过"的地方，它们就是契约里的空洞：
//   1) 布尔 true（JSON Schema 里表示"任意值"）；
//   2) 空对象 {}（同义）；
//   3) {"type":"object"} 但既无 properties 也无 additionalProperties:false ⇒ 成员完全不受约束；
//   4) {"type":"array"} 但没有 items ⇒ 元素不受约束。
// 注意：只写 {"type":"string"} 这类叶子**不算**空洞（它约束了类型）。
//
// 首版实现只认 1) 和 2)，于是 {"type":"object"} 形式的空洞被漏判、报出 loose=0（假阴性）。
// 这条经验值得记住：度量本身也要能被反例推翻 —— 预测 StateDto 有 2 处空洞、实测为 0，才发现的。
int CountUnconstrainedNodes(System.Text.Json.Nodes.JsonNode? node)
{
    if (node is null) return 0;
    if (node is System.Text.Json.Nodes.JsonValue bv && bv.TryGetValue<bool>(out var b)) return b ? 1 : 0;
    if (node is System.Text.Json.Nodes.JsonArray ja)
    {
        int c = 0;
        foreach (var item in ja) c += CountUnconstrainedNodes(item);
        return c;
    }
    if (node is not System.Text.Json.Nodes.JsonObject o) return 0;
    if (o.Count == 0) return 1;

    int loose = 0;
    if (TypeIncludes(o, "object"))
    {
        bool hasProps = o.TryGetPropertyValue("properties", out var props)
                        && props is System.Text.Json.Nodes.JsonObject po && po.Count > 0;
        bool addl = o.TryGetPropertyValue("additionalProperties", out var ap) && ap is not null
                    && !(ap is System.Text.Json.Nodes.JsonValue apv && apv.TryGetValue<bool>(out var apb) && apb);
        if (!hasProps && !addl) loose++;
    }
    if (TypeIncludes(o, "array") && !o.ContainsKey("items")) loose++;

    foreach (var key in new[] { "properties", "$defs", "definitions" })
        if (o.TryGetPropertyValue(key, out var sub) && sub is System.Text.Json.Nodes.JsonObject so)
            foreach (var kv in so) loose += CountUnconstrainedNodes(kv.Value);
    if (o.TryGetPropertyValue("items", out var items)) loose += CountUnconstrainedNodes(items);
    if (o.TryGetPropertyValue("additionalProperties", out var apn) && apn is System.Text.Json.Nodes.JsonObject)
        loose += CountUnconstrainedNodes(apn);
    foreach (var key in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
        if (o.TryGetPropertyValue(key, out var arr) && arr is System.Text.Json.Nodes.JsonArray aa)
            foreach (var item in aa) loose += CountUnconstrainedNodes(item);
    return loose;
}

// `type` 可能是字符串，也可能是数组（如 ["integer","null"]）；这里判断它是否包含某个类型名。
bool TypeIncludes(System.Text.Json.Nodes.JsonObject o, string typeName)
{
    if (!o.TryGetPropertyValue("type", out var tn) || tn is null) return false;
    if (tn is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)) return s == typeName;
    if (tn is System.Text.Json.Nodes.JsonArray a)
        foreach (var x in a)
            if (x is System.Text.Json.Nodes.JsonValue xv && xv.TryGetValue<string>(out var xs) && xs == typeName) return true;
    return false;
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

/// <summary>
/// 替身求解器（S21）：记录调用次数后**委托**给真实实现。
/// 用途是验证"门面可以注入求解器"这条缝真正贯通 —— 它是"换算法"的前提，
/// 也是将来真换算法时的可用骨架（替换 <c>_inner</c> 即可）。
/// </summary>
sealed class CountingSolver : JarSystem.Core.Abstractions.ISolver
{
    private readonly JarSystem.Core.Solver.StateSpaceSolver _inner = new();

    /// <summary>被调用次数（一次可行性分析可能调用多次：划分搜索里每侧各一次）。</summary>
    public int Calls { get; private set; }

    public JarSystem.Core.Solver.SolverResult Solve(
        List<Requirement> requirements,
        JarSystem.Core.Solver.WorkshopInfo workshopInfo,
        List<JarState> jars,
        Dictionary<string, int> liquids,
        int transferBudget = int.MaxValue)
    {
        Calls++;
        return _inner.Solve(requirements, workshopInfo, jars, liquids, transferBudget);
    }
}

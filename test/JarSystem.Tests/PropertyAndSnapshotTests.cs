using System.Text.Json;
using FsCheck.Xunit;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using Xunit;

using CoreSystem = JarSystem.Core.JarSystem;

namespace JarSystem.Tests;

/// <summary>
/// P3：性质测试（FsCheck）+ 审批/快照测试（**自实现，不依赖 Verify**）。
///
/// 关于 Verify：33.x 起它是赞助门槛软件（SponsorCheck `SC021` 会让构建直接失败，除非声明赞助或豁免）。
/// 本项目立场是**零依赖**，因此审批测试自实现 —— 行为等价且没有许可证负担：
///   · 基线：<c>Approvals/DefaultWorldOptions.approved.json</c>（提交进仓库）
///   · 不一致时写出 <c>DefaultWorldOptions.received.json</c> 并让测试失败
///   · 人工比对后把 received 覆盖为 approved（这就是"审批"这一步）
/// </summary>
public class PropertyAndSnapshotTests
{
    /// <summary>
    /// 核心不变量：**可行性与执行双向一致** —— 判定可行 ⇒ 执行必须成功；判定不可行 ⇒ 执行必须失败。
    ///
    /// 这正是不变量里最本质的一条（旧 runner 的 2000/3000 用例检查的就是它）。
    /// 写成"性质 + 收缩"而不是手写循环：输入是种子，失败时 FsCheck 会**收缩到最小反例**，
    /// 把打印出的 <c>seedRaw</c> 写进 <c>[Property(Replay = "...")]</c> 即可复现同一次失败。
    ///
    /// 返回类型刻意用 <c>bool</c>（而不是链式 <c>ToProperty().Label()</c>）：
    /// 那是 FsCheck 的 fluent 扩展，命名空间随版本变过（3.x 移到了 `FsCheck.Fluent`）——
    /// 这里只用**最保守的 API**，避免版本差异导致编译失败。
    ///
    /// 为什么不写成"罐子数守恒"：订单**交付**会减少工作台里的罐子，
    /// 那条等式需要把交付数一并算进来才成立 —— 在这里断言它反而容易写成错的期望。
    /// </summary>
    [Property(MaxTest = 200)]
    public bool FeasibilityAndExecution_AreConsistent(byte seedRaw)
    {
        var rnd = new Random(seedRaw);
        int seed = rnd.Next();

        var master = new Workshop("M", new() { "圆罐", "方罐" }, new(), new(), 30, 30);
        var slave = new Workshop("S", new() { "方罐" }, new(), new(), 30, 30);

        // 随机初始罐子（空罐 / 满罐各若干）
        int jars = 1 + seed % 4;
        for (int i = 0; i < jars; i++)
        {
            var shape = ((seed >> i) & 1) == 0 ? "圆罐" : "方罐";
            var liquid = ((seed >> (i + 3)) & 1) == 0 ? null : Constants.LIQUIDS[0];
            master.AddJar(new Jar(shape, liquid));
        }

        var sys = new CoreSystem();
        sys.ClearWorkshops();
        sys.AddWorkshop(master);
        sys.AddWorkshop(slave);
        sys.SetMasterIndex(0);
        sys.SetSlaveIndex(1);

        sys.CreateOrder(
            new() { new Requirement { Shape = "圆罐" }, new Requirement { Shape = "方罐" } },
            Constants.OrderType.A);

        var feasibility = sys.CheckFeasibility(0);
        var execution = sys.ExecuteOrder(0);

        return feasibility != null && feasibility.Feasible == execution.Success;
    }

    /// <summary>
    /// 审批测试：把**内置世界的数据形态**（<see cref="WorldOptions.Default"/> 经 JSON 往返）钉成可读基线。
    ///
    /// 为什么选它而不是 `GetState()`：`state` 含时间戳等易变字段，而世界配置**没有易变字段** ⇒ 基线稳定、diff 一眼看懂。
    /// 与 223 行文本快照互补：那份锁"对外状态的键与值"，这份锁"世界是怎么装配出来的"。
    /// </summary>
    [Fact]
    public void DefaultWorldOptionsIsStable()
    {
        var json = JsonSerializer.Serialize(
            WorldOptionsJson.FromJson(WorldOptionsJson.ToJson(WorldOptions.Default)),
            new JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default) { WriteIndented = true });

        var dir = Path.Combine(RepoRoot(), "test", "JarSystem.Tests", "Approvals");
        Directory.CreateDirectory(dir);
        var approvedPath = Path.Combine(dir, "DefaultWorldOptions.approved.json");
        var receivedPath = Path.Combine(dir, "DefaultWorldOptions.received.json");

        if (!File.Exists(approvedPath))
        {
            // 首次运行：建立基线（等同 Verify 的"首次生成 *.verified.txt"）
            File.WriteAllText(approvedPath, json);
            Assert.True(true);
            return;
        }

        var approved = File.ReadAllText(approvedPath);
        if (approved == json)
        {
            if (File.Exists(receivedPath)) File.Delete(receivedPath);   // 一致则清掉上一次的残留
            return;
        }

        File.WriteAllText(receivedPath, json);
        Assert.Fail(
            "内置世界与审批基线不一致。" + Environment.NewLine
            + "  基线: " + approvedPath + Environment.NewLine
            + "  新值: " + receivedPath + Environment.NewLine
            + "确认新值无误后，把 received 覆盖到 approved（这就是审批动作）。");
    }

    /// <summary>
    /// 仓库根：从当前目录与应用目录向上找 `config/world.json` 所在目录。
    /// （xUnit v3 运行器的 cwd 不是仓库根 —— 这条踩过一次；此处与宿主查找策略一致。）
    /// </summary>
    private static string RepoRoot()
    {
        var candidates = new List<string> { Directory.GetCurrentDirectory() };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            candidates.Add(dir.FullName);
            dir = dir.Parent;
        }
        var root = candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "config", "world.json")));
        if (root != null) return root;
        throw new InvalidOperationException("找不到仓库根（应有 config/world.json）");
    }
}

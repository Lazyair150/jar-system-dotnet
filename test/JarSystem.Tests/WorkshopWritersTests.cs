using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 工作台写入主体的清单门（谁有权改真实工作台）。
///
/// 背景：求解器只做推理、不碰真实工作台；真正改世界的是三类**主体** ——
/// 玩家的手工命令、执行器（按计划/贪心）、以及换台与回溯。它们共用同一组
/// <c>Workshop</c> 端口（MarkJar / PourIn / PourOut / TryReshape /
/// AddJar / RemoveJar / TransferIn / TransferOut / AddLiquid / TryReceiveLiquid）。
///
/// 这组测试钉三件事，都是"结构而不是数量"，所以不会随重构天天漂：
/// 1 <b>求解器目录里一处端口调用都不许有</b> —— 求解器一旦能改真实工作台，
///   "先算后做"的边界就没了，"可行/不可行"和"世界变了"会互相污染。
/// 2 <b>改工作台的文件必须在清单里</b> —— 新增一个主体时这里会红，
///   逼作者当场回答"它凭什么能改世界"，而不是悄悄多一条写入路径。
/// 3 <b>`Jar` 的变更方法必须 internal</b> —— 端口是唯一的门，
///   若哪天有人把 `Mark` / `Reshape` 放开成 public，绕过工作台直接改罐子就变成合法操作。
/// </summary>
public class WorkshopWritersTests
{
    /// <summary>所有会改真实工作台的端口名。</summary>
    private static readonly string[] Ports =
    {
        "PourIn", "PourOut", "TryReshape", "MarkJar",
        "AddJar", "RemoveJar", "TransferIn", "TransferOut",
        "AddLiquid", "TryReceiveLiquid"
    };

    /// <summary>允许改工作台的文件（相对 src/JarSystem.Core/）。新增主体必须登记并说明理由。</summary>
    private static readonly Dictionary<string, string> AllowedWriters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Commands/JarCommands.cs"] = "玩家的手工命令：所有操作都经 Workshop 端口并检查返回值",
        ["Executor/SolverStepApplier.cs"] = "旧执行器：把求解器步骤逐条施加到真实工作台，MATCH 也经 Workshop.MarkJar",
        ["World/Execution/WorldPlanStepApplier.cs"] = "新世界计划步骤执行器：按不可变 WorldPlanStep 调用 Workshop 领域端口",
        ["Executor/GreedyPlanBuilder.cs"] = "执行器：求解器未判定时的贪心兜底",
        ["Orders/OrderEvent.cs"] = "订单交付：把交付罐从交付台上取下",
        ["Orders/SlaveRollback.cs"] = "失败/结束时的跨台回流（唯一被回滚的东西）",
        ["Orders/Switching/ResourceTransferrer.cs"] = "换台迁徙：把旧台资源搬到新台",
        ["Orders/Switching/SwitchCases.cs"] = "换台迁徙的重组阶段（把游离液体灌进旧台基础形状的空罐）",
        ["Process/Execution/OrderExecutionSession.cs"] = "过程化的订单执行（异步轨），与同步执行器同一套端口",
        ["Production/Warehouse.cs"] = "仓库 → 主台的流入（规则.txt「中央仓库单向流动」的唯一出口）"
    };

    private static string CoreRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JarSystem.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "JarSystem.Core");
    }

    private static IEnumerable<string> Sources(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") == false)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") == false);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// 这些**接收者**不是工作台，而是求解器自己的纯值或工厂方法 —— 文本匹配必须放过它们，
    /// 否则门会报一堆假阳性（第一版就是这么红的：`st.AddLiquid(...)` 是 `SolverState` 的方法，
    /// `WorkshopOperation.PourOut()` 是枚举工厂）。
    /// 这是文本扫描的固有局限：精确做法是用 Roslyn 分析器按**类型**判定。
    /// 在换分析器之前，这里用一个显式的接收者白名单把噪声挡在门外，并要求
    /// "白名单只放纯值/工厂，不放任何工作台变量"。
    /// </summary>
    private static readonly HashSet<string> NotAWorkshop = new(StringComparer.Ordinal)
    {
        "st", "state", "s", "js", "list", "ops", "cands",
        "WorkshopOperation", "WorkshopRulebook", "JarFacts"
    };

    private static IEnumerable<(string File, int Line, string Port, string Text)> PortCalls(string root)
    {
        var rx = new Regex(@"(?<recv>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*(" + string.Join("|", Ports) + @")\s*\(",
            RegexOptions.Compiled);
        foreach (var path in Sources(root))
        {
            // 端口自己的定义（Domain/Workshop.cs）不算"调用点"。
            var rel = Relative(root, path);
            if (rel.Equals("Domain/Workshop.cs", StringComparison.OrdinalIgnoreCase)) continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;   // 注释不算
                var m = rx.Match(line);
                if (!m.Success) continue;
                if (NotAWorkshop.Contains(m.Groups["recv"].Value)) continue;
                yield return (rel, i + 1, m.Groups["Port"].Value, line.Trim());
            }
        }
    }

    [Fact]
    public void SolverNeverTouchesARealWorkshop()
    {
        // 求解器的输入是 WorkshopInfo / JarState（纯值），不是 Workshop。
        // 一旦这里出现端口调用，就说明有人把"算"和"做"接到了一起。
        var root = CoreRoot();
        var hits = PortCalls(root)
            .Where(c => c.File.StartsWith("Solver/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(hits.Count == 0,
            "求解器目录里出现了工作台端口调用（求解器必须只做推理）：\n" +
            string.Join("\n", hits.Select(h => $"  src/JarSystem.Core/{h.File}:{h.Line}  {h.Port}  {h.Text}")));
    }

    [Fact]
    public void EveryWorkshopWriterIsOnTheList()
    {
        var root = CoreRoot();
        var calls = PortCalls(root).ToList();
        Assert.True(calls.Count > 20, $"只扫到 {calls.Count} 处端口调用，扫描疑似失效（防空转）");

        var unknown = calls
            .Where(c => !AllowedWriters.ContainsKey(c.File))
            .GroupBy(c => c.File)
            .OrderBy(g => g.Key)
            .ToList();

        Assert.True(unknown.Count == 0,
            "出现了未登记的工作台写入主体 —— 请在 WorkshopWritersTests.AllowedWriters 里登记，" +
            "并说明它凭什么可以改真实工作台：\n" +
            string.Join("\n", unknown.Select(g =>
                $"  src/JarSystem.Core/{g.Key}（{g.Count()} 处，首个：第 {g.First().Line} 行 {g.First().Port}）")));
    }

    [Fact]
    public void TheListHasNoStaleEntries()
    {
        // 登记了但已经不再写工作台的文件要删掉登记：清单只增不减会慢慢变成噪音，
        // 而噪音清单等于没有清单。
        var root = CoreRoot();
        var writers = PortCalls(root).Select(c => c.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = AllowedWriters.Keys.Where(k => !writers.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "这些文件已不再调用工作台端口，请从 AllowedWriters 里删掉：\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void JarMutationStaysInternalSoThePortRemainsTheOnlyDoor()
    {
        // 端口是唯一的门。Jar 的变更方法一旦 open，绕过 Workshop 直接改罐子就成了合法操作，
        // 于是"一罐一操作 / 基础标记 / 禁令 / 容量"这些判定全部可以被跳过。
        var root = CoreRoot();
        var jarFile = Path.Combine(root, "Domain", "Jar.cs");
        Assert.True(File.Exists(jarFile), "找不到 Domain/Jar.cs");

        var mutators = new[] { "Mark", "Unmark", "PourIn", "ClearLiquid", "RestoreLiquid", "Reshape" };
        var text = File.ReadAllText(jarFile);
        var offenders = new List<string>();
        foreach (var name in mutators)
        {
            // 只看声明行（允许前面有 internal / void 等修饰），出现 "public" 就是破口。
            var rx = new Regex(@"^\s*(public\s+)?[^/\n]*\b" + name + @"\s*\(",
                RegexOptions.Multiline);
            foreach (Match m in rx.Matches(text))
            {
                var line = m.Value.Trim();
                if (line.StartsWith("public", StringComparison.Ordinal)) offenders.Add($"{name}: {line}");
            }
        }
        Assert.True(offenders.Count == 0,
            "Jar 的变更方法必须是 internal，否则可以绕过工作台端口直接改罐子：\n  " +
            string.Join("\n  ", offenders));
    }
}

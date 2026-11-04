using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **移植不变量的执行者** —— 把"核心不依赖宿主、不被墙钟驱动"从文档约定变成构建期事实。
///
/// 背景：这个核心将来要装进另一个游戏引擎（引擎给我们帧、回调、画面与适配，
/// 不给我们确定性仿真、可重放命令日志、领域投影）。所以"能不能移植"不是一句评价，
/// 而是一组可以被机器否证的禁令：
///
/// 1. 核心不引用任何引擎 / UI / 宿主框架（`UnityEngine`、`Godot`、`Avalonia`、
///    `Microsoft.AspNetCore` …）。
/// 2. 核心不自己开线程、不用定时器（`Task.Run`、`Parallel.`、`new Thread`、
///    `Task.Delay`、`Thread.Sleep`）—— 推进世界的只有 `Tick`，而且是外部驱动。
/// 3. 核心读墙钟只许在"记录"位置，不许在"判定"位置。
/// 4. 核心的随机只许来自门面持有的那条 `Rng`（给定种子即确定）。
///
/// **为什么第 3、4 条不是"禁死"而是"登记"**：现在确实有几处墙钟（`JarSystem.Now`
/// 决定事件历史里的 `timestamp`；`FeasibilityChecker` 记耗时）和两处无种子默认值
/// （不给 `randomSeed` 时）。它们都不参与判定，删掉会改契约或改调用方语义。
/// 所以规则是：**允许存在，但必须一笔一笔登记写理由**；没登记的、或者条数变多的，都会红。
///
/// 条数而不是行号：行号会随后续重构天天变，登记表会变成噪音，然后被人一把删掉；
/// 条数只在"有人新加了一处"时变，而那一刻正是应该有人来解释理由的时刻。
///
/// 这一层守的是**方向**，不是风格：任何一条被违反，代价都是"搬到新引擎里跑出来不一样"，
/// 而那种不一致不会在单元测试里自己冒出来。
/// </summary>
public class PortabilityTests
{
    /// <summary>一条禁令：编号、人话、正则。</summary>
    private sealed record Rule(string Id, string Meaning, Regex Pattern);

    private static readonly Rule[] Rules =
    [
        new("WallClock",
            "读墙钟（只许出现在记录位置，不许出现在判定位置）",
            new Regex(
                @"\b(?:DateTime|DateTimeOffset)\s*\.\s*(?:Now|UtcNow)\b",
                RegexOptions.Compiled)),

        new("Timer",
            "定时器 / 休眠（推进世界的只能是 Tick）",
            new Regex(
                @"\bStopwatch\b|\bTask\s*\.\s*Delay\b|\bThread\s*\.\s*Sleep\b" +
                @"|\bEnvironment\s*\.\s*TickCount(?:64)?\b",
                RegexOptions.Compiled)),

        new("UnseededRandom",
            "无种子随机（给定种子就必须可复现）",
            new Regex(@"new\s+Random\s*\(\s*\)", RegexOptions.Compiled)),

        new("EngineType",
            "引用引擎 / UI / 宿主框架（核心必须两个宿主通用）",
            new Regex(
                @"\busing\s+(?:UnityEngine|Godot|Avalonia|System\.Windows(?:\.Forms)?" +
                @"|Microsoft\.AspNetCore|System\.Drawing|Microsoft\.Xna|MonoGame)\b",
                RegexOptions.Compiled)),

        new("Threading",
            "核心自己开并发（时序就成了运行时性质，回放不可复现）",
            new Regex(
                @"\bTask\s*\.\s*Run\b|\bParallel\s*\.\s*\w|\bnew\s+Thread\s*\(" +
                @"|\bThreadPool\s*\.",
                RegexOptions.Compiled)),
    ];

    /// <summary>
    /// 登记表：`(相对路径, 规则)` → (期望条数, 理由)。
    ///
    /// **加一行之前先问：能不能不读它？** 能，就不要加。
    /// </summary>
    private static readonly Dictionary<(string Path, string Rule), (int Count, string Why)> Allowed =
        new()
        {
            [("JarSystem.cs", "WallClock")] = (6,
                "`internal DateTimeOffset Now` 决定事件历史里的 timestamp（记录，不判定）；" +
                "另有五处 `if (!Replaying) Now = DateTimeOffset.UtcNow;` 是回放时**刻意不刷新**的守卫" +
                "（订单执行/迁徙、主台切换、Record 的统一意图时间）。"),

            [("JarSystem.Runtime.cs", "WallClock")] = (1,
                "tick 入口统一记录本批世界推进开始时间；回放时跳过读取，由命令日志恢复。"),

            [("Commands/JarCommands.cs", "WallClock")] = (4,
                "四条命令的 `Timestamp`：写进命令日志的时间戳，回放读日志不读钟。"),

            [("Orders/FeasibilityChecker.cs", "WallClock")] = (2,
                "可行性求解的耗时统计（写进 SolverStats，已标 volatile）；不影响 Feasible 结论。"),

            [("JarSystem.cs", "UnseededRandom")] = (1,
                "`randomSeed` 为 null 时的默认值：调用方没给种子，就没有确定性可保。"),

            [("Executor/JarSelector.cs", "UnseededRandom")] = (1,
                "`rng ?? new Random()` 的兜底分支：门面总会注入 Rng，这里只是裸用时的默认值。"),
        };

    [Fact]
    public void CoreDoesNotReachForTheHostClockOrEngine()
    {
        var unregistered = new List<string>();
        var files = 0;
        var hits = 0;

        foreach (var (relPath, text) in CoreSources())
        {
            files++;
            var lines = text.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComment(lines[i]);
                if (code.Length == 0)
                    continue;

                foreach (var rule in Rules)
                {
                    if (!rule.Pattern.IsMatch(code))
                        continue;

                    hits++;
                    if (Allowed.ContainsKey((Normalize(relPath), rule.Id)))
                        continue;

                    unregistered.Add(
                        $"src/JarSystem.Core/{Normalize(relPath)}:{i + 1}  [{rule.Id}] {rule.Meaning}" +
                        $"\n      {code.Trim()}");
                }
            }
        }

        Assert.True(files > 50, $"只扫到 {files} 个 .cs 文件，路径解析可能错了，这个测试会变成空转。");
        Assert.True(hits > 0,
            "一条都没扫到 —— 说明正则或注释剥离坏了，这个测试正在假装通过。");
        Assert.True(unregistered.Count == 0,
            "核心碰到了宿主 / 墙钟 / 并发（未登记 " + unregistered.Count + " 处）：\n  " +
            string.Join("\n  ", unregistered.Take(20)) +
            (unregistered.Count > 20 ? "\n  …" : "") +
            "\n\n  —— 能不用就别用；确实必须用，就在 PortabilityTests.Allowed 里登记条数与理由。");
    }

    [Fact]
    public void AllowlistMatchesRealityExactly()
    {
        // 统计真实命中数
        var actual = new Dictionary<(string Path, string Rule), int>();
        var scanned = 0;

        foreach (var (relPath, text) in CoreSources())
        {
            scanned++;
            foreach (var line in text.Split('\n'))
            {
                var code = StripComment(line);
                if (code.Length == 0)
                    continue;

                foreach (var rule in Rules)
                {
                    if (!rule.Pattern.IsMatch(code))
                        continue;
                    var key = (Normalize(relPath), rule.Id);
                    actual[key] = actual.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }
        }

        Assert.True(scanned > 50, $"只扫到 {scanned} 个 .cs 文件，路径解析可能错了。");

        var stale = Allowed
            .Where(kv => !actual.ContainsKey(kv.Key))
            .Select(kv => $"src/JarSystem.Core/{kv.Key.Path}  [{kv.Key.Rule}]")
            .ToList();

        Assert.True(stale.Count == 0,
            "这些登记已经对不上任何代码了，请从 PortabilityTests.Allowed 里删掉：\n  " +
            string.Join("\n  ", stale));

        var drifted = Allowed
            .Where(kv => actual.TryGetValue(kv.Key, out var n) && n != kv.Value.Count)
            .Select(kv =>
                $"src/JarSystem.Core/{kv.Key.Path}  [{kv.Key.Rule}] 登记 {kv.Value.Count} 条，实际 " +
                $"{actual[kv.Key]} 条 —— {kv.Value.Why}")

            .ToList();

        Assert.True(drifted.Count == 0,
            "登记的条数和代码不符（新加的那一处需要一条理由，删掉的那一处请更新登记）：\n  " +
            string.Join("\n  ", drifted));
    }

    // ------------------------------------------------------------------

    /// <summary>相对 <c>src/JarSystem.Core</c> 的路径，统一成正斜杠（跨平台与登记表一致）。</summary>
    private static string Normalize(string relPath) =>
        relPath.Replace('\\', '/');

    /// <summary>
    /// 剥掉行尾注释 —— 文档里提到 <c>DateTimeOffset.UtcNow</c> 不该算违规。
    /// 只在引号外认 <c>//</c>，避免把 <c>"http://"</c> 这类字符串截断。
    /// </summary>
    private static string StripComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return string.Empty; // `///` 与 `//` 注释行整行跳过

        var inString = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            var c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            if (!inString && c == '/' && line[i + 1] == '/')
                return line[..i];
        }

        return line;
    }

    /// <summary>从测试程序集目录往上找到仓库根（含 <c>JarSystem.slnx</c> 的那一层）。</summary>
    private static IEnumerable<(string Path, string Text)> CoreSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JarSystem.slnx")))
            dir = dir.Parent;

        Assert.True(dir != null, "找不到仓库根（没有 JarSystem.slnx）。");

        var core = Path.Combine(dir!.FullName, "src", "JarSystem.Core");
        Assert.True(Directory.Exists(core), "找不到 src/JarSystem.Core：" + core);

        foreach (var path in Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(core, path);
            if (rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;

            yield return (rel, File.ReadAllText(path));
        }
    }
}

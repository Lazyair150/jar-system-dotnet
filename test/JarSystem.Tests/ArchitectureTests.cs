using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **分层规则的执行者** —— 把"圈层"从文档约定变成构建期事实。
///
/// 背景：这个工程曾经有 **7 组双向依赖环**（`Domain↔Abstractions`、`Domain↔Rules`、
/// `Domain↔Services`、`Orders↔Abstractions`、`Orders↔Solver`、`Orders↔Services`、
/// `Solver↔Abstractions`）。单工程内互相 `using` 编译得过，所以没有任何东西会报警；
/// 环只会在"想单独复用一层"或"想知道改动会影响谁"的时候以维护成本的形式显现。
///
/// 现在已经清成零环。但**零环是一次手术的结果，不是结构**：下一个人（或下一次改动）
/// 只要再加一个 `using` 就能把环悄悄装回去。所以这里把顺序写成一张表，
/// 由测试守着 —— 任何向上引用都会在这里失败，并指出是哪一行、违反了哪一格。
///
/// 规则只有一句：**一个命名空间只许引用排位比它低的命名空间**。
/// 这条规则比"禁止环"更强也更有用：它直接给出一个可以照着写代码的方向感。
/// </summary>
public class ArchitectureTests
{
    /// <summary>
    /// 圈层表（数字小 = 更靠底层）。顺序不是审美，是被真实依赖推出来的：
    /// 例如 `Commands` 必须排在 `Orders` 之前，因为 `Orders/OrderEvent` 要驱动 `CommandExecutor`；
    /// 而 `Services` 排在 `Process` 之后，因为 `ProductionService` 要跑 `Process` 的管线。
    ///
    /// **新增命名空间必须在这里登记**（`EveryNamespaceIsRanked` 会拦住漏登记的），
    /// 登记的同时也就回答了"它属于哪一层"。
    /// </summary>
    private static readonly Dictionary<string, int> Layer = new(StringComparer.Ordinal)
    {
        ["Config"] = 0,
        ["Domain"] = 1,
        ["Abstractions"] = 2,
        // Presentation 与 Abstractions 同类：**纯契约词汇 + 有界缓冲，不依赖任何层**。
        // 排在这里而不是顶层的理由：`World` 要把信号投影成帧（`World/BusPresentationBridge`），
        // 若把 Presentation 放在 World 之上，这条边就成了向上引用。
        ["Presentation"] = 3,
        ["Rules"] = 4,
        ["Production"] = 5,
        ["Solver"] = 6,
        ["Commands"] = 7,
        ["Orders"] = 8,
        ["Process"] = 9,
        ["Services"] = 10,
        ["Sessions"] = 11,
        ["Executor"] = 12,
        ["World"] = 13,
        ["Bootstrap"] = 14,
    };

    private static readonly Regex NamespaceRx =
        new(@"^namespace\s+JarSystem\.Core\.(\w+)\s*;", RegexOptions.Multiline);

    private static readonly Regex UsingRx =
        new(@"^using\s+JarSystem\.Core\.(\w+)\s*;", RegexOptions.Multiline);

    [Fact]
    public void CoreLayersOnlyDependDownward()
    {
        var violations = new List<string>();
        var files = 0;

        foreach (var (path, text) in CoreSources())
        {
            files++;
            var ns = NamespaceRx.Match(text);
            if (!ns.Success)
                continue;
            var self = ns.Groups[1].Value;
            var selfRank = Layer.TryGetValue(self, out var r) ? r : (int?)null;
            if (selfRank is null)
                continue; // 由 EveryNamespaceIsRanked 报

            foreach (Match u in UsingRx.Matches(text))
            {
                var target = u.Groups[1].Value;
                if (target == self || !Layer.TryGetValue(target, out var targetRank))
                    continue;

                if (targetRank >= selfRank.Value)
                {
                    var line = LineOf(text, u.Index);
                    violations.Add(
                        $"{Path.GetFileName(path)}:{line}  `{self}`(L{selfRank}) 引用了 `{target}`(L{targetRank})" +
                        " —— 只许向下，不许同层或向上");
                }
            }
        }

        Assert.True(files > 50, $"只扫到 {files} 个 .cs 文件，路径解析可能错了，这个测试会变成空转。");
        Assert.True(violations.Count == 0,
            "分层规则被破坏（数量 " + violations.Count + "）：\n  " +
            string.Join("\n  ", violations.Take(20)) +
            (violations.Count > 20 ? "\n  …" : ""));
    }

    [Fact]
    public void EveryNamespaceIsRanked()
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in CoreSources())
        {
            var ns = NamespaceRx.Match(text);
            if (ns.Success)
                seen.Add(ns.Groups[1].Value);
        }

        Assert.True(seen.Count > 5, $"只扫到 {seen.Count} 个命名空间，路径解析可能错了。");

        var unranked = seen.Where(n => !Layer.ContainsKey(n)).ToList();
        Assert.True(unranked.Count == 0,
            "这些命名空间没有登记圈层（新增层时请同时在 ArchitectureTests.Layer 里回答它属于哪一层）：" +
            string.Join(", ", unranked));

        var stale = Layer.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "这些命名空间已经不存在了，请从 Layer 表里删掉：" + string.Join(", ", stale));
    }

    // ------------------------------------------------------------------

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n')
                line++;
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
            // 构建产物不是源码：obj/ 里是生成物，bin/ 里是副本
            var rel = Path.GetRelativePath(core, path);
            if (rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;

            yield return (path, File.ReadAllText(path));
        }
    }
}

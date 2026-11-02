using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JarSystem.Core.Config;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 关卡文件（<c>config/levels/*.json</c>）的契约测试。
///
/// 为什么放在**核心测试**里而不是宿主测试里：关卡文件是**数据**，它唯一必须成立的事是
/// "里面那份 world 能过 <see cref="WorldOptions.Validate"/>，并且手感数值是设计要的那组"。
/// 至于"宿主怎么发现它们、<c>--level N</c> 怎么用" —— 那是 <c>GameHost/LevelCatalog.cs</c> 的事，
/// 由宿主自己保证（那里任何一关坏了会**拒绝启动**，比测试更早暴露）。
///
/// 钉住四件事：
/// 1 关卡目录里至少有六关，且文件名排序 = 游玩顺序（序号取位置，不写进文件）；
/// 2 每一关都有 name / hint / 正数 turns / world；
/// 3 **每一关的 world 都能过校验** —— 这是"关卡坏在启动前"的唯一机器保证；
/// 4 手感数值集中在关卡文件里：`producerSlots = 3`、`defaultMaxJars = 6`、`defaultMaxLiquids ∈ {4,5}`。
/// </summary>
public class LevelCatalogTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JarSystem.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static List<string> LevelFiles()
    {
        var dir = Path.Combine(RepoRoot(), "config", "levels");
        Assert.True(Directory.Exists(dir), "找不到 config/levels 目录");
        return Directory.EnumerateFiles(dir, "*.json")
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();
    }

    private static (string Name, string Hint, int Turns, WorldOptions World) Parse(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String,
            Path.GetFileName(path) + " 缺少 name");
        Assert.True(root.TryGetProperty("hint", out var h) && h.ValueKind == JsonValueKind.String,
            Path.GetFileName(path) + " 缺少 hint");
        Assert.True(root.TryGetProperty("turns", out var t) && t.ValueKind == JsonValueKind.Number,
            Path.GetFileName(path) + " 缺少 turns");
        Assert.True(root.TryGetProperty("world", out var w) && w.ValueKind == JsonValueKind.Object,
            Path.GetFileName(path) + " 缺少 world 对象");

        var turns = t.GetInt32();
        Assert.True(turns > 0, Path.GetFileName(path) + " 的 turns 必须是正数，实际 " + turns);

        var world = WorldOptionsJson.FromJson(w.GetRawText());
        return (n.GetString()!, h.GetString()!, turns, world);
    }

    [Fact]
    public void SixLevelsExistInPlayOrder()
    {
        var files = LevelFiles();
        Assert.True(files.Count >= 6, "关卡少于 6 个，实际 " + files.Count);

        // 顺序由文件名决定（01_ … 06_）：序号是"位置"，插一关不必回头改后续文件。
        var names = files.Select(f => Path.GetFileName(f)!).ToList();
        Assert.Equal(names.OrderBy(x => x, StringComparer.Ordinal), names);
    }

    [Fact]
    public void EveryLevelWorldPassesValidation()
    {
        var problems = new List<string>();
        foreach (var file in LevelFiles())
        {
            var (name, hint, turns, world) = Parse(file);
            Assert.False(string.IsNullOrWhiteSpace(name), Path.GetFileName(file) + " 的 name 为空");
            Assert.False(string.IsNullOrWhiteSpace(hint), Path.GetFileName(file) + " 的 hint 为空");

            var report = world.Validate();
            foreach (var e in report.Errors) problems.Add(Path.GetFileName(file) + "：" + e);
        }
        Assert.True(problems.Count == 0, "关卡世界的校验错误：\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void FeelNumbersLiveInTheLevelFiles()
    {
        // 这三条正是"手感数值不进代码"的落点：卡槽上限、罐位、液池。
        // 它们都在关卡文件里 ⇒ 换一关换一套，代码不用动。
        foreach (var file in LevelFiles())
        {
            var (_, _, _, world) = Parse(file);
            var f = Path.GetFileName(file);
            Assert.Equal(3, world.ProducerSlots);
            Assert.Equal(6, world.DefaultMaxJars);
            Assert.InRange(world.DefaultMaxLiquids, 4, 5);
        }
    }

    [Fact]
    public void TheTurnLimitIsWrittenOnce()
    {
        // 回合上限只有一个来源：关卡信封上的 `turns`（宿主 LevelCatalog 把它灌进世界的 TurnLimit，
        // 内核据此硬停并判失败，见 TurnBudgetTests）。
        // 所以**不许**在 world 块里再写一份 `turnLimit` —— 两份值的第一天就开始漂移，
        // 而"最多几回合"正是这一关的难度本身。
        var problems = new List<string>();
        foreach (var file in LevelFiles())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("world", out var worldEl), Path.GetFileName(file) + " 缺少 world");
            if (worldEl.TryGetProperty("turnLimit", out _))
                problems.Add(Path.GetFileName(file) + "：world 里不要再写 turnLimit，回合上限只写在信封的 turns 上");

            var turns = root.GetProperty("turns").GetInt32();
            if (turns <= 0) problems.Add(Path.GetFileName(file) + "：turns 必须是正数");
        }
        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    [Fact]
    public void EveryLevelCanProduceAtLeastOneShape()
    {
        // 防空转：一个"没有任何生产者"的关卡在 UI 上看起来像坏了，其实是数据写错了。
        var problems = new List<string>();
        foreach (var file in LevelFiles())
        {
            var (_, _, _, world) = Parse(file);
            var produced = world.Producers.SelectMany(p => p.Rates.Keys).Where(k => k.Length > 0).Distinct().Count();
            if (produced == 0) problems.Add(Path.GetFileName(file) + "：没有任何生产者");
        }
        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }
}

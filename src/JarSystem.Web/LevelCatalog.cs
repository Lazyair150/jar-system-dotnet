using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JarSystem.Core.Config;

/// <summary>
/// 关卡目录（宿主侧）：把 <c>config/levels/*.json</c> 读成「一关 = 一份世界 + 一条提示 + 回合上限」。
///
/// 边界（与 <see cref="JarSystemService"/> 同一口径）：
/// <list type="bullet">
/// <item>**核心不认识"关卡"** —— 关只是"打包好的一份 <see cref="WorldOptions"/>"，核心照旧只认世界；</item>
/// <item>文件 IO、目录扫描、命令行参数都只出现在宿主层；</item>
/// <item>每关的世界**必须**过 <see cref="WorldOptions.Validate"/>：任一关坏了就拒绝启动，
/// 而不是"选到那一关才炸"（与 <c>config/world.json</c> 的失败口径一致）。</item>
/// </list>
///
/// 文件格式（`config/levels/NN_名字.json`）：
/// <code>
/// {
///   "name": "第一关 · 学会改形",
///   "hint": "……",
///   "turns": 3,
///   "world": { "shapes": [...], "liquids": [...], ... }   // 与 config/world.json 同构
/// }
/// </code>
/// 序号取**文件名排序后的位置**（1 起），不写进文件里 —— 这样插一关不必回头改所有后续文件。
/// </summary>
public sealed record LevelInfo(int Index, string Name, string Hint, int Turns, WorldOptions World, string Path);

/// <summary>扫描并解析 <c>config/levels/</c>。</summary>
public static class LevelCatalog
{
    /// <summary>关卡目录（相对仓库根）。</summary>
    public const string RelativeDir = "config/levels";

    /// <summary>
    /// 读出全部关卡，按文件名排序、序号从 1 起。
    /// 任何一关解析失败或校验不过 ⇒ 抛 <see cref="InvalidOperationException"/>（启动即失败）。
    /// </summary>
    public static IReadOnlyList<LevelInfo> Discover()
    {
        var dir = JarSystemService.ResolveConfigDir();
        var levelDir = System.IO.Path.Combine(dir, "levels");
        if (!Directory.Exists(levelDir)) return Array.Empty<LevelInfo>();

        var files = Directory.EnumerateFiles(levelDir, "*.json")
            .OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();

        var levels = new List<LevelInfo>(files.Count);
        foreach (var file in files)
        {
            var index = levels.Count + 1;
            levels.Add(Parse(index, file));
        }
        return levels;
    }

    /// <summary>按 1 起序号找一关；找不到返回 null（调用方决定是报错还是退回默认世界）。</summary>
    public static LevelInfo? Find(int index) => Discover().FirstOrDefault(l => l.Index == index);

    private static LevelInfo Parse(int index, string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!
            : System.IO.Path.GetFileNameWithoutExtension(file);
        var hint = root.TryGetProperty("hint", out var h) && h.ValueKind == JsonValueKind.String
            ? h.GetString()!
            : "";
        var turns = root.TryGetProperty("turns", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32()
            : 0;

        if (turns <= 0)
            throw new InvalidOperationException("关卡 " + file + " 缺少正的 turns（这一关要几回合内完成）");

        if (!root.TryGetProperty("world", out var worldEl) || worldEl.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("关卡 " + file + " 缺少 world 对象（应与 config/world.json 同构）");

        var world = WorldOptionsJson.FromJson(worldEl.GetRawText());
        var report = world.Validate();
        foreach (var warning in report.Warnings) Console.Error.WriteLine("[level] warning: " + warning);
        if (!report.IsValid)
        {
            foreach (var error in report.Errors) Console.Error.WriteLine("[level] error: " + error);
            throw new InvalidOperationException(
                "关卡 " + file + " 的世界校验失败：" + report.Errors.Count + " 个错误，已拒绝启动");
        }

        return new LevelInfo(index, name, hint, turns, world, file);
    }
}

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
public sealed record LevelInfo(int Index, string Name, string Hint, int Turns, WorldOptions World, string Path)
{
    /// <summary>
    /// 开局的主台下标（信封的 `master`；null = 不预设，让玩家自己挑）。
    /// 为什么不放进 WorldOptions：台子是这一关的**摆放**，不是世界规则；
    /// 核心不认识"开局该选哪台"，只有关卡知道。
    /// </summary>
    public int? Master { get; init; }

    /// <summary>开局的从台下标（信封的 `slave`；null = 不用从台）。</summary>
    public int? Slave { get; init; }

    /// <summary>
    /// 开局派单表：这一关开局桌上有几张单、各自用哪个模板（信封的 `orders`，允许重复）。
    ///
    /// 为什么不按"每个模板各来一单"推导：**模板目录是"这一关可做的东西"，开局单数是"这一关的内容"**。
    /// 第六关的教训就是两者被当成了同一件事——提示说"两张订单一模一样，先做哪一张另一张就做不成"，
    /// 而运行期按模板数只派了一单，于是"抢资源"这件事根本没发生。
    ///
    /// 没写 `orders` 的关卡沿用旧口径（每模板一单），既有五关的行为一字不变。
    /// </summary>
    public IReadOnlyList<string> Orders { get; init; } = Array.Empty<string>();
}

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

        // **目标单数也只有一个来源**：关卡信封上的 goal（做成几单算赢）。
        var goal = root.TryGetProperty("goal", out var g) && g.ValueKind == JsonValueKind.Number
            ? g.GetInt32()
            : 0;

        if (goal <= 0)
            throw new InvalidOperationException(
                "关卡 " + file + " 缺少正的 goal（这一关要做成几单算赢；没有目标就永远判不了胜）");

        if (!root.TryGetProperty("world", out var worldEl) || worldEl.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("关卡 " + file + " 缺少 world 对象（应与 config/world.json 同构）");

        // 目标单数与回合上限一样，只许写在信封上：world 块里再写一遍，两边立刻会漂移。
        if (worldEl.TryGetProperty("goalOrders", out _))
            throw new InvalidOperationException(
                "关卡 " + file + " 的 world 块里不要写 goalOrders：目标单数只写在信封的 goal 上");

        var world = WorldOptionsJson.FromJson(worldEl.GetRawText());

        // **回合上限只有一个来源**：关卡信封上的 turns。
        // 世界里也允许有 turnLimit（自由世界用），但关卡文件不让作者写两遍 ——
        // 写了两份的第一天就开始漂移，而"最多几回合"是这一关的难度本身。
        world.TurnLimit = turns;

        // 目标单数同理：信封给，内核的 WorldOptions.GoalOrders 只是它的落点。
        world.GoalOrders = goal;

        var report = world.Validate();
        foreach (var warning in report.Warnings) Console.Error.WriteLine("[level] warning: " + warning);
        if (!report.IsValid)
        {
            foreach (var error in report.Errors) Console.Error.WriteLine("[level] error: " + error);
            throw new InvalidOperationException(
                "关卡 " + file + " 的世界校验失败：" + report.Errors.Count + " 个错误，已拒绝启动");
        }

        // 开局台位也写在信封上：关卡知道"这一关从哪两台开始"，即它的起手是很具体的内容。
        // 越界在这里就拒绝，而不是等玩家点开那一刻才炸。
        int? ReadBench(string key)
        {
            if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number) return null;
            var at = el.GetInt32();
            if (at < 0 || at >= world.Workshops.Count)
                throw new InvalidOperationException(
                    "关卡 " + file + " 的 " + key + " = " + at + " 超出工作台范围（0.." + (world.Workshops.Count - 1) + "）");
            return at;
        }

        return new LevelInfo(index, name, hint, turns, world, file)
        {
            Master = ReadBench("master"),
            Slave = ReadBench("slave"),
            Orders = ReadOrders(file, root, world)
        };
    }

    /// <summary>
    /// 读开局派单表（信封的 `orders`）。
    ///
    /// 两种写法都收：字符串（`"一只三角罐"`）或对象（`{ "template": "一只三角罐" }`）——
    /// 后者给将来的"每单单独的奖励/目标"留位置，现在等价于前者。
    ///
    /// 三条拒绝都在启动时：模板名不存在（这一关没有它可做）、条目不是字符串/对象、
    /// 以及写了空数组（`[]` 等于开局一张单都没有，玩家无事可做，几乎一定是笔误）。
    /// </summary>
    private static IReadOnlyList<string> ReadOrders(string file, JsonElement root, WorldOptions world)
        => ReadOrderInstances(root, world.OrderTemplates.Select(t => t.Name), file);

    /// <summary>
    /// 关卡里的「订单实例表」。两种写法都认（`"模板名"` 与 `{ "template": "模板名" }`）——
    /// 前者是玩家/手写关卡的直觉写法，后者给将来加每单元数据留位置；两种写法结果**逐字相同**，
    /// 所以这一层必须能被单测直接钉住（解析是契约，不能只靠"跑一关看看"）。
    ///
    /// 规则：没写 `orders` = 每个模板一单（既有五关的口径）；写了就必须逐条认得出来，
    /// 空数组、认不出的条目、引用不存在的模板，一律**开局前**抛错。
    /// </summary>
    public static IReadOnlyList<string> ReadOrderInstances(
        JsonElement root, IEnumerable<string> templates, string label)
    {
        var all = templates.ToList();
        var known = new HashSet<string>(all, StringComparer.Ordinal);

        if (!root.TryGetProperty("orders", out var el) || el.ValueKind != JsonValueKind.Array)
            return all;

        var orders = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            var template = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("template", out var t)
                                          && t.ValueKind == JsonValueKind.String => t.GetString(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(template))
                throw new InvalidOperationException(
                    "关卡 " + label + " 的 orders 里有认不出的条目：写模板名字符串，或 { \"template\": \"名字\" }");

            if (!known.Contains(template!))
                throw new InvalidOperationException(
                    "关卡 " + label + " 的 orders 引用了不存在的模板「" + template + "」（这一关的模板："
                    + string.Join("、", known) + "）");

            orders.Add(template!);
        }

        if (orders.Count == 0)
            throw new InvalidOperationException(
                "关卡 " + label + " 的 orders 是空数组：开局一张单都没有，玩家无事可做（要空桌请删掉这个字段）");

        return orders;
    }
}

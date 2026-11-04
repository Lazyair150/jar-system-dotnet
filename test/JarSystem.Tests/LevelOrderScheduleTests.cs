using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JarSystem.Core.Config;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **开局派单表**（关卡信封的 `orders`）的契约测试。
///
/// 为什么要单独一条契约：模板目录与开局单数曾经被当成同一件事
/// （"每个模板各派一单"），于是第六关写着"两张订单一模一样，先做哪一张另一张就做不成"，
/// 运行期却只派了一单——这一关教的东西根本没发生。
///
/// 现在分成两个概念：
/// <list type="bullet">
/// <item>**模板目录**（`world.orderTemplates`）= 这一关可做的东西；</item>
/// <item>**开局派单表**（信封 `orders`）= 开局桌上有几张单、各用哪个模板（允许重复）。</item>
/// </list>
/// 没写 `orders` 的关卡沿用旧口径（每模板一单），既有五关行为不变。
///
/// 宿主侧解析在 <c>src/JarSystem.Web/LevelCatalog.cs</c> 的 `ReadOrders`；这里直接读文件，
/// 因为核心测试不引用宿主工程。
/// </summary>
public class LevelOrderScheduleTests
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

    private static WorldOptions WorldOf(JsonElement root)
    {
        Assert.True(root.TryGetProperty("world", out var w) && w.ValueKind == JsonValueKind.Object,
            "缺少 world 对象");
        return WorldOptionsJson.FromJson(w.GetRawText());
    }

    /// <summary>
    /// 按宿主 <c>LevelCatalog.ReadOrders</c> 的口径读派单表：字符串或 `{ "template": … }`；
    /// 没有这个字段 ⇒ 每模板一单。
    /// </summary>
    private static List<string> Orders(JsonElement root, WorldOptions world)
    {
        if (!root.TryGetProperty("orders", out var el) || el.ValueKind != JsonValueKind.Array)
            return world.OrderTemplates.Select(t => t.Name).ToList();

        var orders = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.TryGetProperty("template", out var t) ? t.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name)) orders.Add(name!);
        }
        return orders;
    }

    [Fact]
    public void EveryAuthoredOrderNamesARealTemplate()
    {
        // 每一张单都必须指向一个真模板：否则派单在启动时炸（宿主拒绝启动），这里更早暴露。
        var problems = new List<string>();
        foreach (var file in LevelFiles())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var world = WorldOf(doc.RootElement);
            var known = new HashSet<string>(world.OrderTemplates.Select(t => t.Name), StringComparer.Ordinal);
            var orders = Orders(doc.RootElement, world);

            if (orders.Count == 0) problems.Add(Path.GetFileName(file) + "：开局一张单都没有");
            foreach (var name in orders.Where(name => !known.Contains(name)))
                problems.Add(Path.GetFileName(file) + "：orders 引用了不存在的模板「" + name + "」");
        }
        Assert.True(problems.Count == 0, string.Join("  \n  ", problems));
    }

    [Fact]
    public void TheSixthLevelReallyDeploysTwoIdenticalOrders()
    {
        // 第六关教的是"抢资源"：两张一样的单争一份蜜。
        // 它曾经的缺陷不是表现，而是内容与运行期不一致：模板只有一个 ⇒ 只派一单。
        // 所以这里同时钉住两件事——**模板仍然只有一个**（内容没变），**单是两张**（这才是这一关）。
        var file = LevelFiles().Single(f => Path.GetFileName(f).StartsWith("06_", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var world = WorldOf(doc.RootElement);
        var orders = Orders(doc.RootElement, world);

        Assert.Equal(2, orders.Count);
        Assert.Single(orders.Distinct());
        Assert.Single(world.OrderTemplates);
        Assert.Equal(world.OrderTemplates[0].Name, orders[0]);
    }

    [Fact]
    public void LevelsWithoutAnOrderScheduleKeepTheOldOnePerTemplateRule()
    {
        // 没写 orders 的关卡必须与旧行为逐字一致（每模板一单），否则"加一个字段"会暗改既有五关。
        var checkedFiles = 0;
        foreach (var file in LevelFiles())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("orders", out _)) continue;

            var world = WorldOf(doc.RootElement);
            var orders = Orders(doc.RootElement, world);
            Assert.Equal(world.OrderTemplates.Select(t => t.Name).ToList(), orders);
            checkedFiles++;
        }

        // 防空转：如果一个"没写 orders"的关卡都没有，这条测试就什么也没验。
        Assert.True(checkedFiles > 0, "所有关卡都写了 orders，这条契约就没被验证过");
    }
}

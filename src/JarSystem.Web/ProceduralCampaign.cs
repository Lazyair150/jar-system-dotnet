using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.World;

/// <summary>
/// **程序化关卡**：用 `原型 + 种子 + 规则版本` 确定性地生成一关。
///
/// 边界（与 <see cref="LevelCatalog"/> 同一口径）：
/// <list type="bullet">
/// <item>核心不认识"关卡"——生成器产出的仍然只是一份 <see cref="WorldOptions"/> + 订单实例表；</item>
/// <item>随机的只有**内容参数**（速率、库存、容量、名字），机制恒定；</item>
/// <item>同一个 (原型, 种子, 规则版本) 必须产出**逐字相同**的一关：可分享种子、可复现问题；</item>
/// <item>生成完必须过 <see cref="LevelGates"/>——生成的关卡与手写关卡走同一道闸，过不了就**拒绝**生成，
/// 而不是把一关没人能赢的关卡交出去。</item>
/// </list>
///
/// 为什么内容参数都挤在很窄的区间里（速率 0.8/1.0、蜜 1..4）：参数区间越宽，越容易生成出
/// "回合不够"的死关。闸门会拦住这些关卡（拒绝生成），但被拦掉一半种子的生成器对玩家毫无意义
/// —— 所以参数的可选值本身就是**难度曲线的表达**，由作者挑，不由随机数挑。
/// </summary>
public sealed record GeneratedLevel(
    string ArchetypeId,
    string ArchetypeName,
    string Name,
    string Hint,
    int Turns,
    int Goal,
    int Master,
    IReadOnlyList<string> Orders,
    WorldOptions World,
    int Seed,
    string RulesVersion);

/// <summary>生成原型的静态描述（供页面的"随便来一关"列出选项）。</summary>
public sealed record CampaignArchetype(string Id, string Name, string Lesson);

public static class ProceduralCampaign
{
    /// <summary>
    /// **规则版本**：改动生成规则时必须递增它。种子只在同一版本内保证产出同一关，
    /// 否则玩家分享的种子会在升级后变成另一关（这正是"记录版本"的意义）。
    /// </summary>
    public const string RulesVersion = "1";

    /// <summary>六个原型，与手写六关的课程表一一对应（新机制 = 新原型，而不是把旧的调难）。</summary>
    public static readonly IReadOnlyList<CampaignArchetype> Archetypes = new[]
    {
        new CampaignArchetype("reshape", "改形", "基础标记改形、附加标记要付一份成本液体"),
        new CampaignArchetype("slave", "从台装液", "从台只是临时工位，液体要先进罐"),
        new CampaignArchetype("bans", "禁令换台", "禁令禁止使用、不禁存放；换主台会搬走作者给的资源"),
        new CampaignArchetype("four-ops", "四种活", "配、灌、空罐改形、满罐自耗改形"),
        new CampaignArchetype("transfer", "转移", "该转移的罐与催化剂罐都要转移，剩下的回流"),
        new CampaignArchetype("contention", "抢资源", "两张一模一样的单抢同一份稀缺资源"),
    };

    /// <summary>八个形状与两个液体是本游戏世界的固定词表（生成器只能在其上组合参数）。</summary>
    private static readonly string[] ShapePool = { "圆罐", "方罐", "三角罐", "高罐", "长罐", "扁罐", "鼓罐", "尖罐" };
    private static readonly string[] LiquidPool = { "酒", "蜜" };

    public static CampaignArchetype? FindArchetype(string id) =>
        Archetypes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 生成一关。同样的三个参数永远给出同一关（逐字），并且**只有**过闸的那一关会被返回。
    /// </summary>
    /// <exception cref="ArgumentException">原型不认识，或规则版本不符。</exception>
    /// <exception cref="InvalidOperationException">生成出来的这一关过不了闸门（含原因）。</exception>
    public static GeneratedLevel Generate(string archetypeId, int seed, string rulesVersion = RulesVersion)
    {
        var archetype = FindArchetype(archetypeId)
                        ?? throw new ArgumentException("不认识的关卡原型: " + archetypeId, nameof(archetypeId));
        if (!string.Equals(rulesVersion, RulesVersion, StringComparison.Ordinal))
            throw new ArgumentException(
                "这个种子属于规则版本 " + rulesVersion + "，当前版本是 " + RulesVersion + "（规则变了，同一颗种子会是另一关）",
                nameof(rulesVersion));

        // 种子只喂给 Random ⇒ 同一颗种子 + 同一版本 = 同一串取值 = 同一关。
        var rng = new Random(unchecked(seed * 7919 + archetype.Id.GetHashCode(StringComparison.Ordinal)));
        var level = Build(archetype, rng, seed);

        var report = LevelGates.Check(level.World, level.Turns, level.Goal, level.Orders);
        if (!report.Passed)
            throw new InvalidOperationException(
                "生成的关卡过不了闸门（原型 " + archetype.Id + "，种子 " + seed + "）：" + report.Describe());

        return level;
    }

    private static GeneratedLevel Build(CampaignArchetype archetype, Random rng, int seed) => archetype.Id switch
    {
        "reshape" => BuildReshape(archetype, rng, seed),
        "slave" => BuildSlave(archetype, rng, seed),
        "bans" => BuildBans(archetype, rng, seed),
        "four-ops" => BuildFourOps(archetype, rng, seed),
        "transfer" => BuildTransfer(archetype, rng, seed),
        "contention" => BuildContention(archetype, rng, seed),
        _ => throw new ArgumentException("不认识的关卡原型: " + archetype.Id, nameof(archetype)),
    };

    // ---- 六个原型：每个只变内容参数，机制恒定 ---------------------------------------------

    private static GeneratedLevel BuildReshape(CampaignArchetype a, Random rng, int seed)
    {
        var rate = Pick(rng, new[] { 0.8, 1.0 });
        var honey = Pick(rng, new[] { 3, 4 });
        var cost = Pick(rng, LiquidPool);
        var turns = honey == 3 ? 9 : 10;

        var world = Skeleton();
        world.InitialStock[cost] = honey;
        world.Producers.Add(Producer("陶工甲", ("圆罐", rate), ("方罐", rate * 0.8)));
        world.Workshops.Add(Workshop("玄武台", new[] { "圆罐", "方罐" }, new[] { "三角罐" }, ban: Banned(LiquidPool.First(l => l != cost))));
        world.OrderTemplates.Add(Template("一只三角罐", "A", ("三角罐", null)));

        return Make(a, seed, "改形 · 种子 " + seed,
            "玄武台会把圆罐改成三角罐，每改一次要付一份" + cost + "。开局有 " + honey + " 份，产线每回合只补得出有限几只罐 —— 先想清楚要改几次。",
            turns, 1, 0, new[] { "一只三角罐" }, world);
    }

    private static GeneratedLevel BuildSlave(CampaignArchetype a, Random rng, int seed)
    {
        var rate = Pick(rng, new[] { 0.4, 0.5 });
        var honey = Pick(rng, new[] { 4, 5 });
        var turns = 7;

        var world = Skeleton();
        world.InitialStock["蜜"] = honey;
        world.Producers.Add(Producer("陶工乙", ("高罐", rate)));
        world.Workshops.Add(Workshop("玄武台", new[] { "圆罐", "方罐" }, new[] { "三角罐" }, ban: new[] { "酒" }));
        world.Workshops.Add(Workshop("朱雀台", new[] { "高罐" }, new[] { "长罐" }, ban: new[] { "酒" }));
        world.OrderTemplates.Add(Template("高罐灌蜜", "B", ("高罐", "蜜")));

        return Make(a, seed, "从台装液 · 种子 " + seed,
            "要一只装了蜜的高罐：高罐来自产线，蜜来自仓库 —— 但给罐子装液要在从台上做。先把高罐和蜜都挪到该在的位置。",
            turns, 1, 0, new[] { "高罐灌蜜" }, world);
    }

    private static GeneratedLevel BuildBans(CampaignArchetype a, Random rng, int seed)
    {
        var rate = Pick(rng, new[] { 0.4, 0.6 });
        var turns = 7;
        var wanted = Pick(rng, LiquidPool);
        var other = LiquidPool.First(l => l != wanted);
        var minted = Pick(rng, new[] { 2, 3 });

        var world = Skeleton();
        world.InitialStock[wanted] = minted;
        world.InitialStock[other] = 2;
        world.Producers.Add(Producer("陶工丙", ("方罐", rate), ("圆罐", rate / 2)));
        // 玄武台禁"要的那一种" ⇒ 它做不了这一单；青龙台禁"另一种" ⇒ 它做得了。
        world.Workshops.Add(Workshop("玄武台", new[] { "圆罐", "方罐" }, new[] { "三角罐" }, ban: new[] { wanted }));
        world.Workshops.Add(Workshop("青龙台", new[] { "方罐", "圆罐" }, new[] { "高罐" }, ban: new[] { other }));
        world.OrderTemplates.Add(Template("方罐灌" + wanted, "A", ("方罐", wanted)));

        return Make(a, seed, "禁令换台 · 种子 " + seed,
            "玄武台禁止使用「" + wanted + "」，青龙台禁止使用「" + other + "」—— 两张台都拿得到方罐，但这一单只有一张台做得了。" +
            "禁令禁的是「使用」，不是「存放」：仓库里的液体照样能搬过去，只是不能在那里用。",
            turns, 1, 0, new[] { "方罐灌" + wanted }, world);
    }

    private static GeneratedLevel BuildFourOps(CampaignArchetype a, Random rng, int seed)
    {
        var rate = Pick(rng, new[] { 0.4, 0.5 });
        var turns = 11;
        var jars = Pick(rng, new[] { 6, 7 });

        var world = Skeleton();
        world.DefaultMaxJars = jars;
        world.DefaultMaxLiquids = 5;
        world.InitialStock["蜜"] = Pick(rng, new[] { 3, 4 });
        world.Producers.Add(Producer("陶工丁", ("圆罐", rate), ("方罐", rate)));
        world.Workshops.Add(Workshop("玄武台", new[] { "圆罐", "方罐" }, new[] { "三角罐" }, ban: new[] { "酒" }));
        world.Workshops.Add(Workshop("朱雀台", new[] { "高罐", "长罐" }, new[] { "鼓罐" }, ban: new[] { "酒" }));
        world.Workshops.Add(Workshop("白虎台", new[] { "扁罐", "尖罐" }, new[] { "圆罐" }, ban: new[] { "酒" }));
        world.OrderTemplates.Add(Template("三件套", "A", ("圆罐", null), ("三角罐", null), ("圆罐", "蜜")));

        return Make(a, seed, "四种活 · 种子 " + seed,
            "这一单要三件：一只圆罐、一只三角罐、一只装了蜜的圆罐。配、灌、空罐改形、满罐自耗改形 —— 四种活都要用到，注意罐位只有 " + jars + "。",
            turns, 1, 0, new[] { "三件套" }, world);
    }

    private static GeneratedLevel BuildTransfer(CampaignArchetype a, Random rng, int seed)
    {
        var rate = Pick(rng, new[] { 0.4, 0.6 });
        var turns = 12;
        var honey = Pick(rng, new[] { 2, 3 });

        var world = Skeleton();
        world.InitialStock["蜜"] = honey;
        world.Producers.Add(Producer("陶工戊", ("高罐", rate), ("长罐", rate)));
        world.Workshops.Add(Workshop("玄武台", new[] { "圆罐", "方罐" }, new[] { "三角罐" }, ban: new[] { "酒" }));
        world.Workshops.Add(Workshop("朱雀台", new[] { "高罐", "长罐" }, new[] { "鼓罐" }, ban: new[] { "酒" }));
        world.OrderTemplates.Add(Template("高罐配长罐", "B", ("高罐", null), ("长罐", "蜜")));

        return Make(a, seed, "转移 · 种子 " + seed,
            "这一单要在从台上做：高罐与长罐都得先到主台、再被选中转过去；没用上的罐子和液体最后会回流 —— 留意它们回来了多少。",
            turns, 1, 0, new[] { "高罐配长罐" }, world);
    }

    private static GeneratedLevel BuildContention(CampaignArchetype a, Random rng, int seed)
    {
        var rate = Pick(rng, new[] { 0.5, 0.6 });
        var honey = Pick(rng, new[] { 1, 2 });
        var turns = 4;

        var world = Skeleton();
        world.DefaultMaxJars = 6;
        world.InitialStock["蜜"] = honey;
        world.InitialStock["酒"] = 0;
        world.Flow = new FlowOptions { JarCount = 3, LiquidAmount = 1, LiquidRate = { ["酒"] = 0, ["蜜"] = 0 } };
        world.Producers.Add(Producer("陶工己", ("圆罐", rate)));
        world.Workshops.Add(Workshop("玄武台", new[] { "圆罐", "方罐" }, new[] { "三角罐" }, ban: new[] { "酒" }));
        world.OrderTemplates.Add(Template("一只三角罐", "A", ("三角罐", null)));

        return Make(a, seed, "抢资源 · 种子 " + seed,
            "两张订单一模一样，都要一只三角罐；三角罐是附加标记，改一次付一份蜜，开局只有 " + honey + " 份。先做哪一张，另一张就做不成。",
            turns, 1, 0, new[] { "一只三角罐", "一只三角罐" }, world);
    }

    // ---- 公共骨架与取值 -------------------------------------------------------------------

    /// <summary>
    /// 公共骨架：与手写关卡同一组固定参数（八个形状、两种液体、产线卡槽 3、默认容量、**两液自然流入为 0**）。
    /// 目的不是"省事"，而是让生成关卡与手写关卡处在同一个世界里，闸门才有意义。
    /// </summary>
    private static WorldOptions Skeleton() => new()
    {
        Shapes = ShapePool.ToList(),
        Liquids = LiquidPool.ToList(),
        DefaultMaxJars = 6,
        DefaultMaxLiquids = 5,
        ProducerSlots = 3,
        Flow = new FlowOptions
        {
            JarCount = 3,
            LiquidAmount = 2,
            LiquidRate = { ["酒"] = 0, ["蜜"] = 0 },
        },
    };

    private static GeneratedLevel Make(
        CampaignArchetype archetype, int seed, string name, string hint, int turns, int goal, int master,
        IReadOnlyList<string> orders, WorldOptions world)
    {
        world.TurnLimit = turns;
        world.GoalOrders = goal;
        return new GeneratedLevel(archetype.Id, archetype.Name, name, hint, turns, goal, master, orders, world, seed, RulesVersion);
    }

    private static ProducerOptions Producer(string name, params (string Shape, double Rate)[] rates)
    {
        var producer = new ProducerOptions { Name = name };
        foreach (var (shape, rate) in rates) producer.Rates[shape] = rate;
        return producer;
    }

    private static WorkshopOptions Workshop(string name, string[] baseMarks, string[] additional, string[]? ban = null)
    {
        var workshop = new WorkshopOptions
        {
            Name = name,
            BaseMarks = baseMarks.ToList(),
            AdditionalMarks = additional.ToList(),
        };
        if (ban != null) workshop.Ban.AddRange(ban);
        return workshop;
    }

    private static OrderTemplateOptions Template(string name, string type, params (string Shape, string? Liquid)[] reqs)
    {
        var template = new OrderTemplateOptions { Name = name, Type = type };
        foreach (var (shape, liquid) in reqs)
            template.Requirements.Add(new RequirementOptions { Shape = shape, Liquid = liquid });
        return template;
    }

    private static string[] Banned(string liquid) => new[] { liquid };

    private static T Pick<T>(Random rng, IReadOnlyList<T> choices) => choices[rng.Next(choices.Count)];
}

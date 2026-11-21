using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JarSystem.Core.Config;

/// <summary>
/// **世界配置**：把一个"世界"整体描述成数据（形状/液体词表、容量默认、流入、每个 tick 的液体产出速率、
/// 生产者、初始库存、工作台清单、随机名字池）。
///
/// 设计原则：
/// <list type="bullet">
/// <item>**规则语义不在配置里**：配置只提供"参数与内容"，玩法不变量（一罐一操作、禁令边界、
/// 形状转换公式、溢出丢弃不回收…）仍在核心代码里，不可被配置改坏；</item>
/// <item>**契约字符串不配置化**：<c>OrderPhase</c>/<c>CheckMethod</c>/<c>SwitchCaseType</c> 等对外取值停留在 <see cref="Constants"/>；</item>
/// <item><see cref="Default"/> **逐值等于重构前的硬编码世界** ⇒ 不读配置时行为与重构前完全一致。</item>
/// </list>
///
/// 本类型是纯 POCO（零依赖），可用 <c>System.Text.Json</c>（.NET 内置）直接反序列化；
/// **读取文件由宿主负责**（核心不做 IO），见 `config/world.json` 与 <c>Bootstrap.SystemBootstrapper</c>。
/// </summary>
public sealed class WorldOptions
{
    /// <summary>形状目录（**唯一权威**；顺序会影响契约快照里相关键的出现顺序）。</summary>
    [JsonPropertyName("shapes")] public List<string> Shapes { get; set; } = new();

    /// <summary>液体目录（**必须恰好两个**——这是玩法语义；此处只承载它们的名字与顺序）。</summary>
    [JsonPropertyName("liquids")] public List<string> Liquids { get; set; } = new();

    /// <summary>工作台默认罐位上限（未在工作台条目里指定时使用）。</summary>
    [JsonPropertyName("defaultMaxJars")] public int DefaultMaxJars { get; set; } = Constants.DEFAULT_MAX_JARS;

    /// <summary>工作台默认液体上限。</summary>
    [JsonPropertyName("defaultMaxLiquids")] public int DefaultMaxLiquids { get; set; } = Constants.DEFAULT_MAX_LIQUIDS;

    /// <summary>主台流入参数与每 tick 液体产出速率。</summary>
    [JsonPropertyName("flow")] public FlowOptions Flow { get; set; } = new();

    /// <summary>初始液体库存（键 = 液体名）。</summary>
    [JsonPropertyName("initialStock")] public Dictionary<string, int> InitialStock { get; set; } = new();

    /// <summary>生产者及其速率（键 = 形状名）。</summary>
    [JsonPropertyName("producers")] public List<ProducerOptions> Producers { get; set; } = new();
    /// <summary>
    /// **产线卡槽容量**（玩法规则，住内核）：同时"装备"着的产线条数上限。
    /// 一"条"= 一个 (生产者, 形状) 且速率 &gt; 0；空槽合法。
    /// **默认 0 = 不限** —— 出厂沙盒不该被关卡规则约束；关卡文件显式写 3（见 关卡化 S1/S5）。
    /// 校验住在 <see cref="Production.ProducerModule.UpdateRate"/>（数据在这里 ⇒ 规则在这里），
    /// 页面限制不算数：别的客户端可以绕过页面，回放也不约束它。
    /// </summary>
    [JsonPropertyName("producerSlots")] public int ProducerSlots { get; set; }

    /// <summary>
    /// **回合上限**（玩法规则，住内核）：这一局最多能推进几回合，到了就**硬停并判失败**。
    /// **默认 0 = 不限** —— 出厂沙盒不该被关卡规则约束；关卡文件把它写成作者要的那几回合
    /// （见 <c>GameHost/LevelCatalog.cs</c>：一关文件里的 `turns` 就是这里的值）。
    ///
    /// 为什么住内核而不是住页面：页面限制可被别的客户端绕过，且"还能不能再推一回合"是玩家
    /// 真正要对抗的东西 —— 它必须和执行在同一个地方，否则界面说没回合了、世界却照走。
    /// 执行点见 <see cref="JarSystem.Tick"/>：超限**整个请求都不生效**（不是走一半）。
    /// </summary>
    [JsonPropertyName("turnLimit")] public int TurnLimit { get; set; }

    /// <summary>
    /// **目标订单数**（玩法规则，住内核）：这一局要做成几单算赢。
    /// **默认 0 = 无目标**（出厂沙盒不判胜）——关卡的关卡文件把它写成 1 或更多
    /// （见 <c>GameHost/LevelCatalog.cs</c>：关卡信封的 `goal` 就是这里的值）。
    ///
    /// 为什么不数"所有订单都完成了"：订单是玩家派出来的，场上可能一单都没有
    /// （"全部完成"就会假成立）。判胜必须对着一个**作者给的目标**，
    /// 否则空场开局第一帧就算赢。
    /// </summary>
    [JsonPropertyName("goalOrders")] public int GoalOrders { get; set; }



    /// <summary>工作台清单（装配顺序 = 列表顺序 ⇒ 主从下标与契约快照的顺序）。</summary>
    [JsonPropertyName("workshops")] public List<WorkshopOptions> Workshops { get; set; } = new();

    /// <summary>
    /// 订单模板库（**内容**，不是运行时对象）。
    /// 空表示"这个世界不提供模板"，此时订单只能靠显式需求或随机生成。
    /// 语义与校验在 <c>JarSystem.Core.Orders.OrderTemplateLibrary</c>：Config 层只描述形状，
    /// 不引用 <c>Requirement</c>（那在更上层），否则会破层序。
    /// </summary>
    [JsonPropertyName("orderTemplates")] public List<OrderTemplateOptions> OrderTemplates { get; set; } = new();

    /// <summary>随机工作台的名字池（内容词，原先是硬编码在服务里的数组）。</summary>
    [JsonPropertyName("namePools")] public NamePoolOptions NamePools { get; set; } = new();

    /// <summary>把 (形状, 液体) 元组拼成一条模板 —— 只是让 <see cref="CreateDefault"/> 里的模板清单可读。</summary>
    private static OrderTemplateOptions Template(string name, string type, params (string Shape, string? Liquid)[] reqs)
    {
        var o = new OrderTemplateOptions { Name = name, Type = type };
        foreach (var (shape, liquid) in reqs)
            o.Requirements.Add(new RequirementOptions { Shape = shape, Liquid = liquid });
        return o;
    }

    /// <summary>由本配置构造词表（形状/液体目录的唯一权威由此产生）。</summary>
    public Vocabulary BuildVocabulary() => new(Shapes, Liquids);

    /// <summary>
    /// 深拷贝配置。世界装配时用它隔离调用方持有的可变 POCO；对外读取 <c>JarSystem.World</c> 时
    /// 也返回副本，避免外部直接改目标/回合上限等字段绕过命令日志。
    /// </summary>
    public WorldOptions Clone() => new()
    {
        Shapes = new List<string>(Shapes),
        Liquids = new List<string>(Liquids),
        DefaultMaxJars = DefaultMaxJars,
        DefaultMaxLiquids = DefaultMaxLiquids,
        Flow = new FlowOptions
        {
            JarCount = Flow.JarCount,
            LiquidAmount = Flow.LiquidAmount,
            LiquidRate = new Dictionary<string, double>(Flow.LiquidRate)
        },
        InitialStock = new Dictionary<string, int>(InitialStock),
        Producers = Producers.Select(p => new ProducerOptions
        {
            Name = p.Name,
            Rates = new Dictionary<string, double>(p.Rates)
        }).ToList(),
        ProducerSlots = ProducerSlots,
        TurnLimit = TurnLimit,
        GoalOrders = GoalOrders,
        Workshops = Workshops.Select(w => new WorkshopOptions
        {
            Name = w.Name,
            BaseMarks = new List<string>(w.BaseMarks),
            AdditionalMarks = new List<string>(w.AdditionalMarks),
            Ban = new List<string>(w.Ban),
            MaxJars = w.MaxJars,
            MaxLiquids = w.MaxLiquids,
            LiquidStagingCapacity = w.LiquidStagingCapacity
        }).ToList(),
        OrderTemplates = OrderTemplates.Select(t => new OrderTemplateOptions
        {
            Name = t.Name,
            Type = t.Type,
            Requirements = t.Requirements.Select(r => new RequirementOptions
            {
                Shape = r.Shape,
                Liquid = r.Liquid
            }).ToList()
        }).ToList(),
        NamePools = new NamePoolOptions
        {
            Prefix = new List<string>(NamePools.Prefix),
            Suffix = new List<string>(NamePools.Suffix)
        }
    };

    /// <summary>
    /// 校验配置（**跨字段/引用完整性**，纯逻辑、无 IO）：引用不存在的形状/液体、模板重名、
    /// 液体不是恰好两种、生产者速率键非法…… 都在这里一次性报出。
    ///
    /// 约定：**Errors 非空 ⇒ 宿主应拒绝启动**（"启动即失败"优于"静默缺产出"）；
    /// Warnings 是需要人判断的情况（例如某个形状没有任何生产者 ⇒ 该形状永远产不出来）。
    /// </summary>
    public WorldValidationResult Validate()
    {
        var r = new WorldValidationResult();

        // ---- 词表 ----
        if (Shapes.Count == 0) r.Errors.Add("shapes 不能为空");
        if (Duplicate(Shapes) is { } dupShape) r.Errors.Add("shapes 有重复项: " + dupShape);
        if (Liquids.Count != 2) r.Errors.Add("liquids 必须恰好两个（玩法语义），当前 " + Liquids.Count + " 个");
        if (Duplicate(Liquids) is { } dupLiquid) r.Errors.Add("liquids 有重复项: " + dupLiquid);
        // 形状/液体名会进求解器的"子问题键"（用控制字符做分隔符），也会进前端 HTML。
        // 因此这里直接拒绝空名与控制字符 —— 顺带避免"名字里塞分隔符导致记忆化键碰撞"这类隐患。
        foreach (var s in Shapes) if (IsBadName(s)) r.Errors.Add("shapes 含非法名称（空/空白或含控制字符）: " + Describe(s));
        foreach (var l in Liquids) if (IsBadName(l)) r.Errors.Add("liquids 含非法名称（空/空白或含控制字符）: " + Describe(l));

        // ⚠️ 已知边界（有意为之，不是遗漏）：**液体名字目前不可改**。
        // 求解器/执行器/切换/可行性里仍有 10 处按内置液体名遍历（且遍历顺序会影响选出的路径），
        // 因此这里**显式拒绝**与内置不一致的液体名，避免"配置改了但没人理它"这种静默不一致。
        // 将来要支持改名，得先把词表穿到那 10 处，并保持顺序 —— 见 13_全局审视 §7。
        for (int i = 0; i < Liquids.Count && i < Constants.LIQUIDS.Count; i++)
        {
            if (Liquids[i] != Constants.LIQUIDS[i])
            {
                r.Errors.Add("liquids 目前不支持改名/改序（求解与执行仍按内置液体名与顺序工作），"
                    + "请保持与内置一致: " + string.Join(", ", Constants.LIQUIDS));
                break;
            }
        }

        // ---- 容量与流入 ----
        if (DefaultMaxJars <= 0) r.Errors.Add("defaultMaxJars 必须为正数");
        if (DefaultMaxLiquids <= 0) r.Errors.Add("defaultMaxLiquids 必须为正数");
        if (TurnLimit < 0) r.Errors.Add("turnLimit 不能为负（0 表示不限）");
        if (GoalOrders < 0) r.Errors.Add("goalOrders 不能为负（0 表示无目标）");
        if (Flow.JarCount < 0) r.Errors.Add("flow.jarCount 不能为负");
        if (Flow.LiquidAmount < 0) r.Errors.Add("flow.liquidAmount 不能为负");
        if (ProducerSlots < 0) r.Errors.Add("producerSlots 不能为负（0 表示不限）");
        foreach (var kv in Flow.LiquidRate)
        {
            if (!Liquids.Contains(kv.Key)) r.Errors.Add("flow.liquidRate 含未知液体: " + kv.Key);
            if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value))
                r.Errors.Add("flow.liquidRate[" + kv.Key + "] 必须是有限数");
            else if (kv.Value < 0) r.Errors.Add("flow.liquidRate[" + kv.Key + "] 不能为负");
            else if (kv.Value > Constants.MAX_PRODUCER_RATE)
                r.Errors.Add("flow.liquidRate[" + kv.Key + "] 超出上限 " + Constants.MAX_PRODUCER_RATE);
        }

        // ---- 初始库存 ----
        foreach (var kv in InitialStock)
        {
            if (!Liquids.Contains(kv.Key)) r.Errors.Add("initialStock 含未知液体: " + kv.Key);
            if (kv.Value < 0) r.Errors.Add("initialStock[" + kv.Key + "] 不能为负");
        }

        // ---- 生产者 ----
        var producerNames = new List<string>();
        var producedShapes = new List<string>();
        foreach (var p in Producers)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) r.Errors.Add("生产者缺少 name");
            else if (producerNames.Contains(p.Name)) r.Errors.Add("生产者名重复: " + p.Name);
            else producerNames.Add(p.Name);
            if (p.Rates.Count == 0) r.Warnings.Add("生产者 " + p.Name + " 没有任何速率");
            foreach (var kv in p.Rates)
            {
                if (!Shapes.Contains(kv.Key)) r.Errors.Add("生产者 " + p.Name + " 含未知形状: " + kv.Key);
                if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value))
                    r.Errors.Add("生产者 " + p.Name + " 的 " + kv.Key + " 速率必须是有限数");
                else if (kv.Value < 0) r.Errors.Add("生产者 " + p.Name + " 的 " + kv.Key + " 速率不能为负");
                // 上界与核心规则一致（Constants.MAX_PRODUCER_RATE）：速率会线性放大产出循环，
                // 配置里放一个 1e9 同样能把宿主打爆 ⇒ 启动即拒绝，而不是等运行时 OOM。
                else if (kv.Value > Constants.MAX_PRODUCER_RATE) r.Errors.Add("生产者 " + p.Name + " 的 " + kv.Key
                    + " 速率不能超过 " + Constants.MAX_PRODUCER_RATE + "（每 tick 产出上限）");
                else if (kv.Value > 0 && !producedShapes.Contains(kv.Key)) producedShapes.Add(kv.Key);
            }
        }
        int initialActiveLines = Producers.Sum(p => p.Rates.Count(kv => kv.Value > 0));
        if (ProducerSlots > 0 && initialActiveLines > ProducerSlots)
            r.Errors.Add("producerSlots 上限为 " + ProducerSlots + "，初始有效产线有 " +
                         initialActiveLines + " 条；请把多余产线速率设为 0 或提高上限");

        // ---- 工作台 ----
        var workshopNames = new List<string>();
        foreach (var w in Workshops)
        {
            if (string.IsNullOrWhiteSpace(w.Name)) r.Errors.Add("工作台缺少 name");
            else if (workshopNames.Contains(w.Name)) r.Errors.Add("工作台名重复: " + w.Name);
            else workshopNames.Add(w.Name);

            // 2026-09-16 补：**没有基础标记 = "卡死台"**（`GetPossibleTransforms` 只会给出"基础标记 + 附加标记"，
            // 两个都空 ⇒ 该台既不能改形，也不能倒液）。这条规则原本**只在**宿主命令
            // `WorkshopService.CreateWorkshop` 里 —— 而配置这条路（`world.json`）没有 ⇒ 同一份"世界"经两条
            // 入口进来口径不一致，写一个 `"baseMarks": []` 就能造出卡死台。现在两条入口同一条判据。
            if (w.BaseMarks.Count == 0)
                r.Errors.Add("工作台 " + w.Name + " 没有任何基础标记 ⇒ 该台既不能倒液也不能改形（卡死台）");

            foreach (var s in w.BaseMarks) if (!Shapes.Contains(s)) r.Errors.Add("工作台 " + w.Name + " 的基础标记含未知形状: " + s);
            foreach (var s in w.AdditionalMarks) if (!Shapes.Contains(s)) r.Errors.Add("工作台 " + w.Name + " 的附加标记含未知形状: " + s);
            foreach (var s in w.BaseMarks) if (w.AdditionalMarks.Contains(s)) r.Errors.Add("工作台 " + w.Name + " 的形状 " + s + " 同时是基础标记与附加标记");
            foreach (var l in w.Ban) if (!NormalizedBan(l, Liquids)) r.Errors.Add("工作台 " + w.Name + " 的禁令含未知液体: " + l);
            if (w.MaxJars is <= 0) r.Errors.Add("工作台 " + w.Name + " 的 maxJars 必须为正数");
            if (w.MaxLiquids is <= 0) r.Errors.Add("工作台 " + w.Name + " 的 maxLiquids 必须为正数");
            if (w.LiquidStagingCapacity is < 0)
                r.Errors.Add("工作台 " + w.Name + " 的 liquidStagingCapacity 不能为负数");
        }

        // ---- 名字池 ----
        if (NamePools.Prefix.Count == 0 || NamePools.Suffix.Count == 0)
            r.Warnings.Add("namePools 为空 ⇒ 随机工作台将没有可用名字");

        // ---- 覆盖性（警告，不阻断）----
        foreach (var s in Shapes)
            if (!producedShapes.Contains(s))
                r.Warnings.Add("形状 " + s + " 没有任何生产者 ⇒ 中央仓库永远不会产出它（工作台内仍可通过改形得到）");

        return r;
    }

    private static string? Duplicate(List<string> items)
    {
        var seen = new List<string>();
        foreach (var i in items)
        {
            if (seen.Contains(i)) return i;
            seen.Add(i);
        }
        return null;
    }

    /// <summary>名称是否非法：空/纯空白，或含控制字符（控制字符会被求解器的子问题键用作分隔符）。</summary>
    private static bool IsBadName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        foreach (var ch in name) if (char.IsControl(ch)) return true;
        return false;
    }

    /// <summary>把非法名称渲染成可读文本（控制字符转义输出，避免错误信息本身被污染）。</summary>
    private static string Describe(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("X4"));
            else sb.Append(ch);
        }
        return "\"" + sb + "\"";
    }

    /// <summary>禁令允许写 "酒" 或 "禁酒" 两种写法（与 <c>WorkshopCapability</c> 的规范化一致）。</summary>
    private static bool NormalizedBan(string ban, List<string> liquids)
    {
        var v = ban.Trim();
        if (v.StartsWith("禁") && v.Length > 1) v = v[1..];
        return liquids.Contains(v);
    }

    // ============================================================
    // 内置默认世界 —— **逐值等于重构前的硬编码世界**（不读配置时的行为基准）
    // ============================================================
    /// <summary>
    /// 内置默认世界（每次访问返回**全新实例**）。
    ///
    /// 原先写成 static 只读**单例**，但 WorldOptions 是可变 POCO（所有属性都有 setter），
    /// 于是成了"全局可变单例"：任何代码改了 WorldOptions.Default.Liquids[0]，
    /// 全进程后续的默认世界都跟着变。改为每次构造一份，读写都不再共享状态。
    /// （JarSystem 会在装配时再做一次深拷贝；此属性只保证每次访问 Default 不共享实例。）
    /// </summary>
    public static WorldOptions Default => CreateDefault();

    private static WorldOptions CreateDefault()
    {
        var options = new WorldOptions
        {
            Shapes = new List<string>(Constants.SHAPES),
            Liquids = new List<string>(Constants.LIQUIDS),
            Flow = new FlowOptions
            {
                JarCount = 3,
                LiquidAmount = 2,
                LiquidRate = new Dictionary<string, double> { [Constants.LIQUIDS[0]] = 1, [Constants.LIQUIDS[1]] = 1 }
            },
            NamePools = new NamePoolOptions
            {
                Prefix = new List<string> { "金", "木", "水", "火", "土", "天", "地", "玄", "黄" },
                Suffix = new List<string> { "工坊", "作坊", "窑", "炉", "台", "阁" }
            }
        };

        // 初始库存：每种液体 20（顺序 = 词表顺序 ⇒ 契约快照里 liquids 的键序）
        foreach (var liquid in options.Liquids) options.InitialStock[liquid] = 20;

        // 生产者：4 个（顺序与重构前一致 ⇒ producerStatus 的键序）
        options.Producers.Add(new ProducerOptions { Name = "陶工甲", Rates = new() { ["圆罐"] = 0.4, ["方罐"] = 0.3 } });
        options.Producers.Add(new ProducerOptions { Name = "陶工乙", Rates = new() { ["高罐"] = 0.5, ["长罐"] = 0.2 } });
        options.Producers.Add(new ProducerOptions { Name = "机器A", Rates = new() { ["三角罐"] = 0.6, ["扁罐"] = 0.3 } });
        options.Producers.Add(new ProducerOptions { Name = "机器B", Rates = new() { ["鼓罐"] = 0.4, ["尖罐"] = 0.3 } });

        // 订单模板库：世界"可派什么单"的落点（此前这一层根本不存在，只能靠调用方传需求或随机掷骰）。
        // 顺序即"牌库"顺序，按名字取用；条目仍在 Orders 层做词表与条数校验。
        options.OrderTemplates.Add(Template("空双罐", Constants.OrderType.A,
            ("圆罐", null), ("方罐", null)));
        options.OrderTemplates.Add(Template("灌酒圆罐", Constants.OrderType.A,
            ("圆罐", null), ("圆罐", "酒")));
        options.OrderTemplates.Add(Template("三角改形", Constants.OrderType.A,
            ("方罐", null), ("三角罐", null)));
        options.OrderTemplates.Add(Template("主从高长", Constants.OrderType.B,
            ("高罐", null), ("长罐", "蜜")));
        options.OrderTemplates.Add(Template("三件套", Constants.OrderType.B,
            ("尖罐", null), ("扁罐", null), ("鼓罐", "酒")));   // 空罐在前、满罐在后（Order 的顺序不变量）

        // 工作台：模板（原 Rules.WORKSHOP_TEMPLATES）+ 青龙台 + 白虎台（顺序与重构前一致）
        foreach (var t in Rules.WORKSHOP_TEMPLATES)
            options.Workshops.Add(new WorkshopOptions
            {
                Name = t.Name,
                BaseMarks = new List<string>(t.BaseMarks),
                AdditionalMarks = new List<string>(t.AdditionalMarks),
                Ban = new List<string>(t.Ban)
            });
        options.Workshops.Add(new WorkshopOptions { Name = "青龙台", BaseMarks = ["三角罐"], AdditionalMarks = ["圆罐"], Ban = ["蜜"] });
        options.Workshops.Add(new WorkshopOptions { Name = "白虎台", BaseMarks = ["长罐", "扁罐"], AdditionalMarks = ["鼓罐"] });

        return options;
    }
}

/// <summary>主台流入参数 + 每 tick 液体产出速率。</summary>
public sealed class FlowOptions
{
    /// <summary>每次流入主台的罐子数。</summary>
    [JsonPropertyName("jarCount")] public int JarCount { get; set; } = 3;

    /// <summary>每次流入主台的液体量。</summary>
    [JsonPropertyName("liquidAmount")] public int LiquidAmount { get; set; } = 2;

    /// <summary>每 tick 各液体的产出速率（键 = 液体名；未列出者按 1）。</summary>
    [JsonPropertyName("liquidRate")] public Dictionary<string, double> LiquidRate { get; set; } = new();
}

/// <summary>生产者（名称 + 各形状速率）。</summary>
public sealed class ProducerOptions
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>键 = 形状名，值 = 每 tick 产出速率。</summary>
    [JsonPropertyName("rates")] public Dictionary<string, double> Rates { get; set; } = new();
}

/// <summary>工作台条目（装配参数；容量省略时用 <see cref="WorldOptions.DefaultMaxJars"/> / <c>DefaultMaxLiquids</c>）。</summary>
public sealed class WorkshopOptions
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("baseMarks")] public List<string> BaseMarks { get; set; } = new();
    [JsonPropertyName("additionalMarks")] public List<string> AdditionalMarks { get; set; } = new();
    [JsonPropertyName("ban")] public List<string> Ban { get; set; } = new();
    [JsonPropertyName("maxJars")] public int? MaxJars { get; set; }
    /// <summary>新订单暂存区全液种合计容量；省略时回退到 maxLiquids/defaultMaxLiquids。</summary>
    [JsonPropertyName("liquidStagingCapacity")] public int? LiquidStagingCapacity { get; set; }
    [JsonPropertyName("maxLiquids")] public int? MaxLiquids { get; set; }
}

/// <summary>随机工作台的名字池（内容词）。</summary>
public sealed class NamePoolOptions
{
    [JsonPropertyName("prefix")] public List<string> Prefix { get; set; } = new();
    [JsonPropertyName("suffix")] public List<string> Suffix { get; set; } = new();
}

/// <summary>
/// 订单模板里的一条需求（**数据形态**）。与 <c>Requirement</c> 一一对应，但故意不共用类型：
/// 前者属于配置（层 0），后者属于契约词汇（层 2）。
/// </summary>
public sealed class RequirementOptions
{
    [JsonPropertyName("shape")] public string Shape { get; set; } = "";

    /// <summary>null/空 = 空罐需求；否则是"装满该液体的该形状罐"。</summary>
    [JsonPropertyName("liquid")] public string? Liquid { get; set; }
}

/// <summary>一张订单模板：名字 + 类型 + 需求清单。</summary>
public sealed class OrderTemplateOptions
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>订单类型：<c>A</c>（主台独立完成）或 <c>B</c>（需要主从台转移）。</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "A";

    [JsonPropertyName("requirements")] public List<RequirementOptions> Requirements { get; set; } = new();
}

/// <summary>配置校验结果：<see cref="Errors"/> 非空应拒绝启动；<see cref="Warnings"/> 供人判断。</summary>
public sealed class WorldValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

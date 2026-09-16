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

    /// <summary>工作台清单（装配顺序 = 列表顺序 ⇒ 主从下标与契约快照的顺序）。</summary>
    [JsonPropertyName("workshops")] public List<WorkshopOptions> Workshops { get; set; } = new();

    /// <summary>随机工作台的名字池（内容词，原先是硬编码在服务里的数组）。</summary>
    [JsonPropertyName("namePools")] public NamePoolOptions NamePools { get; set; } = new();

    /// <summary>由本配置构造词表（形状/液体目录的唯一权威由此产生）。</summary>
    public Vocabulary BuildVocabulary() => new(Shapes, Liquids);

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
        for (int i = 0; i < Liquids.Count && i < Constants.LIQUIDS.Length; i++)
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
        if (Flow.JarCount < 0) r.Errors.Add("flow.jarCount 不能为负");
        if (Flow.LiquidAmount < 0) r.Errors.Add("flow.liquidAmount 不能为负");
        foreach (var kv in Flow.LiquidRate)
        {
            if (!Liquids.Contains(kv.Key)) r.Errors.Add("flow.liquidRate 含未知液体: " + kv.Key);
            if (kv.Value < 0) r.Errors.Add("flow.liquidRate[" + kv.Key + "] 不能为负");
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
                if (kv.Value < 0) r.Errors.Add("生产者 " + p.Name + " 的 " + kv.Key + " 速率不能为负");
                // 上界与核心规则一致（Constants.MAX_PRODUCER_RATE）：速率会线性放大产出循环，
                // 配置里放一个 1e9 同样能把宿主打爆 ⇒ 启动即拒绝，而不是等运行时 OOM。
                else if (kv.Value > Constants.MAX_PRODUCER_RATE) r.Errors.Add("生产者 " + p.Name + " 的 " + kv.Key
                    + " 速率不能超过 " + Constants.MAX_PRODUCER_RATE + "（每 tick 产出上限）");
                else if (kv.Value > 0 && !producedShapes.Contains(kv.Key)) producedShapes.Add(kv.Key);
            }
        }

        // ---- 工作台 ----
        var workshopNames = new List<string>();
        foreach (var w in Workshops)
        {
            if (string.IsNullOrWhiteSpace(w.Name)) r.Errors.Add("工作台缺少 name");
            else if (workshopNames.Contains(w.Name)) r.Errors.Add("工作台名重复: " + w.Name);
            else workshopNames.Add(w.Name);

            foreach (var s in w.BaseMarks) if (!Shapes.Contains(s)) r.Errors.Add("工作台 " + w.Name + " 的基础标记含未知形状: " + s);
            foreach (var s in w.AdditionalMarks) if (!Shapes.Contains(s)) r.Errors.Add("工作台 " + w.Name + " 的附加标记含未知形状: " + s);
            foreach (var s in w.BaseMarks) if (w.AdditionalMarks.Contains(s)) r.Errors.Add("工作台 " + w.Name + " 的形状 " + s + " 同时是基础标记与附加标记");
            foreach (var l in w.Ban) if (!NormalizedBan(l, Liquids)) r.Errors.Add("工作台 " + w.Name + " 的禁令含未知液体: " + l);
            if (w.MaxJars is <= 0) r.Errors.Add("工作台 " + w.Name + " 的 maxJars 必须为正数");
            if (w.MaxLiquids is <= 0) r.Errors.Add("工作台 " + w.Name + " 的 maxLiquids 必须为正数");
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
    public static WorldOptions Default { get; } = CreateDefault();

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
                LiquidRate = new Dictionary<string, int> { [Constants.LIQUIDS[0]] = 1, [Constants.LIQUIDS[1]] = 1 }
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
    [JsonPropertyName("liquidRate")] public Dictionary<string, int> LiquidRate { get; set; } = new();
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
    [JsonPropertyName("maxLiquids")] public int? MaxLiquids { get; set; }
}

/// <summary>随机工作台的名字池（内容词）。</summary>
public sealed class NamePoolOptions
{
    [JsonPropertyName("prefix")] public List<string> Prefix { get; set; } = new();
    [JsonPropertyName("suffix")] public List<string> Suffix { get; set; } = new();
}

/// <summary>配置校验结果：<see cref="Errors"/> 非空应拒绝启动；<see cref="Warnings"/> 供人判断。</summary>
public sealed class WorldValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

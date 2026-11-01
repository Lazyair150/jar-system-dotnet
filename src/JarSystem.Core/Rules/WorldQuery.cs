using System.Text.Json.Serialization;

namespace JarSystem.Core.Rules;

/// <summary>
/// **受校验的结构化世界查询**（P5）：新条框与约束建立在"对规范世界的可查询事实"之上，
/// 而不是每来一种条件就加一个端口方法。
///
/// 设计取向（重要）：
/// <list type="bullet">
/// <item>**有界词汇表**：作用域 / 主体 / 对象 / 比较符都来自下面那几个常量集合，
/// <see cref="Validate"/> 在**加载时**就能把写错的东西报出来（不需要跑起来才发现）；</item>
/// <item>**不做自由表达式**：任意路径 + 谓词求值需要一个解析器与求值器 —— 那正是本项目明确拒绝的
/// "规则引擎式过度设计"；这里的形态是"结构化查询"，扩展方式是**加词汇**，不是加重语言；</item>
/// <item>**只读、可复现**：查询只读世界事实（<see cref="IWorldFacts"/>），同一世界状态下必得同一答案。</item>
/// </list>
///
/// 数据形态示例：
/// <code>
/// { "workshop": "master", "what": "jars", "shape": "圆罐", "empty": true, "op": "&gt;=", "value": 2 }
/// </code>
/// </summary>
public sealed class WorldQuery
{
    /// <summary>可查询的对象（词汇表）。</summary>
    public static IReadOnlyList<string> WhatValues { get; } =
        Array.AsReadOnly<string>(["jars", "liquid", "warehouseJars", "warehouseLiquid", "discardedJars", "discardedLiquid"]);

    /// <summary>可用的比较符（词汇表）。</summary>
    public static IReadOnlyList<string> OpValues { get; } =
        Array.AsReadOnly<string>([">=", ">", "<=", "<", "==", "!="]);

    /// <summary>工作台主体：<c>master</c> / <c>slave</c> / 具体工作台名（对 warehouse 类对象无意义）。</summary>
    [JsonPropertyName("workshop")] public string? Workshop { get; set; }

    /// <summary>查询对象（见 <see cref="WhatValues"/>）。</summary>
    [JsonPropertyName("what")] public string What { get; set; } = "jars";

    /// <summary>形状筛选项（jars 类对象用；null 表示不限）。</summary>
    [JsonPropertyName("shape")] public string? Shape { get; set; }

    /// <summary>液体筛选项（liquid 类对象用）。</summary>
    [JsonPropertyName("liquid")] public string? Liquid { get; set; }

    /// <summary>空满筛选项（jars 类对象用；null 表示不限）。</summary>
    [JsonPropertyName("empty")] public bool? Empty { get; set; }

    /// <summary>比较符（见 <see cref="OpValues"/>）。</summary>
    [JsonPropertyName("op")] public string Op { get; set; } = ">=";

    /// <summary>阈值。</summary>
    [JsonPropertyName("value")] public int Value { get; set; }

    /// <summary>校验词汇表与必填项（加载时调用；Errors 非空应拒绝加载）。</summary>
    public void Validate(List<string> errors, string where)
    {
        if (!WhatValues.Contains(What)) errors.Add(where + "：未知 what \"" + What + "\"（可用：" + string.Join(" / ", WhatValues) + "）");
        if (!OpValues.Contains(Op)) errors.Add(where + "：未知 op \"" + Op + "\"（可用：" + string.Join(" ", OpValues) + "）");
        var needsWorkshop = What is "jars" or "liquid";
        if (needsWorkshop && string.IsNullOrWhiteSpace(Workshop))
            errors.Add(where + "：what=" + What + " 需要 workshop（master / slave / 工作台名）");
        if (What is "liquid" or "warehouseLiquid" or "discardedLiquid" && string.IsNullOrWhiteSpace(Liquid))
            errors.Add(where + "：what=" + What + " 需要 liquid");
    }

    /// <summary>
    /// 求值：条件成立返回 null；不成立返回**世界语言的原因**（可直接显示给玩家）。
    /// 取不到的事实按 0 计（<see cref="IWorldFacts"/> 的约定：实现方不得抛异常）。
    /// </summary>
    public string? Evaluate(IWorldFacts facts, string? reason = null)
    {
        int actual = What switch
        {
            "jars" => facts.WorkshopJars(Workshop ?? "", Shape, Empty),
            "liquid" => facts.WorkshopLiquid(Workshop ?? "", Liquid ?? ""),
            "warehouseJars" => facts.WarehouseJars(),
            "warehouseLiquid" => facts.WarehouseLiquid(Liquid ?? ""),
            "discardedJars" => facts.DiscardedJars(),
            "discardedLiquid" => facts.DiscardedLiquid(Liquid ?? ""),
            _ => 0
        };

        bool ok = Op switch
        {
            ">=" => actual >= Value,
            ">" => actual > Value,
            "<=" => actual <= Value,
            "<" => actual < Value,
            "==" => actual == Value,
            "!=" => actual != Value,
            _ => false
        };
        if (ok) return null;
        return (string.IsNullOrEmpty(reason) ? "条件不成立" : reason)
            + "（" + Describe() + "，当前 " + actual + "）";
    }

    /// <summary>可读描述（用于失败原因；不含"当前值"，那由 <see cref="Evaluate"/> 附上）。</summary>
    public string Describe()
    {
        var subject = What switch
        {
            "jars" => Workshop + " 的" + (Shape ?? "任意形状") + (Empty switch { true => "空罐", false => "满罐", null => "罐子" }),
            "liquid" => Workshop + " 的" + Liquid,
            "warehouseJars" => "仓库罐子",
            "warehouseLiquid" => "仓库" + Liquid,
            "discardedJars" => "累计丢弃罐子",
            "discardedLiquid" => "累计丢弃" + Liquid,
            _ => What
        };
        return subject + " " + Op + " " + Value;
    }
}

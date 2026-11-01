using System.Text.Json.Serialization;
using JarSystem.Core.Config;

namespace JarSystem.Core.Rules;

/// <summary>
/// 场景数据（P4d）：让"临时规则与事故"从代码搬到数据 —— 关卡/剧本可用 JSON 描述：
/// 开局即生效的修饰器（<see cref="InitialModifiers"/>，必带区间），以及在第 N 刻触发的
/// 事件（<see cref="Events"/>，每个事件携带若干修饰器，也必带区间）。
///
/// 边界（与设计文档一致）：数据只描述"什么规则、什么时候"，不描述"世界怎么变"（改资源仍走既有意图方法）；
/// 核心不做 IO（读写文件是宿主的事，与 WorldOptionsJson 同一分工）；
/// 数据驱动的修饰器**必须写明区间终点 to** —— 数据里没有"撤销"这个动作，若允许无终点，
/// 规则会一直挂着（代码侧仍可用作用域表达"直到撤销"）。Validate 会把它当错误。
/// </summary>
public sealed class ScenarioOptions
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("initialModifiers")] public List<ModifierData> InitialModifiers { get; set; } = new();

    [JsonPropertyName("events")] public List<EventData> Events { get; set; } = new();

    /// <summary>跨字段校验（纯逻辑、无 IO）：Errors 非空则宿主应拒绝加载。</summary>
    public ScenarioValidationResult Validate()
    {
        var r = new ScenarioValidationResult();
        var known = new[] { "suppress", "retime", "intensity", "grant", "require" };
        if (Events.Count == 0 && InitialModifiers.Count == 0)
            r.Warnings.Add("场景既没有初始修饰器也没有事件（等于空场景）");

        void Check(ModifierData m, string where)
        {
            if (string.IsNullOrWhiteSpace(m.Kind)) r.Errors.Add(where + "：kind 不能为空");
            else if (!known.Contains(m.Kind))
                r.Errors.Add(where + "：未知 kind \"" + m.Kind + "\"（数据侧支持：" + string.Join(" / ", known) + "）");
            if (m.From is < 0 || m.To is < 0)
                r.Errors.Add(where + "：from/to 不能为负（0 表示世界尚未推进）");
            if (m.To is null) r.Errors.Add(where + "：数据驱动的修饰器必须写明 to（区间终点）");
            if (m.From.HasValue && m.To.HasValue && m.From > m.To) r.Errors.Add(where + "：from 不能大于 to");
            if (m.Kind == "suppress" && m.Operation == null && m.Shape == null && m.Liquid == null)
                r.Errors.Add(where + "：suppress 至少要写 operation / shape / liquid 之一");
            if (m.Kind == "retime" && (m.TicksPerStep is null or <= 0)) r.Errors.Add(where + "：retime 需要正整数 ticksPerStep");
            if (m.Kind == "intensity")
            {
                if (string.IsNullOrWhiteSpace(m.Target) || m.FactorPerMille is null)
                    r.Errors.Add(where + "：intensity 需要 target 与 factorPerMille");
                else if (m.FactorPerMille is < 0 or > Constants.MAX_INTENSITY_FACTOR_PER_MILLE)
                    r.Errors.Add(where + "：factorPerMille 必须在 0~"
                        + Constants.MAX_INTENSITY_FACTOR_PER_MILLE + " 之间");
            }
            if (m.Kind == "grant" && string.IsNullOrWhiteSpace(m.Shape)) r.Errors.Add(where + "：grant 需要 shape");
            if (m.Kind == "require")
            {
                if (m.Condition == null) r.Errors.Add(where + "：require 需要 condition（结构化查询）");
                else m.Condition.Validate(r.Errors, where + ".condition");
            }
        }

        foreach (var m in InitialModifiers) Check(m, "initialModifiers");
        foreach (var e in Events)
        {
            if (e.AtTick <= 0) r.Errors.Add("events：atTick 必须是正数（0 = 世界尚未推进）");
            foreach (var m in e.Modifiers) Check(m, "events(" + e.Name + ")");
        }
        return r;
    }
}

/// <summary>一个修饰器的数据形态（键名即数据契约）。</summary>
public sealed class ModifierData
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "关卡";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("operation")] public string? Operation { get; set; }
    [JsonPropertyName("shape")] public string? Shape { get; set; }
    [JsonPropertyName("liquid")] public string? Liquid { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("factorPerMille")] public int? FactorPerMille { get; set; }
    [JsonPropertyName("ticksPerStep")] public int? TicksPerStep { get; set; }
    [JsonPropertyName("processKind")] public string? ProcessKind { get; set; }
    [JsonPropertyName("from")] public int? From { get; set; }
    [JsonPropertyName("to")] public int? To { get; set; }

    /// <summary>require 专用：**结构化查询**（受校验的词汇表，见 <see cref="WorldQuery"/>）。</summary>
    [JsonPropertyName("condition")] public WorldQuery? Condition { get; set; }
}

/// <summary>一个事件的数据形态：在第 <see cref="AtTick"/> 刻发生，并把它的修饰器压进栈。</summary>
public sealed class EventData
{
    [JsonPropertyName("atTick")] public int AtTick { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("modifiers")] public List<ModifierData> Modifiers { get; set; } = new();
}

/// <summary>校验结果：Errors 非空应拒绝加载；Warnings 供人判断。</summary>
public sealed class ScenarioValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

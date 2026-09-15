using System.Collections.Generic;
using JarSystem.Core.Config;

namespace JarSystem.Core.Domain;

/// <summary>
/// 工作台的「能力参数」（规则侧）：基础标记 / 附加标记 / 禁令。
///
/// 这就是**"配置装配工作台"在内存里的那份配置本身**：不可变值对象 ⇒
/// <see cref="Workshop"/>、<see cref="WorkshopSnapshot"/>、<c>Solver.WorkshopInfo</c>
/// 可以**共用同一份**，不必三处各存一份标记表（S1：消除重复）。
///
/// 语义（与重构前逐字一致，勿改）：
/// <list type="bullet">
/// <item>禁令在构造时规范化为**裸液体名**（去掉「禁」前缀），避免 "酒" 与 "禁酒" 两种写法并存；</item>
/// <item>形状转换**公式**属于核心（在 <see cref="ShapeRules"/>），本类只提供**参数**；
/// 因此加/减形状不需要改规则引擎，只需换一份 <see cref="WorkshopCapability"/>。</item>
/// </list>
/// </summary>
public sealed class WorkshopCapability
{
    /// <summary>基础标记（只读）。</summary>
    public IReadOnlyList<string> BaseMarks { get; }

    /// <summary>附加标记（只读；不含 Case2 作用域内的临时标记——那属于 <see cref="Workshop"/> 的运行时状态）。</summary>
    public IReadOnlyList<string> AdditionalMarks { get; }

    /// <summary>禁令（只读；构造时已规范化为裸液体名）。</summary>
    public IReadOnlyList<string> Ban { get; }

    public WorkshopCapability(
        IEnumerable<string>? baseMarks = null,
        IEnumerable<string>? additionalMarks = null,
        IEnumerable<string>? ban = null)
    {
        BaseMarks = new List<string>(baseMarks ?? []);
        AdditionalMarks = new List<string>(additionalMarks ?? []);
        Ban = NormalizeBan(ban);
    }

    public bool IsBaseMarked(string shape) => BaseMarks.Contains(shape);

    public bool IsAdditionalMarked(string shape) => AdditionalMarks.Contains(shape);

    public bool IsBanned(string liquid) => Ban.Contains(liquid) || Ban.Contains("禁" + liquid);

    /// <summary>本能力下，形状 <paramref name="shape"/> 可以变成哪些形状（委托 <see cref="ShapeRules"/>）。</summary>
    /// <param name="extraAdditionalMarks">额外附加标记（Case2 作用域内的临时标记；null/空视为无）。</param>
    public List<string> GetPossibleTransforms(string shape, IReadOnlyList<string>? extraAdditionalMarks = null)
    {
        var additional = extraAdditionalMarks == null || extraAdditionalMarks.Count == 0
            ? AdditionalMarks
            : Concat(AdditionalMarks, extraAdditionalMarks);
        return ShapeRules.GetPossibleTransforms(BaseMarks, additional, shape);
    }

    /// <summary>是否可从 <paramref name="from"/> 转换为 <paramref name="to"/>。</summary>
    public bool CanTransform(string from, string to, IReadOnlyList<string>? extraAdditionalMarks = null) =>
        GetPossibleTransforms(from, extraAdditionalMarks).Contains(to);

    private static List<string> Concat(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var merged = new List<string>(a);
        merged.AddRange(b);
        return merged;
    }

    // 禁令统一存裸液体名（去掉「禁」前缀），避免 "酒" 与 "禁酒" 两种写法并存
    private static List<string> NormalizeBan(IEnumerable<string>? bans)
    {
        var r = new List<string>();
        if (bans == null) return r;
        foreach (var b in bans)
        {
            var v = b.Trim();
            if (v.StartsWith("禁") && v.Length > 1) v = v[1..];
            if (!r.Contains(v)) r.Add(v);
        }
        return r;
    }
}

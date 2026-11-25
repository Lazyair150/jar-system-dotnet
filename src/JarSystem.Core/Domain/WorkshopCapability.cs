using System;
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
/// 语义：
/// <list type="bullet">
/// <item>禁令在构造时规范化为**裸液体名**（去掉「禁」前缀），避免 "酒" 与 "禁酒" 两种写法并存；</item>
/// <item>Ban 约束暂存液体身份；工作台允许操作的 Jar 极性由本世界的身份映射与 Ban 共同推导；</item>
/// <item>同极 Jar 可操作，异极 Jar 不可；Neutral Jar 对两种专注工作台均可操作；</item>
/// <item>形状转换**公式**属于核心（在 <see cref="ShapeRules"/>），本类只提供**参数**；
/// 因此加/减形状不需要改规则引擎，只需换一份 <see cref="WorkshopCapability"/>。</item>
/// </list>
/// </summary>
public sealed class WorkshopCapability
{
    // 私有 List + ReadOnlyCollection 包装（与 <c>Vocabulary</c> 同一手法，2026-09-16 补）。
    // 原先直接把 `new List<string>(...)` 交给 IReadOnlyList 属性：**可以向下转型就地改写**
    // （`((List<string>)cap.Ban).Add("酒")`），而本对象被 工作台 / 快照(Solver.WorkshopInfo) **共用** ——
    // 改一处等于同时改三处的规则。原注释声称"不可变值对象"，只有把包装补上这句话才成立。
    private readonly List<string> _baseMarks;
    private readonly List<string> _additionalMarks;
    private readonly List<string> _ban;
    private readonly Vocabulary _vocabulary;

    private readonly IReadOnlyList<string> _baseMarksView;
    private readonly IReadOnlyList<string> _additionalMarksView;
    private readonly IReadOnlyList<string> _banView;

    /// <summary>基础标记（只读）。</summary>
    public IReadOnlyList<string> BaseMarks => _baseMarksView;

    /// <summary>附加标记（只读；不含 Case2 作用域内的临时标记——那属于 <see cref="Workshop"/> 的运行时状态）。</summary>
    public IReadOnlyList<string> AdditionalMarks => _additionalMarksView;

    /// <summary>禁令（只读；构造时已规范化为裸液体名）。</summary>
    public IReadOnlyList<string> Ban => _banView;

    public WorkshopCapability(
        IEnumerable<string>? baseMarks = null,
        IEnumerable<string>? additionalMarks = null,
        IEnumerable<string>? ban = null,
        Vocabulary? vocabulary = null)
    {
        _vocabulary = vocabulary ?? Vocabulary.Default;
        _baseMarks = new List<string>(baseMarks ?? []);
        _additionalMarks = new List<string>(additionalMarks ?? []);
        _ban = NormalizeBan(ban);
        _baseMarksView = _baseMarks.AsReadOnly();
        _additionalMarksView = _additionalMarks.AsReadOnly();
        _banView = _ban.AsReadOnly();
    }

    public WorkshopCapability WithVocabulary(Vocabulary vocabulary) =>
        new(_baseMarks, _additionalMarks, _ban, vocabulary);

    public bool IsBaseMarked(string shape) => BaseMarks.Contains(shape);

    public bool IsAdditionalMarked(string shape) => AdditionalMarks.Contains(shape);

    public bool IsBanned(string liquid) => Ban.Contains(liquid) || Ban.Contains("禁" + liquid);

    /// <summary>Liquid identities known to this World; Ban is a staging-account policy only.</summary>
    public IReadOnlyList<string> Liquids => _vocabulary.Liquids;

    /// <summary>
    /// Kept as a compatibility projection. A liquid Ban does not define a Jar-operation
    /// polarity restriction, so workshop polarity remains unrestricted.
    /// </summary>
    public WorkshopPolarityMode PolarityMode => WorkshopPolarityMode.Unrestricted;

    /// <summary>Ban never restricts possession or operation of a Jar's internal liquid.</summary>
    public bool CanOperateJar(JarPolarity polarity) => true;

    public static bool CanOperateJar(WorkshopPolarityMode mode, JarPolarity polarity) => mode switch
    {
        WorkshopPolarityMode.Unrestricted => true,
        WorkshopPolarityMode.NeutralOnly => polarity == JarPolarity.Neutral,
        WorkshopPolarityMode.Cathode => polarity is JarPolarity.Neutral or JarPolarity.Cathode,
        WorkshopPolarityMode.Anode => polarity is JarPolarity.Neutral or JarPolarity.Anode,
        _ => false
    };

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

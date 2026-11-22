using System;

namespace JarSystem.Core.Domain;

/// <summary>
/// 「动用工作台游离液体」的回应（原则4：规则 = 概念体的能力）。
/// 本类型提供两种明确的禁液检查：工作台游离液体的取用/存入，以及工作台对含液 Jar 的操作许可。
/// Jar 内部液体不计入游离液体库存，但其液体种类仍受当前工作台的 Jar 操作禁令约束。
///
/// <see cref="Code"/> 是稳定码（给程序看），<see cref="Reason"/> 是给人看的一句话。
/// 两者都不随调用方变化 —— 这正是把规则从"各处 if"收成"一个能力"的目的：
/// 谁问都是同一句回答，改规则只需要改这里。
/// </summary>
public readonly struct LiquidGrant
{
    internal LiquidGrant(bool granted, string? code, string? reason)
    {
        Granted = granted;
        Code = code;
        Reason = reason;
    }

    public bool Granted { get; }

    /// <summary><c>banned</c> / <c>insufficient</c> / <c>over-capacity</c> /（允许时为 null）</summary>
    public string? Code { get; }

    public string? Reason { get; }

    public static LiquidGrant Yes() => new(true, null, null);

    internal static LiquidGrant Banned(string liquid) =>
        new(false, "banned", "工作台禁" + liquid);

    internal static LiquidGrant Insufficient(string liquid) =>
        new(false, "insufficient", "液体" + liquid + "不足");

    internal static LiquidGrant OverCapacity(string liquid) =>
        new(false, "over-capacity", "液体" + liquid + "存储已满");
}

/// <summary>
/// 液体规则的**纯值**表述：禁令表 + 每液容量。无状态、可复制、可跨层传递。
///
/// 为什么要单独有这么个东西：<see cref="Workshop"/> 是有状态的（池子里有多少），
/// 而 <see cref="WorkshopRulebook"/>、<c>Executor.GreedyPlanBuilder</c>、求解器是**静态/纯值**的，
/// 它们拿不到活的 Workshop。于是过去它们各自拼一遍 "IsBanned + 数量 + 上限"，
/// 同一个规则有五六个说法 —— 一改就漏。
///
/// 现在三分：<b>规则</b>在 <see cref="LiquidRules"/>（纯函数），<b>库存</b>由调用方给（<c>have</c>），
/// <b>有状态的门面</b>是 <see cref="Workshop"/> 上的三个能力（它把自己的库存喂进来）。
///
/// <para><b>工作台禁液作用域</b>：游离液体不能被该台取用或存入；
/// 已在 Jar 内部的液体不属于游离液体账目，但只要 Jar 留在该台，该台也不能对这只 Jar 执行操作。
/// 资源迁移本身不是 Jar 操作，迁移后由接收工作台的禁令重新决定能否操作。</para>
/// </summary>
public readonly struct LiquidRules
{
    private readonly WorkshopCapability? _capability;
    private readonly int _capacity;

    /// <param name="capability">禁令来源（可为 null = 无禁令）。</param>
    /// <param name="capacity">每种液体的容量上限；&lt;= 0 表示不设上限。</param>
    public LiquidRules(WorkshopCapability? capability, int capacity)
    {
        _capability = capability;
        _capacity = capacity;
    }

    /// <summary>无禁令、无容量限制（用于"只看规则不问账目"的场景）。</summary>
    public static LiquidRules Unlimited => new(null, 0);

    /// <summary>禁令门：这整种液体在本台是否不可用。</summary>
    public bool IsBanned(string liquid) => _capability?.IsBanned(liquid) ?? false;

    /// <summary>工作台游离液体是否可取用。</summary>
    public LiquidGrant Check(string liquid) =>
        IsBanned(liquid) ? LiquidGrant.Banned(liquid) : LiquidGrant.Yes();

    /// <summary>
    /// 当前工作台是否允许对内部装有该液体的 Jar 执行操作。
    /// 这是 Jar 操作许可，不是从游离液体池扣账；Jar 的主从/工作台迁移不调用此检查。
    /// </summary>
    public LiquidGrant CheckJarOperation(string liquid) =>
        IsBanned(liquid) ? LiquidGrant.Banned(liquid) : LiquidGrant.Yes();

    /// <summary>能不能取出 <paramref name="amount"/> 份（禁令 + 库存）。</summary>
    public LiquidGrant CanPay(string liquid, int amount, int have)
    {
        var touch = Check(liquid);
        if (!touch.Granted) return touch;
        if (amount <= 0) return LiquidGrant.Yes();
        return have >= amount ? LiquidGrant.Yes() : LiquidGrant.Insufficient(liquid);
    }

    /// <summary>能不能存入工作台游离液体池（禁令 + 容量）。</summary>
    public LiquidGrant CanStore(string liquid, int amount, int have)
    {
        var policy = Check(liquid);
        if (!policy.Granted) return policy;
        if (amount <= 0) return LiquidGrant.Yes();
        if (_capacity <= 0) return LiquidGrant.Yes();
        return have + amount <= _capacity ? LiquidGrant.Yes() : LiquidGrant.OverCapacity(liquid);
    }
}

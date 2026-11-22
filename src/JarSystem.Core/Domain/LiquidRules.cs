using System;

namespace JarSystem.Core.Domain;

/// <summary>
/// 「动用工作台游离液体」的回应（原则4：规则 = 概念体的能力）。
/// 本类型只评估工作台游离液体池；Jar 内部液体不属于该池，不得送入本规则检查。
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
/// <para><b>存储与取用是两条规则</b>（2026-11 定案）：禁令只约束工作台游离液体的<b>取用</b>（倒入罐子、支付空罐改形成本），<b>不</b>约束<b>存储</b> —— 被禁的液体可以流进工作台、留在池子里（那一格仍然存在），
/// 只是取不出来。于是 <see cref="CanStore"/> 不再问禁令，<see cref="CanPay"/>/<see cref="Check"/> 仍然问。</para>
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

    /// <summary>工作台游离液体是否可取用（只答 staging ban）。</summary>
    public LiquidGrant Check(string liquid) =>
        IsBanned(liquid) ? LiquidGrant.Banned(liquid) : LiquidGrant.Yes();

    /// <summary>能不能取出 <paramref name="amount"/> 份（禁令 + 库存）。</summary>
    public LiquidGrant CanPay(string liquid, int amount, int have)
    {
        var touch = Check(liquid);
        if (!touch.Granted) return touch;
        if (amount <= 0) return LiquidGrant.Yes();
        return have >= amount ? LiquidGrant.Yes() : LiquidGrant.Insufficient(liquid);
    }

    /// <summary>能不能放进 <paramref name="amount"/> 份（**只问容量**：禁液也能存进来，只是取不出来）。</summary>
    public LiquidGrant CanStore(string liquid, int amount, int have)
    {
        if (amount <= 0) return LiquidGrant.Yes();
        if (_capacity <= 0) return LiquidGrant.Yes();
        return have + amount <= _capacity ? LiquidGrant.Yes() : LiquidGrant.OverCapacity(liquid);
    }
}

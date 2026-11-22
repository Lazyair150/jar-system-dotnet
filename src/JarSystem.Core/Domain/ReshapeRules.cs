using System.Collections.Generic;

namespace JarSystem.Core.Domain;

/// <summary>
/// 改形规则的**纯值**：方向（标记集合）、空罐改形的成本、满罐自耗的液体，各一个提问。
///
/// <para>为什么要它：改形有三条规则，原先分别写在两处 —— 活工作台
/// （<see cref="Workshop.CanReshapeTo"/>，带中文理由）与规则表
/// （<see cref="WorkshopRulebook.IsAllowed"/> 的 ReshapeEmpty / ReshapeFull 分支，只回 bool）。
/// 两边一旦有人只改一处，规则表就会开始"承诺做不到的事"或"否认做得到的事"，
/// 而那正是差分对拍反复抓到过的形态。</para>
///
/// <para>术语边界：<c>marked</c>（一罐一操作）<b>不在这里</b> —— 它属于订单阶段的状态，
/// 由门面各自先判；本类型只管"工作台允许这只罐子做什么"。</para>
/// </summary>
public readonly struct ReshapeRules
{
    private readonly WorkshopCapability? _capability;
    private readonly RuleContext? _context;
    private readonly LiquidRules _liquid;

    public ReshapeRules(WorkshopCapability? capability, LiquidRules liquid)
    {
        _capability = capability;
        _context = null;
        _liquid = liquid;
    }

    public ReshapeRules(RuleContext context, LiquidRules liquid)
    {
        _context = context ?? throw new System.ArgumentNullException(nameof(context));
        _capability = context.Capability;
        _liquid = liquid;
    }

    /// <summary>方向：从 <paramref name="from"/> 能否改成 <paramref name="to"/>（基础形状规则 + 当前 Grant 修饰）。</summary>
    public bool CanTransform(string from, string to) =>
        _context?.CanTransform(from, to) ?? (_capability?.CanTransform(from, to) ?? false);

    /// <summary>
    /// 空罐改形的成本。
    /// 指定了 <paramref name="costLiquid"/> 就必须付得起**那一种**（换一种会静默改账）；
    /// 没指定则按池子键序挑第一份能付的 —— 顺序确定，所以可回放。
    /// </summary>
    public ReshapeGrant PayCost(string? costLiquid, IReadOnlyDictionary<string, int> pool)
    {
        if (costLiquid != null)
            return _liquid.CanPay(costLiquid, 1, pool.GetValueOrDefault(costLiquid)).Granted
                ? ReshapeGrant.Yes()
                : ReshapeGrant.No("no-cost-liquid", "无法支付液体" + costLiquid);

        foreach (var l in pool.Keys)
            if (_liquid.CanPay(l, 1, pool[l]).Granted) return ReshapeGrant.Yes();
        return ReshapeGrant.No("no-cost-liquid", "没有可支付的改形成本液体");
    }

    /// <summary>
    /// 满罐自耗：Jar 内液体不从工作台游离液体池扣账，但当前工作台仍必须允许
    /// 操作含有该液体的 Jar。资源迁移不调用此规则，迁移后由新工作台重新判定。
    /// </summary>
    public ReshapeGrant SelfConsume(string? liquidInJar)
    {
        if (liquidInJar == null) return ReshapeGrant.No("not-full", "必须为满罐");
        var grant = _liquid.CheckJarOperation(liquidInJar);
        return grant.Granted ? ReshapeGrant.Yes() : ReshapeGrant.No("banned", grant.Reason);
    }
}

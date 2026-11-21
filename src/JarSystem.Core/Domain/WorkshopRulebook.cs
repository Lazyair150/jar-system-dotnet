using System.Collections.Generic;

namespace JarSystem.Core.Domain;

/// <summary>工作台能对罐子做的四件事（与 <see cref="Workshop"/> 的四个执行方法一一对应）。</summary>
public enum WorkshopOperationKind
{
    /// <summary>空罐灌入某液体（消耗工作台游离液体 1 单位）。</summary>
    PourIn,

    /// <summary>满罐倒出（产出工作台游离液体 1 单位）。</summary>
    PourOut,

    /// <summary>空罐改形（消耗 1 单位游离液体作成本）。</summary>
    ReshapeEmpty,

    /// <summary>满罐改形（罐内液体自耗，结果为空罐）。</summary>
    ReshapeFull
}

/// <summary>一次"想对罐子做什么"的请求。只描述意图，不持有罐子对象。</summary>
public readonly struct WorkshopOperation
{
    private WorkshopOperation(WorkshopOperationKind kind, string? targetShape, string? targetLiquid, string? costLiquid)
    {
        Kind = kind;
        TargetShape = targetShape;
        TargetLiquid = targetLiquid;
        CostLiquid = costLiquid;
    }

    public WorkshopOperationKind Kind { get; }

    /// <summary>改形的目标形状（<see cref="WorkshopOperationKind.ReshapeEmpty"/> / <see cref="WorkshopOperationKind.ReshapeFull"/>）。</summary>
    public string? TargetShape { get; }

    /// <summary>灌入的液体（<see cref="WorkshopOperationKind.PourIn"/>）。</summary>
    public string? TargetLiquid { get; }

    /// <summary>改形的成本液体（<see cref="WorkshopOperationKind.ReshapeEmpty"/>）。</summary>
    public string? CostLiquid { get; }

    public static WorkshopOperation PourIn(string liquid) =>
        new(WorkshopOperationKind.PourIn, null, liquid, null);

    public static WorkshopOperation PourOut() =>
        new(WorkshopOperationKind.PourOut, null, null, null);

    public static WorkshopOperation ReshapeEmpty(string targetShape, string costLiquid) =>
        new(WorkshopOperationKind.ReshapeEmpty, targetShape, null, costLiquid);

    public static WorkshopOperation ReshapeFull(string targetShape) =>
        new(WorkshopOperationKind.ReshapeFull, targetShape, null, null);

    public override string ToString() => Kind switch
    {
        WorkshopOperationKind.PourIn => "pour_in(" + TargetLiquid + ")",
        WorkshopOperationKind.PourOut => "pour_out",
        WorkshopOperationKind.ReshapeEmpty => "reshape_empty(" + TargetShape + ", cost=" + CostLiquid + ")",
        _ => "reshape_full(" + TargetShape + ")"
    };
}

/// <summary>罐子的规则相关状态（不含身份与词表：规则不看这些）。</summary>
public readonly struct JarFacts
{
    public JarFacts(string shape, string? liquid, bool isMarked)
    {
        Shape = shape;
        Liquid = liquid;
        IsMarked = isMarked;
    }

    public string Shape { get; }
    public string? Liquid { get; }
    public bool IsMarked { get; }

    public static JarFacts Of(Jar jar) => new(jar.Shape, jar.Liquid, jar.IsMarked);
}

/// <summary>
/// **工作台规则表**：给一处"罐子 + 工作台"状态，回答"哪些操作合法、哪些不合法"。
///
/// <para><b>它是什么、不是什么</b>：
/// 它<b>不执行任何操作</b>，也不持有罐子对象——只把 <c>Workshop.PourIn/PourOut/ReshapeEmpty/ReshapeFull</c>
/// 里那几道判定按同样的顺序重述一遍，输入是纯事实（形状、液体、是否已被操作、各液体库存、上限）。
/// 也就是说：它是这些规则的**可调用形态**，而不是另一套规则。</para>
///
/// <para><b>为什么执行器与求解器"不必"调它</b>（这是刻意的）：
/// 求解器（<c>Solver/StateSpaceSolver</c>）跑的是几十万节点的热路径，让它每节点调这张表会把
/// 描述符分配压进搜索预算；而它本来就知道规则（<c>IsBanned</c>/<c>IsBaseMarked</c>/<c>CanTransform</c>
/// 三个判定 + 库存与转移预算）。所以这里<b>不强制</b>任何一方调用本表。</para>
///
/// <para><b>那它存在的意义</b>：把"两边都知道同一套规则"从<b>口头约定</b>变成<b>可执行断言</b>。
/// 只要有一条测试对每个 (罐子, 工作台, 候选操作) 断言
/// 「<c>WorkshopRulebook.IsAllowed</c> 与 <c>Workshop.xxx</c> 的执行结果一致」，
/// 那么任何一方单独改动规则都会被立刻抓住——这正是过去三次同类错误的形状
/// （原型漏了"形状必须是基础标记"、前置检查把禁令挡在 MATCH 前、以及最初那 105 例错杀）。
/// 见 <c>test/JarSystem.Tests/WorkshopRulebookTests.cs</c>。</para>
///
/// <para><b>与修饰器的关系</b>：判定走 <see cref="RuleContext"/>，因此抑制类修饰器
/// （<c>Suppress</c>）与授予类修饰器（<c>Grant</c>）自动生效；空栈时与改造前逐项相同。</para>
/// </summary>
public static class WorkshopRulebook
{
    /// <summary>
    /// 这一操作**现在**是否合法。判定顺序与 <see cref="Workshop"/> 的四个执行方法逐条对齐，
    /// 因此"合法性"与"执行会不会成功"是同一个谓词的两面（由测试钉住）。
    /// </summary>
    /// <param name="rules">规则上下文（基础法则 + 修饰器）。</param>
    /// <param name="maxLiquids">每种游离液体的容量上限。</param>
    /// <param name="jar">罐子的规则相关状态。</param>
    /// <param name="op">候选操作。</param>
    /// <param name="liquids">工作台当前的游离液体账目（每种液体各自的量）。</param>
    public static bool IsAllowed(RuleContext rules, int maxLiquids, in JarFacts jar,
        in WorkshopOperation op, IReadOnlyDictionary<string, int> liquids)
    {
        // 液体与改形的规则各只有一份：LiquidRules / ReshapeRules。
        // 以前这里和 Workshop / GreedyPlanBuilder 各自拼一遍（改动一处分家三处）。
        var liquidRules = new LiquidRules(rules.Capability, maxLiquids);
        var reshapeRules = new ReshapeRules(rules.Capability, liquidRules);

        // 四个执行方法共同的第二道门（第一道是"罐子存在"）：已被操作过的罐子一律不能再动
        // （"一罐一操作"就是靠它落地的，见 Workshop.cs 各方法的首行）
        if (jar.IsMarked) return false;

        switch (op.Kind)
        {
            case WorkshopOperationKind.PourIn:
            {
                var liquid = op.TargetLiquid;
                if (liquid == null) return false;
                if (jar.Liquid != null) return false;                       // 罐子必须为空
                if (!rules.IsBaseMarked(jar.Shape)) return false;           // 形状必须是本台基础标记
                if (rules.SuppressionReason("POUR_IN", jar.Shape, liquid) != null) return false;
                return liquidRules.CanPay(liquid, 1, Count(liquids, liquid)).Granted;   // 禁令 + 游离液体够付
            }

            case WorkshopOperationKind.PourOut:
            {
                var liquid = jar.Liquid;
                if (liquid == null) return false;                           // 罐子必须为满
                if (rules.SuppressionReason("POUR_OUT", jar.Shape, liquid) != null) return false;
                // 倒出要**两问**：①这种液体在本台能不能取用（禁令）；②池子里这一格还放不放得下（容量）。
                // 2026-11 起 CanStore 只答后者（存储不再受禁令约束），所以禁令必须在这里显式问一次 ——
                // 否则"被禁的酒能倒出"会变成规则表的错误承诺（差分对拍当场抓到了这一条）。
                if (!liquidRules.Check(liquid).Granted) return false;
                return liquidRules.CanStore(liquid, 1, Count(liquids, liquid)).Granted;
            }

            case WorkshopOperationKind.ReshapeEmpty:
            {
                var target = op.TargetShape;
                var cost = op.CostLiquid;
                if (target == null || cost == null) return false;
                if (jar.Liquid != null) return false;                       // 必须为空罐
                if (!reshapeRules.CanTransform(jar.Shape, target)) return false;
                if (rules.SuppressionReason("RESHAPE", jar.Shape, cost) != null) return false;
                return reshapeRules.PayCost(cost, liquids).Granted;               // 禁令 + 成本够付
            }

            case WorkshopOperationKind.ReshapeFull:
            {
                var target = op.TargetShape;
                if (target == null) return false;
                var liquid = jar.Liquid;
                if (liquid == null) return false;                           // 必须为满罐
                if (!reshapeRules.CanTransform(jar.Shape, target)) return false;
                if (rules.SuppressionReason("RESHAPE", jar.Shape, liquid) != null) return false;
                // 罐内液体被禁 ⇒ 改形会"把被禁液体凭空消化掉"，因此整条拒绝
                // （执行侧是把液体取出来发现被禁再回滚，结果同样是失败）
                return reshapeRules.SelfConsume(liquid).Granted;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 列出现在**全部**合法操作（按固定顺序：倒出 → 灌入 → 空罐改形 → 满罐改形）。
    ///
    /// <para>顺序固定是为了可复现：同一状态永远给出同一个序列，测试可以逐项比对。</para>
    /// </summary>
    public static List<WorkshopOperation> Available(RuleContext rules, int maxLiquids, in JarFacts jar,
        IReadOnlyDictionary<string, int> liquids)
    {
        var list = new List<WorkshopOperation>(8);

        if (jar.IsMarked) return list;

        if (IsAllowed(rules, maxLiquids, jar, WorkshopOperation.PourOut(), liquids))
            list.Add(WorkshopOperation.PourOut());

        foreach (var liquid in Config.Constants.LIQUIDS)
        {
            var op = WorkshopOperation.PourIn(liquid);
            if (IsAllowed(rules, maxLiquids, jar, op, liquids)) list.Add(op);
        }

        var targets = rules.GetPossibleTransforms(jar.Shape);
        foreach (var target in targets)
        {
            foreach (var cost in Config.Constants.LIQUIDS)
            {
                var op = WorkshopOperation.ReshapeEmpty(target, cost);
                if (IsAllowed(rules, maxLiquids, jar, op, liquids)) list.Add(op);
            }

            var full = WorkshopOperation.ReshapeFull(target);
            if (IsAllowed(rules, maxLiquids, jar, full, liquids)) list.Add(full);
        }

        return list;
    }

    /// <summary>
    /// 订单暂存版本的规则判定。容量使用工作台所有订单子账的合计剩余空间，
    /// 库存消耗只读取当前订单子账。
    /// </summary>
    public static bool IsAllowed(RuleContext rules, OrderLiquidScope scope, in JarFacts jar,
        in WorkshopOperation op)
    {
        if (scope == null) throw new System.ArgumentNullException(nameof(scope));
        var liquids = scope.Balance;
        var liquidRules = new LiquidRules(rules.Capability, 0);
        var reshapeRules = new ReshapeRules(rules.Capability, liquidRules);

        if (jar.IsMarked) return false;

        switch (op.Kind)
        {
            case WorkshopOperationKind.PourIn:
            {
                var liquid = op.TargetLiquid;
                if (liquid == null || jar.Liquid != null || !rules.IsBaseMarked(jar.Shape)) return false;
                if (rules.SuppressionReason("POUR_IN", jar.Shape, liquid) != null) return false;
                return liquidRules.CanPay(liquid, 1, Count(liquids, liquid)).Granted;
            }
            case WorkshopOperationKind.PourOut:
            {
                var liquid = jar.Liquid;
                if (liquid == null) return false;
                if (rules.SuppressionReason("POUR_OUT", jar.Shape, liquid) != null) return false;
                return scope.CanReceiveOperation(liquid, 1).Granted;
            }
            case WorkshopOperationKind.ReshapeEmpty:
            {
                var target = op.TargetShape;
                var cost = op.CostLiquid;
                if (target == null || cost == null || jar.Liquid != null) return false;
                if (!reshapeRules.CanTransform(jar.Shape, target)) return false;
                if (rules.SuppressionReason("RESHAPE", jar.Shape, cost) != null) return false;
                return reshapeRules.PayCost(cost, liquids).Granted;
            }
            case WorkshopOperationKind.ReshapeFull:
            {
                var target = op.TargetShape;
                var liquid = jar.Liquid;
                if (target == null || liquid == null) return false;
                if (!reshapeRules.CanTransform(jar.Shape, target)) return false;
                if (rules.SuppressionReason("RESHAPE", jar.Shape, liquid) != null) return false;
                return reshapeRules.SelfConsume(liquid).Granted;
            }
            default:
                return false;
        }
    }

    /// <summary>列出订单暂存上下文下全部合法操作，顺序与旧 Available 保持一致。</summary>
    public static List<WorkshopOperation> Available(RuleContext rules, OrderLiquidScope scope,
        in JarFacts jar)
    {
        var list = new List<WorkshopOperation>(8);
        if (jar.IsMarked) return list;

        if (IsAllowed(rules, scope, jar, WorkshopOperation.PourOut()))
            list.Add(WorkshopOperation.PourOut());

        foreach (var liquid in Config.Constants.LIQUIDS)
        {
            var op = WorkshopOperation.PourIn(liquid);
            if (IsAllowed(rules, scope, jar, op)) list.Add(op);
        }

        var targets = rules.GetPossibleTransforms(jar.Shape);
        foreach (var target in targets)
        {
            foreach (var cost in Config.Constants.LIQUIDS)
            {
                var op = WorkshopOperation.ReshapeEmpty(target, cost);
                if (IsAllowed(rules, scope, jar, op)) list.Add(op);
            }

            var full = WorkshopOperation.ReshapeFull(target);
            if (IsAllowed(rules, scope, jar, full)) list.Add(full);
        }

        return list;
    }

    private static int Count(IReadOnlyDictionary<string, int> liquids, string liquid) =>
        liquids.TryGetValue(liquid, out var n) ? n : 0;
}

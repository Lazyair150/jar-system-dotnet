using System.Collections.Generic;

namespace JarSystem.Core.Domain;

/// <summary>工作台对 Jar 执行的化学转换算子；MATCH 是独立的生命周期规则，不属于此枚举。</summary>
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

    /// <summary>Three-state classification derived from the authoritative internal liquid identity.</summary>
    public JarPolarity Polarity => JarPolarityResolver.Resolve(Liquid);

    public static JarFacts Of(Jar jar) => new(jar.Shape, jar.Liquid, jar.IsMarked);
}

/// <summary>
/// **工作台规则表**：给一处"罐子 + 工作台"状态，回答"哪些操作合法、哪些不合法"。
///
/// <para><b>它是什么、不是什么</b>：
/// 它不持有 Jar/Workshop 实体，也不直接记账；它对纯事实执行规则评估，并产生纯值 Jar 终态与资源效果。
/// 普通工作台库存和订单暂存是两种资源边界，但算子适用条件只在 <see cref="EvaluateCore"/> 定义一次。</para>
///
/// <para><b>当前接入边界</b>：StateSpaceSolver、Workshop 实际执行、订单候选投影与 MATCH 均消费本表的评估/转换结果。
/// 资源账户仍由所属事务边界扣除或接收；规则表不持有账户，也不提交外部副作用。
/// 规则目录目前是稳定元数据目录，新增算子仍需扩展操作类型与评估/转换实现；它尚不是可热插拔的规则插件系统。</para>
///
/// <para><b>一致性基线</b>：测试对每个 (罐子, 工作台, 候选操作) 断言
/// 「<c>WorkshopRulebook.IsAllowed</c> 与 <c>Workshop.xxx</c> 的执行结果一致」，
/// 并验证纯转换描述的 Jar 终态与资源效果。任何一方改动规则都必须同步通过该对拍。
/// 见 <c>test/JarSystem.Tests/WorkshopRulebookTests.cs</c>。</para>
///
/// <para><b>与修饰器的关系</b>：判定走 <see cref="RuleContext"/>，因此抑制类修饰器
/// （<c>Suppress</c>）与授予类修饰器（<c>Grant</c>）自动生效；空栈时与改造前逐项相同。</para>
/// </summary>
public enum WorkshopRuleDecisionKind
{
    Allowed,
    Rejected,
    Undetermined
}

/// <summary>
/// 规则评估的稳定结果。ReasonCode 是机器可读契约；Reason 仅用于展示/诊断。
/// Undetermined 表示当前上下文不足以证明允许或拒绝，调用方不得把它折叠成 Infeasible。
/// </summary>
public readonly record struct WorkshopRuleDecision(
    WorkshopRuleDecisionKind Kind,
    string ReasonCode,
    string? Reason)
{
    public bool IsAllowed => Kind == WorkshopRuleDecisionKind.Allowed;
    public bool IsRejected => Kind == WorkshopRuleDecisionKind.Rejected;
    public bool IsUndetermined => Kind == WorkshopRuleDecisionKind.Undetermined;

    public static WorkshopRuleDecision Allow() =>
        new(WorkshopRuleDecisionKind.Allowed, "rule.allowed", null);

    public static WorkshopRuleDecision Reject(string code, string reason) =>
        new(WorkshopRuleDecisionKind.Rejected, code, reason);

    public static WorkshopRuleDecision Undetermined(string code, string reason) =>
        new(WorkshopRuleDecisionKind.Undetermined, code, reason);
}

public enum WorkshopRuleCategory
{
    Transform,
    Lifecycle
}

/// <summary>可审计的稳定规则目录项；RuleId 不依赖 CLR 类型名或 UI 文案。</summary>
public sealed record WorkshopRuleDefinition(
    string RuleId,
    int RuleVersion,
    string Operator,
    WorkshopRuleCategory Category);

public static class WorkshopRulebook
{
    /// <summary>基础工作台规则语义版本；改变规则含义时必须递增。</summary>
    public const int RuleContractVersion = 4;

    /// <summary>
    /// 当前工作台算子规则目录。新增算子必须先注册稳定身份/版本，再实现评估与转换；
    /// 改变既有算子语义必须递增对应 RuleVersion。
    /// </summary>
    public static IReadOnlyList<WorkshopRuleDefinition> Definitions { get; } =
        System.Array.AsReadOnly(new[]
        {
            new WorkshopRuleDefinition("jar.workshop.pour-in", 1, "POUR_IN", WorkshopRuleCategory.Transform),
            new WorkshopRuleDefinition("jar.workshop.pour-out", 2, "POUR_OUT", WorkshopRuleCategory.Transform),
            new WorkshopRuleDefinition("jar.workshop.reshape-empty", 1, "RESHAPE_EMPTY", WorkshopRuleCategory.Transform),
            new WorkshopRuleDefinition("jar.workshop.reshape-full", 3, "RESHAPE_FULL", WorkshopRuleCategory.Transform),
            new WorkshopRuleDefinition("jar.workshop.match", 4, "MATCH", WorkshopRuleCategory.Lifecycle)
        });

    private static WorkshopRuleDefinition DefinitionFor(WorkshopOperationKind kind) =>
        kind switch
        {
            WorkshopOperationKind.PourIn => Definitions[0],
            WorkshopOperationKind.PourOut => Definitions[1],
            WorkshopOperationKind.ReshapeEmpty => Definitions[2],
            WorkshopOperationKind.ReshapeFull => Definitions[3],
            _ => throw new System.ArgumentOutOfRangeException(nameof(kind))
        };

    /// <summary>订单直接匹配规则：MATCH 不改变 Jar 化学状态，但会占用并标记该 Jar。</summary>
    public static WorkshopRuleDecision EvaluateMatch(RuleContext rules, in JarFacts jar)
    {
        if (rules.HasUnresolvableRequirements())
            return WorkshopRuleDecision.Undetermined(
                "modifier.required-predicate-unavailable",
                "存在当前生效但没有可执行谓词的 Require 规则");
        if (rules.RequiredViolation() is { } requiredViolation)
            return WorkshopRuleDecision.Reject("modifier.required-condition-failed", requiredViolation);
        if (jar.IsMarked)
            return WorkshopRuleDecision.Reject("jar.already-operated", "罐子已被操作");
        if (!rules.CanOperateJar(rules.ResolvePolarity(jar.Liquid)))
            return WorkshopRuleDecision.Reject("workshop.jar-polarity-incompatible",
                "工作台当前极性不能操作该极性的 Jar");
        if (rules.SuppressionReason("MATCH", jar.Shape, jar.Liquid) is { } suppressed)
            return WorkshopRuleDecision.Reject("modifier.operation-suppressed", suppressed);
        return WorkshopRuleDecision.Allow();
    }

    /// <summary>
    /// MATCH 的纯值转换证明。它不改变 Jar 的化学状态，只把 IsMarked 置为 true。
    /// 让直接匹配也使用与四种化学操作相同的 RuleId/Version/After 契约。
    /// </summary>
    public static WorkshopOperationEvaluation EvaluateMatchTransition(
        RuleContext rules,
        in JarFacts jar)
    {
        var decision = EvaluateMatch(rules, jar);
        if (!decision.IsAllowed)
            return new WorkshopOperationEvaluation(decision, null);

        var definition = Definitions.First(x => x.Operator == "MATCH");
        return new WorkshopOperationEvaluation(
            decision,
            new WorkshopOperationTransition(
                definition.RuleId,
                definition.RuleVersion,
                new JarFacts(jar.Shape, jar.Liquid, true),
                null, null, null)
            { WorkshopPolarityMode = rules.PolarityMode });
    }

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
        in WorkshopOperation op, IReadOnlyDictionary<string, int> liquids) =>
        Evaluate(rules, maxLiquids, jar, op, liquids).IsAllowed;

    public static WorkshopRuleDecision Evaluate(RuleContext rules, int maxLiquids, in JarFacts jar,
        in WorkshopOperation op, IReadOnlyDictionary<string, int> liquids) =>
        EvaluateCore(rules, maxLiquids, null, jar, op, liquids);

    /// <summary>
    /// 唯一的工作台算子判定内核。普通库存与订单暂存只提供不同的资源边界，
    /// 算子适用条件、抑制规则、形状规则与拒绝语义在此只定义一次。
    /// </summary>
    private static WorkshopRuleDecision EvaluateCore(
        RuleContext rules,
        int maxLiquids,
        OrderLiquidScope? scope,
        in JarFacts jar,
        in WorkshopOperation op,
        IReadOnlyDictionary<string, int> liquids)
    {
        if (rules.HasUnresolvableRequirements())
            return WorkshopRuleDecision.Undetermined(
                "modifier.required-predicate-unavailable",
                "存在当前生效但没有可执行谓词的 Require 规则");
        if (rules.RequiredViolation() is { } requiredViolation)
            return WorkshopRuleDecision.Reject("modifier.required-condition-failed", requiredViolation);

        var liquidRules = new LiquidRules(rules.Capability, scope == null ? maxLiquids : 0);
        var reshapeRules = new ReshapeRules(rules, liquidRules);

        if (jar.IsMarked)
            return WorkshopRuleDecision.Reject("jar.already-operated", "罐子已被操作");
        if (!rules.CanOperateJar(rules.ResolvePolarity(jar.Liquid)))
            return WorkshopRuleDecision.Reject("workshop.jar-polarity-incompatible",
                "工作台当前极性不能操作该极性的 Jar");

        switch (op.Kind)
        {
            case WorkshopOperationKind.PourIn:
            {
                var liquid = op.TargetLiquid;
                if (liquid == null)
                    return WorkshopRuleDecision.Reject("operation.missing-liquid", "灌入操作缺少目标液体");
                if (jar.Liquid != null)
                    return WorkshopRuleDecision.Reject("jar.not-empty", "罐子不为空");
                if (!rules.IsBaseMarked(jar.Shape))
                    return WorkshopRuleDecision.Reject("workshop.shape-not-base-marked", "形状" + jar.Shape + "非本工作台基础标记");
                if (rules.SuppressionReason("POUR_IN", jar.Shape, liquid) is { } suppressed)
                    return WorkshopRuleDecision.Reject("modifier.operation-suppressed", suppressed);

                var payment = liquidRules.CanPay(liquid, 1, Count(liquids, liquid));
                if (!payment.Granted)
                    return WorkshopRuleDecision.Reject("liquid.unavailable", payment.Reason ?? "液体不可用");
                if (scope != null)
                {
                    var scopePayment = scope.CanConsume(liquid, 1);
                    if (!scopePayment.Granted)
                        return WorkshopRuleDecision.Reject("resource.scope-insufficient", scopePayment.Reason ?? "订单液体子账不足");
                }
                return WorkshopRuleDecision.Allow();
            }

            case WorkshopOperationKind.PourOut:
            {
                var liquid = jar.Liquid;
                if (liquid == null)
                    return WorkshopRuleDecision.Reject("jar.empty", "罐子为空");
                if (rules.SuppressionReason("POUR_OUT", jar.Shape, liquid) is { } suppressed)
                    return WorkshopRuleDecision.Reject("modifier.operation-suppressed", suppressed);

                // Jar polarity compatibility was checked against the owning Workshop above.
                // Production is not cancelled by staging admission: the output first enters the
                // current workshop's staging/output boundary, where Ban or capacity rejects it
                // into an explicit Discard effect. This keeps Jar chemistry separate from storage.
                return WorkshopRuleDecision.Allow();
            }

            case WorkshopOperationKind.ReshapeEmpty:
            {
                var target = op.TargetShape;
                var cost = op.CostLiquid;
                if (target == null || cost == null)
                    return WorkshopRuleDecision.Reject("operation.incomplete-reshape", "改形操作缺少目标形状或成本液体");
                if (jar.Liquid != null)
                    return WorkshopRuleDecision.Reject("jar.not-empty", "必须为空罐");
                if (!reshapeRules.CanTransform(jar.Shape, target))
                    return WorkshopRuleDecision.Reject("shape.transform-not-allowed", "无法从" + jar.Shape + "转化为" + target);
                if (rules.SuppressionReason("RESHAPE", jar.Shape, cost) is { } suppressed)
                    return WorkshopRuleDecision.Reject("modifier.operation-suppressed", suppressed);

                var payment = reshapeRules.PayCost(cost, liquids);
                if (!payment.Granted)
                    return WorkshopRuleDecision.Reject("liquid.reshape-cost-unavailable", payment.Reason ?? "改形成本不足");
                if (scope != null)
                {
                    var scopePayment = scope.CanConsume(cost, 1);
                    if (!scopePayment.Granted)
                        return WorkshopRuleDecision.Reject("resource.scope-insufficient", scopePayment.Reason ?? "订单液体子账不足");
                }
                return WorkshopRuleDecision.Allow();
            }

            case WorkshopOperationKind.ReshapeFull:
            {
                var target = op.TargetShape;
                if (target == null)
                    return WorkshopRuleDecision.Reject("operation.missing-target-shape", "改形操作缺少目标形状");
                var liquid = jar.Liquid;
                if (liquid == null)
                    return WorkshopRuleDecision.Reject("jar.not-full", "必须为满罐");
                if (!reshapeRules.CanTransform(jar.Shape, target))
                    return WorkshopRuleDecision.Reject("shape.transform-not-allowed", "无法从" + jar.Shape + "转化为" + target);
                if (rules.SuppressionReason("RESHAPE", jar.Shape, liquid) is { } suppressed)
                    return WorkshopRuleDecision.Reject("modifier.operation-suppressed", suppressed);

                var selfConsume = reshapeRules.SelfConsume(liquid);
                return selfConsume.Granted
                    ? WorkshopRuleDecision.Allow()
                    : WorkshopRuleDecision.Reject("liquid.jar-self-consume-unavailable", selfConsume.Reason ?? "罐内液体不可自耗");
            }

            default:
                return WorkshopRuleDecision.Reject("operation.unsupported", "不支持的工作台操作");
        }
    }

    /// <summary>
    /// 规则算子的纯值转换结果。它描述 Jar 终态与资源效果，不直接修改 Workshop、Jar 或资源账户。
    /// RuleId/Version 是稳定领域身份，不使用 CLR 类型名作为规则身份。
    /// </summary>
    public readonly record struct WorkshopOperationTransition(
        string RuleId,
        int RuleVersion,
        JarFacts After,
        string? ConsumedFreeLiquid,
        string? ProducedFreeLiquid,
        string? ConsumedJarLiquid)
    {
        public WorkshopPolarityMode WorkshopPolarityMode { get; init; }
    }

    public readonly record struct WorkshopOperationEvaluation(
        WorkshopRuleDecision Decision,
        WorkshopOperationTransition? Transition);

    /// <summary>
    /// 在当前规则上下文中评估并生成一次确定性状态转换。
    /// null 表示规则拒绝；非 null 表示可执行的纯值转换证明。
    /// 资源账户的实际扣除/接收仍由调用方在所属事务边界内完成。
    /// </summary>
    public static WorkshopOperationEvaluation EvaluateTransition(
        RuleContext rules, int maxLiquids, in JarFacts jar,
        in WorkshopOperation op, IReadOnlyDictionary<string, int> liquids)
    {
        var decision = Evaluate(rules, maxLiquids, jar, op, liquids);
        return new WorkshopOperationEvaluation(
            decision,
            decision.IsAllowed ? CreateTransitionCore(jar, op, rules.PolarityMode) : null);
    }

    public static WorkshopOperationTransition? CreateTransition(
        RuleContext rules, int maxLiquids, in JarFacts jar,
        in WorkshopOperation op, IReadOnlyDictionary<string, int> liquids) =>
        EvaluateTransition(rules, maxLiquids, jar, op, liquids).Transition;

    private static WorkshopOperationTransition? CreateTransitionCore(
        in JarFacts jar, in WorkshopOperation op, WorkshopPolarityMode polarityMode) =>
        op.Kind switch
        {
            WorkshopOperationKind.PourIn => Transition(WorkshopOperationKind.PourIn,
                new JarFacts(jar.Shape, op.TargetLiquid, true), op.TargetLiquid, null, null, polarityMode),
            WorkshopOperationKind.PourOut => Transition(WorkshopOperationKind.PourOut,
                new JarFacts(jar.Shape, null, true), null, jar.Liquid, null, polarityMode),
            WorkshopOperationKind.ReshapeEmpty => Transition(WorkshopOperationKind.ReshapeEmpty,
                new JarFacts(op.TargetShape!, null, true), op.CostLiquid, null, null, polarityMode),
            WorkshopOperationKind.ReshapeFull => Transition(WorkshopOperationKind.ReshapeFull,
                new JarFacts(op.TargetShape!, null, true), null, null, jar.Liquid, polarityMode),
            _ => null
        };

    private static WorkshopOperationTransition Transition(
        WorkshopOperationKind kind,
        JarFacts after,
        string? consumedFreeLiquid,
        string? producedFreeLiquid,
        string? consumedJarLiquid,
        WorkshopPolarityMode polarityMode)
    {
        var definition = DefinitionFor(kind);
        return new WorkshopOperationTransition(
            definition.RuleId, definition.RuleVersion, after,
            consumedFreeLiquid, producedFreeLiquid, consumedJarLiquid)
        { WorkshopPolarityMode = polarityMode };
    }

    /// <summary>
    /// 列出现在**全部**合法操作（按固定顺序：倒出 → 灌入 → 空罐改形 → 满罐改形）。
    ///
    /// <para>顺序固定是为了可复现：同一状态永远给出同一个序列，测试可以逐项比对。</para>
    /// </summary>
    public static List<WorkshopOperation> Available(RuleContext rules, int maxLiquids, in JarFacts jar,
        IReadOnlyDictionary<string, int> liquids)
    {
        var facts = jar;
        return AvailableCore(rules, facts, op => IsAllowed(rules, maxLiquids, facts, op, liquids));
    }

    /// <summary>
    /// 订单暂存版本的规则判定。容量使用工作台所有订单子账的合计剩余空间，
    /// 库存消耗只读取当前订单子账。
    /// </summary>
    public static bool IsAllowed(RuleContext rules, OrderLiquidScope scope, in JarFacts jar,
        in WorkshopOperation op) =>
        Evaluate(rules, scope, jar, op).IsAllowed;

    public static WorkshopRuleDecision Evaluate(RuleContext rules, OrderLiquidScope scope,
        in JarFacts jar, in WorkshopOperation op)
    {
        if (scope == null) throw new System.ArgumentNullException(nameof(scope));
        return EvaluateCore(rules, 0, scope, jar, op, scope.Balance);
    }

    /// <summary>
    /// 订单子账转换证明。与普通库存版本共享 RuleId / After / 罐内自耗语义，
    /// 但资源扣除/接收由 OrderLiquidScope 完成。
    /// </summary>
    public static WorkshopOperationEvaluation EvaluateTransition(
        RuleContext rules, OrderLiquidScope scope, in JarFacts jar, in WorkshopOperation op)
    {
        var decision = Evaluate(rules, scope, jar, op);
        return new WorkshopOperationEvaluation(
            decision,
            decision.IsAllowed ? CreateTransitionCore(jar, op, rules.PolarityMode) : null);
    }

    public static WorkshopOperationTransition? CreateTransition(
        RuleContext rules, OrderLiquidScope scope, in JarFacts jar, in WorkshopOperation op) =>
        EvaluateTransition(rules, scope, jar, op).Transition;

    /// <summary>列出订单暂存上下文下全部合法操作，顺序与旧 Available 保持一致。</summary>
    public static List<WorkshopOperation> Available(RuleContext rules, OrderLiquidScope scope,
        in JarFacts jar)
    {
        if (scope == null) throw new System.ArgumentNullException(nameof(scope));
        var facts = jar;
        return AvailableCore(rules, facts, op => IsAllowed(rules, scope, facts, op));
    }

    private static List<WorkshopOperation> AvailableCore(
        RuleContext rules,
        in JarFacts jar,
        Func<WorkshopOperation, bool> isAllowed)
    {
        var list = new List<WorkshopOperation>(8);
        if (jar.IsMarked) return list;

        var pourOut = WorkshopOperation.PourOut();
        if (isAllowed(pourOut)) list.Add(pourOut);

        foreach (var liquid in rules.Liquids)
        {
            var pourIn = WorkshopOperation.PourIn(liquid);
            if (isAllowed(pourIn)) list.Add(pourIn);
        }

        foreach (var target in rules.GetPossibleTransforms(jar.Shape))
        {
            foreach (var cost in rules.Liquids)
            {
                var reshapeEmpty = WorkshopOperation.ReshapeEmpty(target, cost);
                if (isAllowed(reshapeEmpty)) list.Add(reshapeEmpty);
            }

            var reshapeFull = WorkshopOperation.ReshapeFull(target);
            if (isAllowed(reshapeFull)) list.Add(reshapeFull);
        }

        return list;
    }

    private static int Count(IReadOnlyDictionary<string, int> liquids, string liquid) =>
        liquids.TryGetValue(liquid, out var n) ? n : 0;
}

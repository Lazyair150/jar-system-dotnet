using JarSystem.Core.Domain;

namespace JarSystem.Core.Domain;

/// <summary>
/// 规则读取的唯一入口：基础法则（<see cref="WorkshopCapability"/>）+ 活跃修饰器。
///
/// 为什么要有它：修饰器要能影响"能不能倒液/改形/被禁"，而规则读取散在 <c>Workshop</c>、
/// 快照与求解器里。把读取收敛到一个对象后：
/// <list type="bullet">
/// <item>默认空栈就等于今天的行为（逐项等价，可用于等价性论证）；</item>
/// <item>修饰器只影响这一个入口，不必在每处规则判断里各加一段分支；</item>
/// <item>求解器与执行器可以共用同一个上下文对象（P3b-2 接），口径不会漂。</item>
/// </list>
/// </summary>
public sealed class RuleContext
{
    private readonly WorkshopCapability _capability;
    private readonly EffectiveRuleProfile? _effectiveProfile;
    private readonly string _ruleScope;
    private readonly string? _ruleSlotId;

    /// <summary>由基础能力构造（无修饰器：与改造前的规则判定逐项相同）。</summary>
    public static RuleContext From(WorkshopCapability capability) => new(capability, ModifierStack.Empty);

    /// <summary>Builds a frozen rule context from contributions already filtered to their capture-time active window.</summary>
    public static RuleContext From(
        EffectiveRuleProfile profile,
        string scope = "workshop",
        string? sourceSlotId = null,
        bool mutableStack = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Rule scope is required.", nameof(scope));
        if (sourceSlotId is not null && string.IsNullOrWhiteSpace(sourceSlotId))
            throw new ArgumentException("Rule slot identity cannot be whitespace.", nameof(sourceSlotId));
        var scoped = profile.Contributions
            .Where(x => x.Scope == scope && (sourceSlotId is null || x.Source.SlotId == sourceSlotId))
            .ToArray();
        var baseMarks = scoped.Where(x => x.Effect == RuleContributionEffect.GrantBaseMark).Select(x => x.Target).Distinct(StringComparer.Ordinal).ToArray();
        var additionalMarks = scoped.Where(x => x.Effect == RuleContributionEffect.GrantAdditionalMark)
            .Where(x => x.Source.SourceKind != "temporary-modifier")
            .Select(x => x.Target).Distinct(StringComparer.Ordinal).ToArray();
        var bans = scoped.Where(x => x.Effect == RuleContributionEffect.BanLiquid).Select(x => x.Target).Distinct(StringComparer.Ordinal).ToArray();
        var capability = new WorkshopCapability(baseMarks, additionalMarks, bans);
        var modifiers = new List<IRuleModifier>();
        foreach (var contribution in scoped)
        {
            var parameters = contribution.Parameters.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
            switch (contribution.Effect)
            {
                case RuleContributionEffect.GrantAdditionalMark:
                    if (contribution.Source.SourceKind == "temporary-modifier")
                        modifiers.Add(new GrantModifier { Name = Parameter(parameters, "modifierName", "grant"), Source = Parameter(parameters, "modifierSource", "temporary"), Priority = contribution.Priority, Shape = contribution.Target });
                    break;
                case RuleContributionEffect.SuppressOperation:
                    modifiers.Add(new SuppressModifier { Name = Parameter(parameters, "modifierName", "suppress"), Source = Parameter(parameters, "modifierSource", "temporary"), Priority = contribution.Priority, Operation = Parameter(parameters, "operation", "") is { Length: > 0 } operation ? operation : null, Shape = Parameter(parameters, "shape", "") is { Length: > 0 } shape ? shape : null, Liquid = Parameter(parameters, "liquid", "") is { Length: > 0 } liquid ? liquid : null, Reason = Parameter(parameters, "reason", "此刻不得进行该操作") });
                    break;
                case RuleContributionEffect.RequireCondition:
                    modifiers.Add(new RequireModifier { Name = Parameter(parameters, "modifierName", "require"), Source = Parameter(parameters, "modifierSource", "temporary"), Priority = contribution.Priority, Text = contribution.Target, Violation = null });
                    break;
                case RuleContributionEffect.RetimeProcess:
                    modifiers.Add(new RetimeModifier { Name = Parameter(parameters, "modifierName", "retime"), Source = Parameter(parameters, "modifierSource", "temporary"), Priority = contribution.Priority, ProcessKind = contribution.Target == "*" ? null : contribution.Target, TicksPerStep = int.TryParse(Parameter(parameters, "ticksPerStep", "1"), out var ticks) ? ticks : 1 });
                    break;
                case RuleContributionEffect.ModifyIntensity:
                    modifiers.Add(new IntensityModifier { Name = Parameter(parameters, "modifierName", "intensity"), Source = Parameter(parameters, "modifierSource", "temporary"), Priority = contribution.Priority, Target = contribution.Target, FactorPerMille = int.TryParse(Parameter(parameters, "factorPerMille", "1000"), out var factor) ? factor : 1000 });
                    break;
            }
        }
        var stack = mutableStack
            ? ModifierStack.CreateSeeded(modifiers)
            : ModifierStack.CreateReadOnlySnapshot(modifiers);
        return new RuleContext(capability, stack, profile, scope, sourceSlotId);
    }

    private static string Parameter(IReadOnlyDictionary<string, string> parameters, string key, string fallback) =>
        parameters.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>由基础能力 + 修饰器栈构造。</summary>
    public RuleContext(
        WorkshopCapability capability,
        ModifierStack stack,
        EffectiveRuleProfile? effectiveProfile = null,
        string ruleScope = "workshop",
        string? ruleSlotId = null)
    {
        _capability = capability;
        Stack = stack;
        _effectiveProfile = effectiveProfile;
        _ruleScope = ruleScope;
        _ruleSlotId = ruleSlotId;
    }

    /// <summary>基础能力（只读；快照与对外投影仍读它）。</summary>
    public WorkshopCapability Capability => _capability;

    /// <summary>活跃修饰器栈（与工作台、过程系统共用同一实例：一处入栈，处处生效）。</summary>
    public ModifierStack Stack { get; }

    public EffectiveRuleProfile? EffectiveProfile => _effectiveProfile;

    public string RuleScope => _ruleScope;
    public string? RuleSlotId => _ruleSlotId;

    public bool IsBaseMarked(string shape) => _capability.IsBaseMarked(shape);

    /// <summary>是否属于附加标记（基础 + 被 <see cref="GrantModifier"/> 授予的形状）。</summary>
    public bool IsAdditionalMarked(string shape)
    {
        if (_capability.IsAdditionalMarked(shape)) return true;
        if (Stack.IsEmpty) return false;                       // 热路径：空栈不做任何分配
        foreach (var granted in Stack.GrantedShapes())
            if (granted == shape) return true;
        return false;
    }

    public bool IsBanned(string liquid) => _capability.IsBanned(liquid);

    /// <summary>可改形目标：基础能力给出的 + 被授予的形状（后者视作附加标记）。</summary>
    public List<string> GetPossibleTransforms(string shape)
    {
        var granted = Stack.GrantedShapes();
        return granted.Count == 0
            ? _capability.GetPossibleTransforms(shape)
            : _capability.GetPossibleTransforms(shape, granted);
    }

    public bool CanTransform(string from, string to) => GetPossibleTransforms(from).Contains(to);

    /// <summary>该操作现在是否被修饰器抑制（返回原因或 null）。</summary>
    public string? SuppressionReason(string? operation, string? shape, string? liquid) =>
        Stack.SuppressionReason(operation, shape, liquid);

    public bool HasUnresolvableRequirements() => Stack.HasUnresolvableRequirements() ||
        (_effectiveProfile?.Contributions.Any(x =>
            x.Scope == _ruleScope &&
            (_ruleSlotId is null || x.Source.SlotId == _ruleSlotId) &&
            x.Effect == RuleContributionEffect.ModifyCapacity) ?? false);

    public string? RequiredViolation() => Stack.RequiredViolation();
}

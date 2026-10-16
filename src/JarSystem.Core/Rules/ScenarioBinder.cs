namespace JarSystem.Core.Rules;

/// <summary>
/// 把场景数据绑成可执行的修饰器与事件源（P4d）。
///
/// 分工：数据层（<see cref="ScenarioOptions"/>）只描述"什么规则、什么时候"；本层把它翻译成
/// <see cref="IRuleModifier"/> 与 <see cref="IWorldEventSource"/> —— 于是"事故与临时规则"
/// 可以由关卡数据驱动，而不必改一行宿主代码。
///
/// 时机语义：事件在管线第 ① 步（过程推进之前）触发；它压入的修饰器**带区间**，
/// 到点由第 ② 步的 Expire 自动摘掉（这就是"临时禁令会自己解除"的数据版）。
/// </summary>
public static class ScenarioBinder
{
    /// <summary>
    /// 把一条数据绑成修饰器（调用前请先跑 <see cref="ScenarioOptions.Validate"/>）。
    /// <paramref name="world"/> 只有 <c>require</c> 需要（条件类规则要去数世界）；其余 kind 传 null 即可。
    /// </summary>
    public static IRuleModifier Bind(ModifierData d, IWorldFacts? world = null) => d.Kind switch
    {
        "suppress" => new SuppressModifier
        {
            Source = d.Source,
            Reason = string.IsNullOrEmpty(d.Reason) ? "此刻不得进行该操作" : d.Reason,
            Operation = d.Operation,
            Shape = d.Shape,
            Liquid = d.Liquid,
            Window = (d.From ?? 0, d.To)
        },
        "retime" => new RetimeModifier
        {
            Source = d.Source,
            ProcessKind = d.ProcessKind,
            TicksPerStep = d.TicksPerStep ?? 1,
            Window = (d.From ?? 0, d.To)
        },
        "intensity" => new IntensityModifier
        {
            Source = d.Source,
            Target = d.Target ?? "",
            FactorPerMille = d.FactorPerMille ?? 1000,
            Window = (d.From ?? 0, d.To)
        },
        "grant" => new GrantModifier
        {
            Source = d.Source,
            Shape = d.Shape ?? "",
            Window = (d.From ?? 0, d.To)
        },
        "require" => new RequireModifier
        {
            Source = d.Source,
            Text = string.IsNullOrEmpty(d.Reason) ? "条件类规则" : d.Reason,
            Window = (d.From ?? 0, d.To),
            Violation = BuildCondition(d, world)
        },
        _ => throw new ArgumentException("数据侧不支持的修饰器 kind: " + d.Kind)
    };

    /// <summary>把数据里的"下限条件"绑成一个谓词：返回"不成立的原因"或 null（成立）。</summary>
    private static Func<string?>? BuildCondition(ModifierData d, IWorldFacts? world)
    {
        if (world == null || d.Condition == null) return null;   // 校验阶段已挡住；这里再兜一层
        var reason = string.IsNullOrEmpty(d.Reason) ? null : d.Reason;
        var query = d.Condition;
        return () => query.Evaluate(world, reason);
    }

    /// <summary>开局生效的修饰器（逐个绑好；区间到点仍由栈自动摘除）。</summary>
    public static List<IRuleModifier> BindInitial(ScenarioOptions scenario, IWorldFacts? world = null) =>
        scenario.InitialModifiers.Select(m => Bind(m, world)).ToList();

    /// <summary>由场景数据造一个事件源：第 N 刻触发的事件会把它的修饰器压进给定的栈。</summary>
    public static IWorldEventSource CreateEventSource(ScenarioOptions scenario, ModifierStack stack,
        IWorldFacts? world = null) =>
        new ScenarioEventSource(scenario, stack, world);

    private sealed class ScenarioEventSource : IWorldEventSource
    {
        private readonly ScenarioOptions _scenario;
        private readonly ModifierStack _stack;
        private readonly IWorldFacts? _world;

        public ScenarioEventSource(ScenarioOptions scenario, ModifierStack stack, IWorldFacts? world)
        {
            _scenario = scenario;
            _stack = stack;
            _world = world;
        }

        public IReadOnlyList<WorldEvent> DrainForTick(int tick)
        {
            var list = new List<WorldEvent>();
            foreach (var e in _scenario.Events)
            {
                if (e.AtTick != tick) continue;
                var bound = e.Modifiers.Select(m => Bind(m, _world)).ToList();
                list.Add(new WorldEvent
                {
                    Name = e.Name,
                    Description = e.Description,
                    // 只为带区间的修饰器入栈：到期由 Expire 摘掉，宿主不必持有作用域。
                    Effect = () => { foreach (var m in bound) _stack.Push(m); }
                });
            }
            return list;
        }
    }
}

namespace JarSystem.Core.Process;

/// <summary>
/// **tick 调度器**：把"一个 tick 里谁被调用、以什么次序"从硬编码变成一张显式表。
///
/// <para>
/// 它补的是引擎那一半能力：引擎自带时间流逝，模块只要实现 <c>process()</c> 就会被调用；
/// 这里对应的是"实现 <see cref="ITickHandler"/> 并注册"，**不必再改 <c>JarSystem.Tick</c>**。
/// </para>
///
/// <para>
/// 但另一半刻意不做 —— **调度器不认识"时间到了没有"**。它不持有时钟、不起线程、不看墙钟：
/// 什么时候跑一次由调用方（<see cref="TickPipeline"/>）决定。理由是同一条：
/// 核心一旦拥有时间推进，判定就与墙钟相关，回放不再可复现。
/// </para>
///
/// <para>
/// 三条注册纪律（违反任一条，同一份命令日志就会在不同会话里重放出不同世界）：
/// </para>
/// <list type="number">
/// <item><b>注册必须在时间开始流动之前</b>。第一次 <see cref="RunTick"/> 之后表即封盘（<see cref="IsSealed"/>），
///       再注册直接抛 <see cref="InvalidOperationException"/>。</item>
/// <item><b>同阶段内 <see cref="ITickHandler.Order"/> 必须唯一</b>。冲突在注册时报错，
///       不静默按注册顺序 —— 哈希序或调用序在这里就是不确定性的来源。</item>
/// <item><b>处理器不得改世界</b>。只能读世界 + 写自己的状态；要改世界必须经 <c>WorldBus</c>
///       排进下一个 tick 的 <see cref="TickPhase.Events"/> 阶段。</item>
/// </list>
/// </summary>
public sealed class WorldScheduler
{
    private static readonly TickPhase[] PerTickPhases =
    {
        TickPhase.Events, TickPhase.Modifiers, TickPhase.Processes, TickPhase.Production
    };

    private readonly Dictionary<TickPhase, List<ITickHandler>> _byPhase = new();
    private readonly List<ITickHandler> _all = new();

    /// <summary>表是否已封盘（第一次 <see cref="RunTick"/> 之后恒为 true）。</summary>
    public bool IsSealed { get; private set; }

    /// <summary>已注册的处理器（按阶段、再按 <see cref="ITickHandler.Order"/> 升序）。</summary>
    public IReadOnlyList<ITickHandler> Handlers => _all;

    /// <summary>注册一个 tick 处理器。<see cref="IsSealed"/> 之后调用会抛异常。</summary>
    /// <exception cref="InvalidOperationException">表已封盘，或同阶段 <see cref="ITickHandler.Order"/> 冲突。</exception>
    public void Register(ITickHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (IsSealed)
            throw new InvalidOperationException(
                $"tick 表已封盘，不能再注册 '{handler.Name}'（时间已开始流动；注册必须在第一个 tick 之前完成）");

        if (!_byPhase.TryGetValue(handler.Phase, out var list))
        {
            list = new List<ITickHandler>();
            _byPhase[handler.Phase] = list;
        }

        if (list.Exists(h => h.Order == handler.Order))
        {
            var clash = list.Find(h => h.Order == handler.Order);
            throw new InvalidOperationException(
                $"阶段 {handler.Phase} 的 Order {handler.Order} 已被 '{clash!.Name}' 占用，"
                + $"不能再注册 '{handler.Name}'（同阶段顺序必须唯一，否则 tick 内次序不确定）");
        }

        var at = list.FindIndex(h => h.Order > handler.Order);
        if (at < 0) list.Add(handler);
        else list.Insert(at, handler);

        RebuildFlat();
    }

    /// <summary>
    /// 跑一个 tick 的四个**每 tick**阶段（<see cref="TickPhase.Events"/> →
    /// <see cref="TickPhase.Modifiers"/> → <see cref="TickPhase.Processes"/> → <see cref="TickPhase.Production"/>）。
    /// 首次调用会把表封盘。
    /// </summary>
    public void RunTick(int tick)
    {
        IsSealed = true;
        var ctx = new TickContext(tick);
        foreach (var phase in PerTickPhases)
        {
            if (!_byPhase.TryGetValue(phase, out var list)) continue;
            for (int i = 0; i < list.Count; i++)
                list[i].OnTick(ctx);
        }
    }

    /// <summary>
    /// 跑**每次宿主 Tick 调用一次**的阶段（目前只有 <see cref="TickPhase.Flow"/>）。
    ///
    /// <para>
    /// 它不与 <see cref="RunTick"/> 合并，是为了让"流入每次调用一次、产出每 tick 一次"这个
    /// 既有语义有一个**具名的位置**，而不是散落在调用方的一行调用里。
    /// </para>
    /// </summary>
    public void RunCallBoundary(int tick)
    {
        if (!_byPhase.TryGetValue(TickPhase.Flow, out var list)) return;
        var ctx = new TickContext(tick);
        for (int i = 0; i < list.Count; i++)
            list[i].OnTick(ctx);
    }

    private void RebuildFlat()
    {
        _all.Clear();
        foreach (var phase in PerTickPhases) AppendPhase(phase);
        AppendPhase(TickPhase.Flow);
    }

    private void AppendPhase(TickPhase phase)
    {
        if (_byPhase.TryGetValue(phase, out var list))
            _all.AddRange(list);
    }
}

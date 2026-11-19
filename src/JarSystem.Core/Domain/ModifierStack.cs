using JarSystem.Core.Domain;

namespace JarSystem.Core.Domain;

/// <summary>
/// 修饰器栈：世界在某一刻的活跃修饰器集合（按区间自动失效，按作用域强制回收）。
///
/// 三条设计约束：
/// <list type="number">
/// <item>必回收：所有入栈都经 <see cref="Push"/> 返回的 <see cref="ModifierScope"/>（IDisposable），
/// 异常路径同样回收 —— 与既有 <c>WithTemporaryAdditionalMarks</c> 同一手法；</item>
/// <item>区间自动失效：每个 tick 由 <see cref="Expire"/> 摘掉过期项（"临时禁令到期自动解除"）；</item>
/// <item>冲突显式报错：同种类、同目标、同优先级却同时命中的多条修饰器 ⇒ 抛异常，
/// 而不是悄悄挑一条（本仓库一贯的"宁可拒绝，不要静默不一致"）。</item>
/// </list>
/// </summary>
public sealed class ModifierStack : IIntensitySource
{
    /// <summary>共享的空栈（默认上下文用；只读，故可安全共享）。</summary>
    public static readonly ModifierStack Empty = new(isReadOnly: true);

    private readonly List<IRuleModifier> _active = new();
    private readonly IReadOnlyList<IRuleModifier> _view;
    private readonly bool _readOnly;

    // 授予形状的缓存（热路径）：这条路径在**求解器内循环**里被反复调用（每个候选都要问能否改成它），
    // 原先每次调用都新建 List ⇒ 空栈时也在制造垃圾。缓存按栈是否变化失效。
    private readonly List<string> _grantedCache = new();
    private int _grantedRevision = -1;
    private int _grantedTick = -1;

    /// <summary>栈当前认识的世界 tick（由 Expire 每 tick 刷新）。</summary>
    private int _now;
    private int _revision;

    public ModifierStack(bool isReadOnly = false)
    {
        _readOnly = isReadOnly;
        _view = _active.AsReadOnly();
    }

    /// <summary>当前活跃修饰器（只读视图，活视图；包含尚未到 From 的未来修饰器）。</summary>
    public IReadOnlyList<IRuleModifier> Active => _view;

    /// <summary>
    /// 当前时刻真正生效的修饰器副本。与 Active 不同，它排除尚未到 From 的未来修饰器，
    /// 供世界求解快照捕获，避免把未来规则提前投影到当前求解输入。
    /// </summary>
    public IReadOnlyList<IRuleModifier> CaptureEffectiveModifiers() =>
        _active.Where(IsLive).ToArray();

    /// <summary>是否没有任何活跃修饰器（**热路径**用：空栈一律走直通，连一次分配都不做）。</summary>
    public bool IsEmpty => _active.Count == 0;

    /// <summary>入栈并返回作用域（using 结束即回收；异常路径同样回收）。</summary>
    public ModifierScope Push(IRuleModifier modifier)
    {
        if (_readOnly) throw new InvalidOperationException("空栈（共享实例）不可写入");
        _active.Add(modifier);
        _revision++;
        return new ModifierScope(this, modifier);
    }

    /// <summary>摘掉过期修饰器（由 tick 管线每 tick 调用一次）。返回摘掉的条数。</summary>
    public int Expire(int tick)
    {
        _now = tick;
        if (_readOnly) return 0;
        var removed = _active.RemoveAll(m => m.Window is { To: { } to } && tick > to);
        if (removed > 0) _revision++;
        return removed;
    }

    /// <summary>世界重建时把窗口时钟归零（只由 <see cref="Process.ProcessSystem.Clear"/> 调用）。</summary>
    internal void ResetClock()
    {
        _now = 0;
        _revision++;
        _grantedRevision = -1;
        _grantedTick = -1;
    }

    /// <summary>
    /// 世界重建时清空所有旧时间线的修饰器。旧 <see cref="ModifierScope"/> 之后调用 Dispose
    /// 只会命中一个空集合，因此仍保持幂等。
    /// </summary>
    internal void Clear()
    {
        if (_readOnly) return;
        if (_active.Count > 0) _revision++;
        _active.Clear();
        _grantedCache.Clear();
        _grantedRevision = -1;
        _grantedTick = -1;
    }

    /// <summary>
    /// 抑制原因：若当前有修饰器禁止（operation / shape / liquid 任一维度）该操作，
    /// 返回世界语言的原因；否则返回 null。
    /// </summary>
    public string? SuppressionReason(string? operation, string? shape, string? liquid)
    {
        if (_active.Count == 0) return null;   // 热路径：空栈直接返回（无分配、无遍历）
        SuppressModifier? winner = null;
        foreach (var m in _active)
        {
            if (m is not SuppressModifier s) continue;
            if (!IsLive(m)) continue;
            if (s.Operation == null && s.Shape == null && s.Liquid == null) continue;   // 空修饰器不抑制任何东西
            if (s.Operation != null && s.Operation != operation) continue;
            if (s.Shape != null && s.Shape != shape) continue;
            if (s.Liquid != null && s.Liquid != liquid) continue;

            if (winner == null) { winner = s; continue; }
            if (s.Priority > winner.Priority) { winner = s; continue; }
            if (s.Priority == winner.Priority)
                throw new InvalidOperationException(
                    "修饰器冲突：" + winner.Name + " 与 " + s.Name + " 同优先级（" + s.Priority + "）却同时命中同一目标");
        }
        return winner?.Reason;
    }

    /// <summary>
    /// 这条修饰器**此刻**是否生效：区间是闭区间 [From, To]。
    ///
    /// 为什么需要它：Expire 只负责"到点摘掉"（To），而 **From 也必须被尊重** ——
    /// 否则一条"从第 12 刻起生效"的开局修饰器会立刻生效（设计是"到点才生效"）。实测发现过这个偏离。
    /// </summary>
    private bool IsLive(IRuleModifier m) => m.Window is not { } w || _now >= w.From;

    /// <summary>步间隔覆盖（取匹配的 <see cref="RetimeModifier"/> 中优先级最高者）；无匹配返回 null。</summary>
    public int? TicksPerStepOverride(string processKind)
    {
        if (_active.Count == 0) return null;
        RetimeModifier? winner = null;
        foreach (var m in _active)
        {
            if (m is not RetimeModifier r) continue;
            if (!IsLive(m)) continue;
            if (r.ProcessKind != null && r.ProcessKind != processKind) continue;
            if (winner == null || r.Priority > winner.Priority) winner = r;
        }
        return winner == null ? null : Math.Max(1, winner.TicksPerStep);
    }

    /// <summary>当前被授予的形状（<see cref="GrantModifier"/>）。</summary>
    public IReadOnlyList<string> GrantedShapes()
    {
        if (_grantedRevision == _revision && _grantedTick == _now) return _grantedCache;
        _grantedCache.Clear();
        foreach (var m in _active)
            if (IsLive(m) && m is GrantModifier g && !string.IsNullOrEmpty(g.Shape) && !_grantedCache.Contains(g.Shape))
                _grantedCache.Add(g.Shape);
        _grantedRevision = _revision;
        _grantedTick = _now;
        return _grantedCache;
    }

    /// <summary>
    /// 强度折算（整数千分比；**无匹配修饰器时恒等返回**）。
    /// 逐条相乘，全程整数运算 ⇒ 与"确定性"这条硬要求一致。
    /// </summary>
    public int ApplyIntensity(string target, int value)
    {
        foreach (var m in _active)
        {
            if (m is not IntensityModifier i) continue;
            if (!IsLive(m)) continue;
            if (i.Target != target) continue;
            var scaled = (long)value * Math.Max(0, i.FactorPerMille) / 1000;
            value = (int)Math.Clamp(scaled, 0L, int.MaxValue);
        }
        return value;
    }

    /// <summary>端口实现（P4c）：让产出层经 <see cref="IIntensitySource"/> 使用同一份折算逻辑。</summary>
    int IIntensitySource.Apply(string target, int value) => ApplyIntensity(target, value);

    /// <summary>
    /// 新增前置条件的违反原因（<see cref="RequireModifier"/>）：第一个不成立的条件的理由；都成立则 null。
    /// </summary>
    public string? RequiredViolation()
    {
        foreach (var m in _active)
        {
            if (m is not RequireModifier r || r.Violation == null) continue;
            if (!IsLive(m)) continue;
            var reason = r.Violation();
            if (reason != null) return reason;
        }
        return null;
    }

    /// <summary>摘除（只供 <see cref="ModifierScope"/> 调用）。</summary>
    internal void Remove(IRuleModifier modifier)
    {
        if (_readOnly) return;
        if (_active.Remove(modifier)) _revision++;
    }
}

/// <summary>修饰器作用域（using 结束或异常时回收；重复 Dispose 幂等）。</summary>
public sealed class ModifierScope : IDisposable
{
    private ModifierStack? _stack;
    private readonly IRuleModifier _modifier;

    internal ModifierScope(ModifierStack stack, IRuleModifier modifier)
    {
        _stack = stack;
        _modifier = modifier;
    }

    public void Dispose()
    {
        _stack?.Remove(_modifier);
        _stack = null;
    }
}

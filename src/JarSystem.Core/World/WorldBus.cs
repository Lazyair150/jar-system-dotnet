using System;
using System.Collections.Generic;
using JarSystem.Core.Rules;

namespace JarSystem.Core.World;

/// <summary>
/// **世界信息总线**：世界每次变化都在这里发一条信号，所有关心它的模块从同一个地方收到。
///
/// 它同时扮演两个角色：
/// <list type="number">
/// <item>**广播口**：<see cref="Publish"/> 把世界变化的粗粒度事实同步分发给所有观察者；</item>
/// <item>**事件源**：实现 <see cref="IWorldEventSource"/>，于是反应器请求的"后续世界变化"
///       会被过程管线在下一个 tick 的第 ① 步统一执行（见 <c>ProcessSystem.DrainEvents</c>）。
///       这个接口本来就有，只是从前没有实现者 —— 总线住进去，级联就有了一个确定性的落点。</item>
/// </list>
///
/// <para><b>为什么级联要跨 tick。</b>同一 tick 内同步递归级联的问题是"收敛"变成运行时性质：
/// 两个反应器互相触发就会挂住，而这个挂住又依赖执行顺序，于是回放不可复现。
/// 改成"这一 tick 排的队，下一 tick 收的果"之后，级联的顺序完全由队列决定，
/// 与墙钟、线程、哈希序无关；长度上限由 <see cref="MaxEffectsPerTick"/> 兜住。</para>
///
/// <para><b>为什么默认什么都不做。</b>没有订阅者、没有反应器时，
/// <see cref="Publish"/> 只做一次循环判定，<see cref="DrainForTick"/> 永远返回空表 ——
/// 世界的行为与接入总线之前**逐字节相同**。这不是巧合，是设计要求：
/// 回放资产是本项目最硬的约束，总线不能因为"存在"就改变世界的走向。</para>
/// </summary>
public sealed class WorldBus : IWorldEventSource, IWorldEffectSink
{
    /// <summary>一个 tick 最多排队多少条"后续世界变化"。超出即拒收并计数，不抛异常。</summary>
    public const int MaxEffectsPerTick = 256;

    /// <summary>一次 <see cref="Publish"/> 最多处理多少条信号（含反应器追加的），超出即丢弃并计数。</summary>
    public const int MaxSignalsPerDispatch = 1024;

    private readonly List<IWorldObserver> _observers = new();
    private readonly List<IWorldReactor> _reactors = new();
    private readonly Queue<WorldSignal> _signals = new();
    private List<WorldEvent> _pending = new();

    /// <summary>累计发出的信号数（诊断用，不参与任何对外契约）。</summary>
    public int Published { get; private set; }

    /// <summary>因超出 <see cref="MaxSignalsPerDispatch"/> 被丢弃的信号数。</summary>
    public int DroppedSignals { get; private set; }

    /// <summary>因超出 <see cref="MaxEffectsPerTick"/> 被拒收的效果数。</summary>
    public int DroppedEffects { get; private set; }

    /// <summary>当前已排队、将在下一个 tick 执行的效果条数。</summary>
    public int PendingCount => _pending.Count;

    public bool HasObservers => _observers.Count > 0;

    public bool HasReactors => _reactors.Count > 0;

    /// <summary>注册观察者（**按注册顺序**同步调用 —— 顺序即语义，不要改成并发分发）。</summary>
    public void Subscribe(IWorldObserver observer)
    {
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        _observers.Add(observer);
    }

    /// <summary>注册反应器（同样按注册顺序调用）。</summary>
    public void AttachReactor(IWorldReactor reactor)
    {
        if (reactor == null) throw new ArgumentNullException(nameof(reactor));
        _reactors.Add(reactor);
    }

    /// <summary>
    /// 丢弃所有**尚未执行**的信号与效果，但**保留订阅者**。
    ///
    /// 世界重置时必须调它：已排队的效果闭包捕获的是旧世界的工作台与订单，
    /// 重置后继续执行等于让旧世界的手伸进新世界。订阅者本身是宿主的接线，不该被重置掉。
    /// </summary>
    public void ResetPending()
    {
        _signals.Clear();
        _pending = new List<WorldEvent>();
    }

    /// <summary>清空订阅与队列（世界重置时用）。</summary>
    public void Clear()
    {
        _observers.Clear();
        _reactors.Clear();
        _signals.Clear();
        _pending = new List<WorldEvent>();
    }

    /// <summary>
    /// 发出一条世界信号并同步分发。
    ///
    /// <para>递归安全：反应器在 <see cref="IWorldReactor.React"/> 里再发信号时，
    /// 那条信号进的是同一个队列、由同一个循环消化，不会二次入栈 —— 因此不存在栈溢出，
    /// 只存在"这一轮最多处理多少条"的显式上限。</para>
    /// </summary>
    /// <returns>本轮实际处理的信号条数。</returns>
    public int Publish(in WorldSignal signal)
    {
        _signals.Enqueue(signal);

        var handled = 0;
        while (_signals.Count > 0 && handled < MaxSignalsPerDispatch)
        {
            var current = _signals.Dequeue();
            handled++;
            Published++;

            for (var i = 0; i < _observers.Count; i++)
                _observers[i].OnSignal(in current);

            for (var i = 0; i < _reactors.Count; i++)
                _reactors[i].React(in current, this);
        }

        if (_signals.Count > 0)
        {
            DroppedSignals += _signals.Count;
            _signals.Clear();
        }

        return handled;
    }

    /// <inheritdoc />
    /// <remarks>
    /// **先取走再清空**（而不是边遍历边清）：这样在效果执行期间新排进来的条目
    /// 落在下一批，不会被本轮顺手吃掉 —— 级联因此严格"一 tick 一跳"，可复现。
    /// </remarks>
    public IReadOnlyList<WorldEvent> DrainForTick(int tick)
    {
        if (_pending.Count == 0) return Array.Empty<WorldEvent>();

        var batch = _pending;
        _pending = new List<WorldEvent>(batch.Count);
        return batch;
    }

    /// <summary>反应器请求后续世界变化的出口。</summary>
    public bool Enqueue(string name, string description, Action effect)
    {
        if (effect == null) throw new ArgumentNullException(nameof(effect));
        if (_pending.Count >= MaxEffectsPerTick)
        {
            DroppedEffects++;
            return false;
        }

        _pending.Add(new WorldEvent { Name = name, Description = description, Effect = effect });
        return true;
    }
}

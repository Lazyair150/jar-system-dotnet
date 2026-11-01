using System;
using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Orders;
using JarSystem.Core.Sessions;

namespace JarSystem.Core.World;

/// <summary>
/// **第一个真实的世界反应器**：工作台能力或罐液资源变了，受影响的订单的可行性结论就不再成立。
///
/// <para><b>它修的是一个真 bug，不是一个理论隐患。</b>
/// <see cref="Order.FeasibilityResult"/> 是**缓存**：`ExecuteOrder` 会读它的
/// <c>SolverPath</c> 照着执行（`OrderService.cs:167-168`）。而这份缓存不记录"算于哪一版工作台"。
/// 于是「先算可行性 → 再换台（主台能力变了）→ 再执行」会**照着一份旧世界算出来的计划去动新世界**：
/// 换台后形状、禁令、罐位都已不同，计划里的动作可能根本不再合法。</para>
///
/// <para><b>为什么清空而不是重算。</b>重算是把秒级的判定塞进 tick 管线 —— 一个 tick 花多久会取决于
/// 世界上有多少张订单。清空是常数时间，而且语义更诚实：旧的结论**已经错了**，不是"过时但可用"。
/// 下一次 <c>CheckFeasibility</c> 会重新算。</para>
///
/// <para><b>为什么按"引用相同"清，而不是整片清。</b>这是本类唯一微妙的地方，也是它第一版被自己的
/// 测试抓出来的地方：失效是**排到下一个 tick** 才执行的，而这中间世界可能又变了 ——
/// 最典型的是「换台（排队失效）→ 用户在同一 tick 里重新判定（写入新结论）→ tick 到了」。
/// 若此时无差别清空，就会把**刚刚算出的、完全正确的新结论**一起抹掉。
/// 所以入队时记下每份结论的**对象引用**，执行时只清"还是当初那一个"的缓存：
/// 中间重算过的，引用已经换了，自然被跳过。</para>
/// </summary>
public sealed class FeasibilityInvalidationReactor : IWorldReactor
{
    /// <summary>
    /// 这些意图**不**改动工作台的形状/禁令/罐位，也不消耗罐液资源，因此不会让已算出的结论失效。
    ///
    /// 特意用"无害"白名单而不是"有害"黑名单：新增的世界操作默认被当作有害，
    /// 漏列的代价只是多清一次缓存（下一次判定会重算，安全）；
    /// 黑名单漏列的代价是留着错的计划照做（危险）。
    /// </summary>
    private static readonly HashSet<string> Harmless = new(StringComparer.Ordinal)
    {
        // 这两个本身就是结论的产出，不可能让别的结论失效。
        WorldOps.CheckFeasibility,
        WorldOps.CheckAllFeasibility,

        // 只改"选哪个台"，不动任何台的形状/禁令/罐位；真正的资源搬运由 setMaster/setSlave 负责。
        WorldOps.SetMasterIndex,
        WorldOps.SetSlaveIndex,

        // 只改订单集，不动世界的罐液资源 —— 新增一张订单不该让**别的**订单的结论失效。
        WorldOps.AddOrder,
        WorldOps.ClearOrders,
        WorldOps.CreateOrder,
        WorldOps.CreateRandomOrder,
        WorldOps.DeleteOrder,

        // 过程编排：真正的资源消耗由它内部的 executeOrder 记一条，会在那里触发。
        WorldOps.StartOrder,
        WorldOps.CancelProcess,
    };

    private readonly Func<IReadOnlyList<Order>> _orders;
    private readonly Func<string> _worldSignature;

    /// <param name="orders">取当前订单表的回调（由门面注入，反应器自身不持有世界）。</param>
    /// <param name="worldSignature">
    /// 判定所依赖的世界内容指纹（与 `OrderService.WorldSignature` 同一个）。
    /// **这是本类真正的判据**：不看发生了什么操作，只看"判定的输入有没有变"。
    /// </param>
    public FeasibilityInvalidationReactor(Func<IReadOnlyList<Order>> orders, Func<string> worldSignature)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _worldSignature = worldSignature ?? throw new ArgumentNullException(nameof(worldSignature));
    }

    /// <summary>上一次见过的世界内容指纹（=== 表示"什么都没变"）。</summary>
    private string? _seenSignature;

    /// <summary>累计清空掉的缓存结论条数（诊断用，不参与任何对外契约）。</summary>
    public int Invalidated { get; private set; }

    /// <inheritdoc />
    public void React(in WorldSignal signal, IWorldEffectSink sink)
    {
        // 只关心意图级世界变更；过程状态的变化不改变资源（过程的效果各自会记一条 WorldChanged）。
        if (signal.Kind != WorldSignalKind.WorldChanged) return;
        if (Harmless.Contains(signal.Op)) return;

        // **按内容指纹判断，而不是按操作名**。这一点很关键：`Tick` 每次都会记一条
        // `WorldOps.Tick`，但它产出的资源量由速率决定（0.4/tick 意味着一半的 tick 什么都不产），
        // 所以"发生过 tick"≠"判定输入变了"。按操作名一刀切，会让每个 tick 都清一次缓存，
        // 把缓存彻底废掉。这里只看指纹变没变。
        var signature = _worldSignature();
        if (string.Equals(signature, _seenSignature, StringComparison.Ordinal)) return;
        _seenSignature = signature;

        // 入队时就记下"此刻哪些结论存在"，执行时按**引用**核对。
        // 这里只读对象引用、不读工作台状态 —— 信号分发时世界正处在"改到一半"，
        // 读资源是不安全的，但读引用是安全的。
        var captured = new List<(Order Order, FeasibilityResultData Result)>();
        foreach (var order in _orders())
        {
            if (order.Status != Constants.OrderStatus.PENDING) continue;
            var result = order.FeasibilityResult;
            if (result == null) continue;
            captured.Add((order, result));
        }

        if (captured.Count == 0) return;

        sink.Enqueue(
            "invalidateFeasibility",
            "资源变更后清空待定订单的可行性缓存（原因：" + signal.Op + "）",
            () =>
            {
                foreach (var (order, result) in captured)
                {
                    // 只清"还是当初那一份"的缓存：中间被重新算过的，引用已经换了，跳过。
                    if (!ReferenceEquals(order.FeasibilityResult, result)) continue;
                    order.ClearFeasibility();
                    Invalidated++;
                }
            });
    }
}

using JarSystem.Core.Abstractions;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Process;

/// <summary>
/// 把「订单执行会话」接到过程系统上的**工作体**：一个单位 = 会话的一次推进；
/// 单位跑完时执行**一次性收尾**（交付 / 失败落账 / 事件历史 / 订单状态）。
///
/// 两条轨都用它：
/// <list type="bullet">
/// <item>同步 drain：门面在一个调用里反复 <see cref="AdvanceOneUnit"/> 直到结束；</item>
/// <item>异步：tick 管线按步间隔每次推进一个单位。</item>
/// </list>
/// 收尾必须**恰好一次**（交付会改世界、写事件历史），因此这里用 <see cref="_completed"/> 兜住重入。
/// </summary>
internal sealed class OrderProcessAdvancer : IProcessAdvancer
{
    private readonly IOrderExecutionCompletion _completion;
    private readonly OrderExecutionSession _session;
    private readonly Func<GuardResult> _guard;
    private bool _completed;

    public OrderProcessAdvancer(IOrderExecutionCompletion completion, OrderExecutionSession session,
        Func<GuardResult>? guard = null)
    {
        _completion = completion;
        _session = session;
        // 守卫由门面提供（只有它认识"世界"）；缺省时恒为"成立"，于是同步轨与既有测试不受影响。
        _guard = guard ?? GuardResult.Ok;
    }

    /// <summary>执行结果五元组（收尾后填充；供调用方返回给宿主）。</summary>
    public (bool Success, string? Reason, Dictionary<string, object?>? Event, bool TransferOccurred,
        List<ExecutionLogEntry>? Operations)? Result { get; private set; }

    public bool AdvanceOneUnit(IProcessRecorder recorder) => _session.AdvanceOneUnit(recorder);

    public bool Succeeded => _session.Succeeded;

    public string? FailureReason => _session.Reason;

    /// <inheritdoc />
    public GuardResult CheckGuard() => _guard();

    /// <inheritdoc />
    public void MarkFailed(string reason) => _session.FailByGuard(reason);

    /// <summary>幂等收尾：订单交付 / 失败 / 事件历史 / 订单状态（与既有尾部逻辑同一份实现）。</summary>
    public void OnFinished()
    {
        if (_completed) return;
        _completed = true;
        Result = _completion.Complete(_session);
    }
}

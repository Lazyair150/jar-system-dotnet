using System;
using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Process;

namespace JarSystem.Core.Services;

/// <summary>
/// 订单相关用例（原 <c>JarSystem.CreateOrder / CreateRandomOrder / DeleteOrder / CheckFeasibility /
/// CheckAllFeasibility / ExecuteOrder</c>）。
///
/// 状态与协作者（<c>Checker</c>/<c>Executor</c>/<c>Orders</c>/<c>EventHistory</c>）一律从门面实时读取，
/// 保证 <c>Reset()</c> 或外部替换协作者后行为与重构前一致。
/// </summary>
internal sealed class OrderService : IOrderExecutionCompletion
{
    private readonly JarSystem _system;

    public OrderService(JarSystem system)
    {
        _system = system;
    }

    public (bool Success, string? Error) CreateOrder(List<Requirement> requirements, string orderType = Constants.OrderType.A)
    {
        try
        {
            // 订单 ID 源同样可注入（回放/多世界一致性，见 JarSystem.OrderIds）
            var order = new Order(requirements, orderType, _system.OrderIds, _system.Vocabulary);
            _system.OrderList.Add(order);
            return (true, null);
        }
        catch (Exception e) { return (false, e.Message); }
    }

    /// <summary>
    /// 按**模板名**派一张订单。模板来自 <c>JarSystem.OrderTemplates</c>（即世界配置）。
    ///
    /// <para>与 <see cref="CreateOrder"/> 的唯一区别是"需求从哪来"：这里从模板实例化一份新列表，
    /// 其余完全一样（同一个 ID 源、同一份词表、同一个入口）⇒ 回放语义不变。</para>
    ///
    /// <para>与 <see cref="CreateRandomOrder"/> 的分工：这里按**名字**取一张确定的模板；
    /// 那里在模板库非空时**随机抽**一张，库空时退回"现掷骰子造需求"的旧行为。</para>
    /// </summary>
    public (bool Success, string? Error) CreateOrderFromTemplate(string templateName)
    {
        if (!_system.OrderTemplates.TryGet(templateName, out var template))
            return (false, "没有这个订单模板: " + templateName);
        return CreateOrder(template.Instantiate(), template.Type);
    }

    /// <summary>
    /// 随机派一张订单。
    ///
    /// <para><b>两种模式（2026-08 定案，由"订单模板库"引入）</b>：</para>
    /// <list type="number">
    /// <item>模板库**非空** ⇒ 从库里**随机抽一张模板**并实例化。订单因此是"世界内容"里有的东西，
    ///       而不是掷出来的组合 —— 这是模板库存在的意义；</item>
    /// <item>模板库**为空** ⇒ 退回原来那段"现掷骰子造需求"的逻辑，**逐字未改**
    ///       （空库的世界行为与引入模板库之前完全一致）。</item>
    /// </list>
    ///
    /// <para><b>为什么仍然可回放</b>：两次抽签都只用门面的 <c>Rng</c>，而 <c>Rng</c> 由种子决定、
    /// 命令顺序决定消耗了多少随机数 ⇒ 同一份命令日志必然抽出同一张模板。
    /// 这条命令**不记参数**（<c>WorldOps.CreateRandomOrder</c> 无字段），所以"抽到哪张"必须
    /// 由种子＋命令序列唯一决定 —— 这正是上面那条推理要成立的原因。</para>
    /// </summary>
    public (bool Success, string? Error) CreateRandomOrder()
    {
        var rand = _system.Rng;  // T4：共用门面的随机源（给了种子即可复现）
        var library = _system.OrderTemplates;
        if (!library.IsEmpty)
        {
            var picked = library.Get(rand.Next(library.Count));
            return CreateOrder(picked.Instantiate(), picked.Type);
        }
        // S2：随机需求按**词表**取（原先写死读 Constants.SHAPES / Constants.LIQUIDS）
        var shapes = _system.Vocabulary.Shapes;
        var liquids = _system.Vocabulary.Liquids;
        var numReqs = rand.Next(1, 4);
        var requirements = new List<Requirement>();
        var numEmpty = rand.Next(1, numReqs + 1);
        for (int i = 0; i < numEmpty; i++)
            requirements.Add(new Requirement { Shape = shapes[rand.Next(shapes.Count)] });
        for (int i = 0; i < numReqs - numEmpty; i++)
            requirements.Add(new Requirement { Shape = shapes[rand.Next(shapes.Count)], Liquid = liquids[rand.Next(liquids.Count)] });
        return CreateOrder(requirements, rand.NextDouble() > 0.5 ? Constants.OrderType.A : Constants.OrderType.B);
    }

    public object? DeleteOrder(int index)
    {
        if (index >= 0 && index < _system.Orders.Count)
        {
            var order = _system.Orders[index];

            // 订单不能从运行中的过程脚底下消失：过程持有 subjectId，订单没了它就指向不存在的东西
            // （世界不变量 PROCESS-ORDER-MISSING）。这里**拒绝**而不是偷偷取消过程——
            // 取消需要调用方知道，否则"删了订单却发现过程也停了"同样是意外。
            var active = _system.Processes.ActiveProcesses
                .FirstOrDefault(p => p.Kind == ProcessKinds.OrderExecution && p.SubjectId == order.Id);
            if (active != null)
                return new
                {
                    success = false,
                    reason = "订单 #" + order.Id + " 正在执行中（过程 #" + active.Id + "）：先取消过程，再删除订单"
                };

            _system.OrderList.RemoveAt(index);
            return new { success = true };
        }
        return new { success = false, reason = "无效索引" };
    }

    /// <summary>可行性分析：A 类只需主台；B 类需主从（结果同时写入订单的 FeasibilityResult）。</summary>
    public FeasibilityResultData? CheckFeasibility(int orderIndex)
    {
        if (orderIndex < 0 || orderIndex >= _system.Orders.Count)
            return new FeasibilityResultData { Feasible = false, Reason = "无效订单索引" };
        if (_system.CurrentMaster == null)
        {
            var r = new FeasibilityResultData { Feasible = false, Reason = "未选择主工作台" };
            _system.Orders[orderIndex].RecordFeasibility(r);
            Remember(_system.Orders[orderIndex]);
            return r;
        }
        var order = _system.Orders[orderIndex];
        var master = _system.Workshops[_system.CurrentMaster.Value];
        Workshop? slave = _system.CurrentSlave.HasValue ? _system.Workshops[_system.CurrentSlave.Value] : null;
        if (order == null || master == null)
        {
            var r = new FeasibilityResultData { Feasible = false, Reason = "无效参数" };
            order!.RecordFeasibility(r);
            Remember(order!);
            return r;
        }
        if (order.Type == Constants.OrderType.B && slave == null)
        {
            var r = new FeasibilityResultData { Feasible = false, Reason = "B类订单需要选择从台" };
            order.RecordFeasibility(r);
            Remember(order);
            return r;
        }
        // A 类订单无需从台：无从台时以主台快照占位（CheckTypeA 不读取从台）
        var resultData = _system.Checker.Check(order, master.CreateSnapshot(), (slave ?? master).CreateSnapshot());
        order.RecordFeasibility(resultData);
        Remember(order);
        return resultData;
    }

    /// <summary>
    /// 「这张订单的结论算在世界第几修订号上」。
    ///
    /// 为什么要它：宿主每 5 秒调一次 <see cref="CheckAllFeasibility"/>（`AutoCheckBackgroundService.cs:11`），
    /// 而那 5 秒里世界往往**什么都没变** —— 判定结果本来就存在订单上，重算纯属重复劳动。
    /// `JarSystem.WorldRevision`（= 命令日志序号）正好是天然的失效键：它只在实际改了世界时才变。
    ///
    /// 与 `World.FeasibilityInvalidationReactor` 的分工：那边负责在**世界真的变了**时把缓存清掉，
    /// 这边负责在**世界没变**时不去碰它。两条合起来，判定的执行次数才与"世界变化次数"成正比，
    /// 而不是与"定时器滴答次数"成正比。
    ///
    /// 刻意做成**旁挂表**而不是往 <c>FeasibilityResultData</c> 加字段：那是对外契约，
    /// 加键要连带改 snapshot / schema / contract.d.ts / 协议清单，而这只是内部记账。
    /// </summary>
    private readonly Dictionary<int, string> _resultSignature = new();

    /// <summary>
    /// 世界**内容指纹**：判定所依赖的一切（主从台选择 + 每台的标记/禁令 + 罐子集合 + 液池）。
    ///
    /// 为什么不用 <c>JarSystem.WorldRevision</c>：`Tick` 每次都会记一条 `WorldOps.Tick`
    /// （`JarSystem.cs:683`，且发生在产出与流入**之后**），所以修订号每 tick 必增 ——
    /// 默认 2 秒一 tick、自动检测 5 秒一次，两次检测之间修订号几乎必然变过，缓存永远不命中。
    /// 而判定真正关心的只有上面那几样；它们没变就**一定**得出同一个结论。
    ///
    /// 指纹里带上罐子 Id：结论里的 <c>transferJarIds</c> 是具体 Id，换一只同形同液的罐子结论就不同。
    /// </summary>
    internal string WorldSignature()
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(_system.CurrentMaster?.ToString() ?? "-").Append('/')
          .Append(_system.CurrentSlave?.ToString() ?? "-");
        for (int i = 0; i < _system.Workshops.Count; i++)
        {
            var w = _system.Workshops[i];
            sb.Append('').Append(i).Append(':');
            foreach (var m in w.Capability.BaseMarks) sb.Append(m).Append(',');
            sb.Append('/');
            foreach (var m in w.Capability.AdditionalMarks) sb.Append(m).Append(',');
            sb.Append('/');
            foreach (var m in w.Capability.Ban) sb.Append(m).Append(',');
            sb.Append('/');
            foreach (var jar in w.Jars)
                sb.Append(jar.Id?.ToString() ?? "?").Append(jar.Shape).Append(jar.Liquid ?? "-").Append('');
            sb.Append('/');
            foreach (var kv in w.LiquidStorage)
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(',');
        }
        return sb.ToString();
    }

    /// <summary>记下"这张订单的结论算在世界第几修订号上"（与 <see cref="_resultRevision"/> 配套）。</summary>
    private void Remember(Orders.Order? order)
    {
        if (order != null) _resultSignature[order.Id] = WorldSignature();
    }

    /// <summary>对所有「待执行」订单做可行性分析（元素见 <see cref="AutoCheckResultDto"/>：index / orderId / result）。</summary>
    public List<AutoCheckResultDto> CheckAllFeasibility()
    {
        var results = new List<AutoCheckResultDto>();
        var signature = WorldSignature();
        for (int i = 0; i < _system.Orders.Count; i++)
        {
            var order = _system.Orders[i];
            if (order.Status != Constants.OrderStatus.PENDING) continue;

            // 世界没变、结论还在 ⇒ 直接复用，不重算。
            bool reusable = order.FeasibilityResult != null
                && _resultSignature.TryGetValue(order.Id, out var at)
                && string.Equals(at, signature, StringComparison.Ordinal);

            results.Add(new AutoCheckResultDto
            {
                Index = i,
                OrderId = order.Id,
                Result = reusable ? order.FeasibilityResult : CheckFeasibility(i)
            });
        }
        return results;
    }

    /// <summary>
    /// 执行订单。返回五元组：成功 / 原因 / 事件报告 / 是否发生转移 / 操作日志。
    ///
    /// 关于 <c>EventHistory</c>：**进入执行之后**无论成败都会追加一条（键集合是对外契约）；
    /// 但**前置校验拒绝的 5 条路径不追加任何记录** —— 非法下标 / 未选主台 / 无效参数 /
    /// B 类未选从台 / 订单已是终态（原注释写的"无论成败都会追加"漏掉了这 5 条，2026-09-16 更正）。
    /// </summary>
    /// <param name="recorder">
    /// 过程记账器：执行过程中的每一步都会报告给它（写进过程日志）。
    /// null 表示不记账 —— 既有调用点与测试可直接不传，行为与重构前逐字相同。
    /// </param>
    public (bool Success, string? Reason, Dictionary<string, object?>? Event, bool TransferOccurred, List<ExecutionLogEntry>? Operations)
        ExecuteOrder(int orderIndex, IProcessRecorder? recorder = null)
    {
        // 同步轨（既有 <c>executeOrder</c> 的语义）：准备好之后，在一个调用里把单位跑到完，再收尾。
        var (session, error) = BeginOrderExecution(orderIndex, recorder);
        if (session == null) return (false, error, null, false, null);
        while (session.AdvanceOneUnit(recorder)) { }
        return CompleteOrderExecution(session);
    }

    /// <summary>
    /// **准备**一次订单执行：前置校验 + 建立事件 + 解析计划 + 建会话；**不推进世界**。
    /// 返回 null 表示前置校验拒绝（与既有实现同一批拒绝路径与文案）。
    ///
    /// 拆分动机（P2b）：异步轨要在"启动"与"推进"之间留出时间，因此准备与收尾必须能分开，
    /// 而两条轨（同步 drain / 异步 tick）共用**同一个会话**，语义只有一份。
    /// </summary>
    internal (OrderExecutionSession? Session, string? Error) BeginOrderExecution(
        int orderIndex, IProcessRecorder? recorder)
    {
        if (orderIndex < 0 || orderIndex >= _system.Orders.Count)
            return (null, "无效订单索引");
        if (_system.CurrentMaster == null)
            return (null, "未选择主工作台");
        var order = _system.Orders[orderIndex];
        var master = _system.Workshops[_system.CurrentMaster.Value];
        Workshop? slave = _system.CurrentSlave.HasValue ? _system.Workshops[_system.CurrentSlave.Value] : null;
        if (order == null || master == null)
            return (null, "无效参数");
        if (order.Type == Constants.OrderType.B && slave == null)
            return (null, "B类订单需要选择从台");

        // 订单状态机前置校验：**终态订单不允许再次执行**。
        // 此前只有前端按钮做了可见性限制（非"待执行"不渲染执行按钮），而 WebSocket 命令没有闸门 ——
        // 直接发 executeOrder 就能对已成功/已失败的订单重复执行，导致同一订单在 `eventHistory`
        // 与交付账目里出现两次记录（罐子守恒仍成立，但审计语义被破坏）。
        if (order.Status != Constants.OrderStatus.PENDING)
            return (null, "订单已是终态（" + order.Status + "），不能重复执行");

        var effectiveSlave = slave ?? master;
        var event_ = new OrderEvent(order, master, effectiveSlave, _system.Log);
        event_.Start();

        List<SolverStepDto>? solverPath = null;
        // **拷贝**而不是直接交出引用：执行器拿到的必须是它自己的一份路径
        // （DTO 的集合同伴已改为只读，这里再显式分离一次，执行期间不再共享可变状态）。
        if (order.FeasibilityResult != null && order.FeasibilityResult.Feasible && order.FeasibilityResult.SolverPath != null)
            solverPath = new List<SolverStepDto>(order.FeasibilityResult.SolverPath);

        // 把"计划从哪来"报给过程日志（只进日志，不下发）。
        //
        // 判据是**勘验是否给了可行结论**，而不是"路径条数 > 0"：
        // 划分搜索的可行解里主台路径或从台路径**都可能为空**（例如"全部交给从台"），
        // 按条数判会把这种真·求解器计划误标成 greedy（P1 实测踩到）。
        // 注意这仍是**声明来源**：运行期若因路径失败降级到贪心，会留一条 fallback 里程碑。
        bool solverPlanned = order.FeasibilityResult is { Feasible: true };
        recorder?.SetPlan(solverPlanned ? PlanSources.Solver : PlanSources.Greedy,
            (solverPath?.Count ?? 0) + (order.FeasibilityResult?.MasterPath?.Count ?? 0));

        return (_system.Executor.BeginSession(order, master, effectiveSlave, event_, solverPath, recorder), null);
    }

    /// <summary>
    /// **收尾**：交付 / 失败落账 / 事件历史 / 订单状态（与改造前的尾部逻辑同一份实现）。
    /// 由执行轨在最后一个单位之后调用一次（见 <see cref="Process.OrderProcessAdvancer.OnFinished"/>）。
    /// </summary>
    /// <inheritdoc cref="Process.IOrderExecutionCompletion.Complete(Process.OrderExecutionSession)"/>
    internal (bool Success, string? Reason, Dictionary<string, object?>? Event, bool TransferOccurred, List<ExecutionLogEntry>? Operations)
        CompleteOrderExecution(OrderExecutionSession session)
    {
        var order = session.Order;
        var master = session.Master;
        var effectiveSlave = session.Slave;
        var event_ = session.Event;

        if (session.Succeeded)
        {
            var delivery = event_.Deliver();
            session.Recorder?.Milestone(ProcessMilestones.Delivery,
                delivery.Success ? "交付完成" : "交付失败：" + (delivery.Reason ?? "未给出原因"));
            if (delivery.Success) order.MarkSucceeded(); else order.MarkFailed();
            _system.AddEvent(new Dictionary<string, object?>
            {
                ["type"] = "order_event",
                ["order"] = order.Id,
                ["master"] = master.Name,
                ["slave"] = effectiveSlave.Name,
                ["success"] = delivery.Success,
                ["timestamp"] = _system.Now.ToString("o"),   // 用"本次意图的时间"（可记录/可回放）
                ["operations"] = session.Operations
            });
            return (delivery.Success, null, event_.GetReport(), event_.TransferOccurred, session.Operations);
        }
        else
        {
            event_.Fail(session.Reason);
            order.MarkFailed();
            _system.AddEvent(new Dictionary<string, object?>
            {
                ["type"] = "order_event",
                ["order"] = order.Id,
                ["master"] = master.Name,
                ["slave"] = effectiveSlave.Name,
                ["success"] = false,
                ["reason"] = session.Reason,
                ["timestamp"] = _system.Now.ToString("o"),   // 同上
                ["operations"] = session.Operations
            });
            return (false, session.Reason, event_.GetReport(), event_.TransferOccurred, session.Operations);
        }
    }

    /// <summary>
    /// 端口实现（P4c）：过程系统只认 <see cref="IOrderExecutionCompletion"/>，因此这里显式转发到
    /// <see cref="CompleteOrderExecution"/> —— 依赖方向变成 **Services → Process**（单向），
    /// 而 Process 不再认识 Services。
    /// </summary>
    (bool Success, string? Reason, Dictionary<string, object?>? Event, bool TransferOccurred, List<ExecutionLogEntry>? Operations)
        IOrderExecutionCompletion.Complete(OrderExecutionSession session) => CompleteOrderExecution(session);
}

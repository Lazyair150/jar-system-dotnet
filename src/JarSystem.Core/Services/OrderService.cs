using System;
using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Services;

/// <summary>
/// 订单相关用例（原 <c>JarSystem.CreateOrder / CreateRandomOrder / DeleteOrder / CheckFeasibility /
/// CheckAllFeasibility / ExecuteOrder</c>）。
///
/// 状态与协作者（<c>Checker</c>/<c>Executor</c>/<c>Orders</c>/<c>EventHistory</c>）一律从门面实时读取，
/// 保证 <c>Reset()</c> 或外部替换协作者后行为与重构前一致。
/// </summary>
internal sealed class OrderService
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

    public (bool Success, string? Error) CreateRandomOrder()
    {
        var rand = _system.Rng;  // T4：共用门面的随机源（给了种子即可复现）
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
        if (index >= 0 && index < _system.Orders.Count) { _system.OrderList.RemoveAt(index); return new { success = true }; }
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
            return r;
        }
        var order = _system.Orders[orderIndex];
        var master = _system.Workshops[_system.CurrentMaster.Value];
        Workshop? slave = _system.CurrentSlave.HasValue ? _system.Workshops[_system.CurrentSlave.Value] : null;
        if (order == null || master == null)
        {
            var r = new FeasibilityResultData { Feasible = false, Reason = "无效参数" };
            order!.RecordFeasibility(r);
            return r;
        }
        if (order.Type == Constants.OrderType.B && slave == null)
        {
            var r = new FeasibilityResultData { Feasible = false, Reason = "B类订单需要选择从台" };
            order.RecordFeasibility(r);
            return r;
        }
        // A 类订单无需从台：无从台时以主台快照占位（CheckTypeA 不读取从台）
        var resultData = _system.Checker.Check(order, master.CreateSnapshot(), (slave ?? master).CreateSnapshot());
        order.RecordFeasibility(resultData);
        return resultData;
    }

    /// <summary>对所有「待执行」订单做可行性分析（元素见 <see cref="AutoCheckResultDto"/>：index / orderId / result）。</summary>
    public List<AutoCheckResultDto> CheckAllFeasibility()
    {
        var results = new List<AutoCheckResultDto>();
        for (int i = 0; i < _system.Orders.Count; i++)
        {
            if (_system.Orders[i].Status == Constants.OrderStatus.PENDING)
                results.Add(new AutoCheckResultDto
                {
                    Index = i,
                    OrderId = _system.Orders[i].Id,
                    Result = CheckFeasibility(i)
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
    public (bool Success, string? Reason, Dictionary<string, object?>? Event, bool TransferOccurred, List<ExecutionLogEntry>? Operations)
        ExecuteOrder(int orderIndex)
    {
        if (orderIndex < 0 || orderIndex >= _system.Orders.Count)
            return (false, "无效订单索引", null, false, null);
        if (_system.CurrentMaster == null)
            return (false, "未选择主工作台", null, false, null);
        var order = _system.Orders[orderIndex];
        var master = _system.Workshops[_system.CurrentMaster.Value];
        Workshop? slave = _system.CurrentSlave.HasValue ? _system.Workshops[_system.CurrentSlave.Value] : null;
        if (order == null || master == null)
            return (false, "无效参数", null, false, null);
        if (order.Type == Constants.OrderType.B && slave == null)
            return (false, "B类订单需要选择从台", null, false, null);

        // 订单状态机前置校验：**终态订单不允许再次执行**。
        // 此前只有前端按钮做了可见性限制（非"待执行"不渲染执行按钮），而 WebSocket 命令没有闸门 ——
        // 直接发 executeOrder 就能对已成功/已失败的订单重复执行，导致同一订单在 `eventHistory`
        // 与交付账目里出现两次记录（罐子守恒仍成立，但审计语义被破坏）。
        if (order.Status != Constants.OrderStatus.PENDING)
            return (false, "订单已是终态（" + order.Status + "），不能重复执行", null, false, null);

        var effectiveSlave = slave ?? master;
        var event_ = new OrderEvent(order, master, effectiveSlave, _system.Log);
        event_.Start();

        List<SolverStepDto>? solverPath = null;
        // **拷贝**而不是直接交出引用：执行器拿到的必须是它自己的一份路径
        // （DTO 的集合同伴已改为只读，这里再显式分离一次，执行期间不再共享可变状态）。
        if (order.FeasibilityResult != null && order.FeasibilityResult.Feasible && order.FeasibilityResult.SolverPath != null)
            solverPath = new List<SolverStepDto>(order.FeasibilityResult.SolverPath);

        var execResult = _system.Executor.Execute(order, master, effectiveSlave, event_, solverPath);

        if (execResult.Success)
        {
            var delivery = event_.Deliver();
            if (delivery.Success) order.MarkSucceeded(); else order.MarkFailed();
            _system.AddEvent(new Dictionary<string, object?>
            {
                ["order"] = order.Id,
                ["master"] = master.Name,
                ["slave"] = effectiveSlave.Name,
                ["success"] = delivery.Success,
                ["timestamp"] = _system.Now.ToString("o"),   // 用"本次意图的时间"（可记录/可回放）
                ["operations"] = execResult.Operations
            });
            return (delivery.Success, null, event_.GetReport(), event_.TransferOccurred, execResult.Operations);
        }
        else
        {
            event_.Fail(execResult.Reason);
            order.MarkFailed();
            _system.AddEvent(new Dictionary<string, object?>
            {
                ["order"] = order.Id,
                ["master"] = master.Name,
                ["slave"] = effectiveSlave.Name,
                ["success"] = false,
                ["reason"] = execResult.Reason,
                ["timestamp"] = _system.Now.ToString("o"),   // 同上
                ["operations"] = execResult.Operations
            });
            return (false, execResult.Reason, event_.GetReport(), event_.TransferOccurred, execResult.Operations);
        }
    }
}

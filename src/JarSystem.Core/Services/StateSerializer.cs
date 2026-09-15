using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Services;

/// <summary>
/// 对外状态快照（<c>JarSystem.GetState()</c>）的唯一实现（阶段 3：由手工字典树改为强类型 DTO 投影）。
///
/// **契约由 <see cref="StateDto"/> 及其成员上的 <c>[JsonPropertyName]</c> 冻结**，
/// 字段名、层级、别名与顺序都与重构前的字典写法一一对应（前端 app.js / Godot 文档 / 契约快照依赖）。
/// </summary>
internal static class StateSerializer
{
    public static StateDto Snapshot(JarSystem system) => new()
    {
        Workshops = system.Workshops.Select((w, i) => SerializeWorkshop(system, w, i)).ToList(),
        Orders = system.Orders.Select((o, i) => SerializeOrder(o, i)).ToList(),
        Warehouse = new WarehouseStateDto
        {
            Jars = system.ProducerModule.WarehouseJarCount,
            Liquids = system.ProducerModule.LiquidWarehouse.ToDictionary(kv => kv.Key, kv => kv.Value),
            DiscardedJars = system.ProducerModule.DiscardedJars,
            DiscardedLiquids = system.ProducerModule.DiscardedLiquids.ToDictionary(kv => kv.Key, kv => kv.Value)
        },
        ProducerStatus = system.ProducerModule.GetProducerStatus(),
        GlobalState = new GlobalStateDto
        {
            AutoCheckEnabled = system.AutoCheckEnabled,
            CurrentMaster = system.CurrentMaster,
            CurrentSlave = system.CurrentSlave,
            MasterName = system.CurrentMaster.HasValue ? system.Workshops[system.CurrentMaster.Value]?.Name : null,
            SlaveName = system.CurrentSlave.HasValue ? system.Workshops[system.CurrentSlave.Value]?.Name : null
        },
        EventHistory = system.EventHistory.TakeLast(10).ToList()
    };

    private static WorkshopStateDto SerializeWorkshop(JarSystem system, Workshop ws, int index) => new()
    {
        Index = index,
        Name = ws.Name,
        BaseMarks = ws.BaseMarks,
        AdditionalMarks = ws.AdditionalMarks,
        Ban = ws.Ban,
        MaxJars = ws.MaxJars,
        MaxLiquids = ws.MaxLiquids,
        Jars = ws.Jars.Select(j => new JarStateDto
        {
            Id = j.Id,
            Shape = j.Shape,
            Liquid = j.Liquid,
            IsMarked = j.IsMarked,
            Description = j.GetDescription()
        }).ToList(),
        Liquids = ws.LiquidStorage.ToDictionary(kv => kv.Key, kv => kv.Value),
        CanPourShapes = ws.BaseMarks,
        IsMaster = index == system.CurrentMaster,
        IsSlave = index == system.CurrentSlave
    };

    private static OrderStateDto SerializeOrder(Order order, int index)
    {
        FeasibilityStateDto? fr = null;
        if (order.FeasibilityResult != null)
            fr = new FeasibilityStateDto
            {
                Feasible = order.FeasibilityResult.Feasible,
                Blockage = order.FeasibilityResult.Blockage,
                Reason = order.FeasibilityResult.Reason,
                Timestamp = order.FeasibilityResult.Timestamp,
                DirectCount = order.FeasibilityResult.DirectCount,
                OperationCount = order.FeasibilityResult.OperationCount,
                SolverPath = order.FeasibilityResult.SolverPath,
                MasterPath = order.FeasibilityResult.MasterPath,
                TransferJarIds = order.FeasibilityResult.TransferJarIds,
                TransferLiquids = order.FeasibilityResult.TransferLiquids
            };

        return new OrderStateDto
        {
            Index = index,
            Id = order.Id,
            Type = order.Type,
            Requirements = order.Requirements,
            Status = order.Status,
            FeasibilityResult = fr,
            Description = order.ToString()
        };
    }
}

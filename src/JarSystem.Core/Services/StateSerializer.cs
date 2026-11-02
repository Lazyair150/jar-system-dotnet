using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Process;
using JarSystem.Core.Abstractions;

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
            // 仓库这一块现在归 Production.Warehouse 管（2026-09 从 ProducerModule 抽出）
            Jars = system.ProducerModule.Warehouse.JarCount,
            Liquids = system.ProducerModule.Warehouse.LiquidWarehouse.ToDictionary(kv => kv.Key, kv => kv.Value),
            DiscardedJars = system.ProducerModule.Warehouse.DiscardedJars,
            DiscardedLiquids = system.ProducerModule.Warehouse.DiscardedLiquids.ToDictionary(kv => kv.Key, kv => kv.Value)
        },
        // 产出模块：投影自 ProducerModule 的只读视图（键名/顺序与原先 GetProducerStatus() 的字典逐项一致）
        ProducerStatus = new ProducerStatusDto
        {
            Producers = system.ProducerModule.Producers.Values
                .Select(p => new ProducerInfoDto
                {
                    Name = p.Name,
                    Rates = new Dictionary<string, double>(p.Rates)
                })
                .ToList(),
            Generators = system.ProducerModule.Generators.Values
                .Select(g => new GeneratorStatusDto
                {
                    Shape = g.Shape,
                    Rate = g.GetTotalRate(),
                    Accumulated = g.Accumulated,
                    Producers = g.Producers.Count
                })
                .ToList(),
            Warehouse = new ProducerWarehouseDto
            {
                Jars = system.ProducerModule.Warehouse.JarCount,
                Liquids = system.ProducerModule.Warehouse.LiquidWarehouse.ToDictionary(kv => kv.Key, kv => kv.Value)
            }
        },
        GlobalState = new GlobalStateDto
        {
            AutoCheckEnabled = system.AutoCheckEnabled,
            CurrentMaster = system.CurrentMaster,
            CurrentSlave = system.CurrentSlave,
            MasterName = system.CurrentMaster.HasValue ? system.Workshops[system.CurrentMaster.Value]?.Name : null,
            SlaveName = system.CurrentSlave.HasValue ? system.Workshops[system.CurrentSlave.Value]?.Name : null
        },
        // 与内部保留窗口**同一个常量**（JarSystem.MaxEventHistory）—— 原先这里写死 `10`：
        // 两处独立的值一旦只改一处，对外 JSON 的条数就静默改变（快照立刻漂移，却看不出是有意还是漏改）。
        EventHistory = system.EventHistory.TakeLast(JarSystem.MaxEventHistory).ToList(),

        // P4：世界修订号 + 活动过程（进行中的过程才有内容；同步轨通常为空）
        WorldRevision = system.WorldRevision,        Processes = system.Processes.ActiveProcesses.Select(p => new ProcessStateDto
        {
            Id = p.Id,
            Kind = p.Kind,
            Subject = p.Subject,
            SubjectId = p.SubjectId,
            State = ProcessContract.Of(p.State),
            Outcome = p.Outcome.HasValue ? ProcessContract.Of(p.Outcome.Value) : null,
            CurrentStep = p.AppliedSteps,
            TotalSteps = p.DeclaredSteps,
            StartedAtTick = p.StartedAtTick,
            DeadlineTick = p.DeadlineTick,
            // 剩余刻数**服务端算好**（客户端不知道 tick 速率，自己算必错）
            RemainingTicks = p.DeadlineTick.HasValue
                ? Math.Max(0, p.DeadlineTick.Value - system.Processes.Clock.Tick)
                : null,
            BlockedReason = p.BlockedReason,
            LastJournalSeq = p.LastJournalSeq
        }).ToList(),

        // 回合预算：上限来自世界的关卡规则，已用来自世界时钟 —— 客户端只显示，不自己算。
        Turns = new TurnStateDto
        {
            Used = system.Turns.Used,
            Limit = system.Turns.Limit,
            Exhausted = system.Turns.IsExhausted
        },

        // 一局的结局：判据在内核（LevelGoal.Judge），这里只搬结论。
        Level = new LevelStateDto
        {
            GoalOrders = system.World.GoalOrders,
            CompletedOrders = system.CompletedOrders,
            Outcome = system.Outcome.Name,
            Reason = system.Outcome.Reason
        }
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
        return new OrderStateDto
        {
            Index = index,
            Id = order.Id,
            Type = order.Type,
            // 显式复制：域实体的 Requirements 现在是只读视图，不直接交进对外投影。
            // DTO 侧仍是 List<T>（契约键 requirements 的 CLR 类型不变，宿主读的也是这个 DTO）。
            Requirements = new List<Requirement>(order.Requirements),
            Status = order.Status,
            // 契约统一（2026-09）：直接投影同一个类型，不再做"另一份投影" —— 两种形状的根源就没了。
            FeasibilityResult = order.FeasibilityResult,
            Description = order.ToString()
        };
    }
}

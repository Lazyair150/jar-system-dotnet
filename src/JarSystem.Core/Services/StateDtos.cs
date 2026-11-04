using System.Collections.Generic;
using System.Text.Json.Serialization;
using JarSystem.Core.Orders;
using JarSystem.Core.Abstractions;

namespace JarSystem.Core.Services;

/// <summary>
/// 对外状态快照的强类型模型（原先是手工拼装的 <c>Dictionary&lt;string, object?&gt;</c> 树）。
///
/// **每个属性的 <c>[JsonPropertyName]</c> 都是对外契约**：前端 `app.js`、Godot 迁移文档、
/// 契约快照（`--dump-contract`）都依赖这些键名与层级。改键名 = 破坏契约。
/// 命名策略（camelCase）对本类的属性不再起作用，键名完全由特性决定。
/// </summary>
public sealed class StateDto
{
    [JsonPropertyName("workshops")] public List<WorkshopStateDto> Workshops { get; init; } = new();
    [JsonPropertyName("orders")] public List<OrderStateDto> Orders { get; init; } = new();
    [JsonPropertyName("warehouse")] public WarehouseStateDto Warehouse { get; init; } = new();

    /// <summary>生产者状态（3 个键：producers / generators / warehouse）。</summary>
    [JsonPropertyName("producerStatus")] public ProducerStatusDto ProducerStatus { get; init; } = new();

    [JsonPropertyName("globalState")] public GlobalStateDto GlobalState { get; init; } = new();

    /// <summary>最近 10 条事件（元素结构与各事件的写入处一致，属既有契约）。</summary>
    [JsonPropertyName("eventHistory")] public List<Dictionary<string, object?>> EventHistory { get; init; } = new();

    /// <summary>
    /// **世界修订号**（P3b/P4 新增键）：等于宿主"改世界意图"的序号。
    /// 用途：让客户端/诊断能说出"这份过程日志/这次分析看到的是世界的哪一版"。
    /// </summary>
    [JsonPropertyName("worldRevision")] public int WorldRevision { get; init; }

    /// <summary>
    /// **活动过程**（P4 新增键）：只列**进行中**的过程（同步轨在本调用内就跑完，因此通常为空；
    /// 异步轨的过程在多个 tick 之间活着，这里就是它们的进度）。元素见 <see cref="ProcessStateDto"/>。
    /// </summary>
    [JsonPropertyName("processes")] public List<ProcessStateDto> Processes { get; init; } = new();

    /// <summary>
    /// **回合预算**（关卡规则，新增键）：这一局还剩几回合、上限是多少、是否已经用完。
    /// 由内核算好，客户端不推：一回合 = 一个 tick，`limit = 0` 表示这个世界没有上限。
    /// </summary>
    [JsonPropertyName("turns")] public TurnStateDto Turns { get; init; } = new();

    /// <summary>
    /// 世界阶段（`setup` / `running` / `settled`，见 <see cref="World.WorldPhase"/>）。
    /// 页面据此决定"现在能点什么"，而不是靠"有没有订单在跑"这类各自拼凑的判断。
    /// </summary>
    [JsonPropertyName("phase")] public string Phase { get; init; } = "setup";

    /// <summary>
    /// **一局的结局**（新增键）：目标单数、已完成单数、赢/输/进行中与理由。
    /// 判据在内核（<c>LevelGoal.Judge</c>），客户端只显示 —— 界面算出来的"你赢了"
    /// 与内核算出来的必须永远是同一句话。
    /// </summary>
    [JsonPropertyName("level")] public LevelStateDto Level { get; init; } = new();
}

/// <summary>一局的结局（4 键）：客户端不自己判胜。</summary>
public sealed class LevelStateDto
{
    /// <summary>要做成几单算赢（0 = 无目标）。</summary>
    [JsonPropertyName("goalOrders")] public int GoalOrders { get; init; }
    [JsonPropertyName("completedOrders")] public int CompletedOrders { get; init; }
    /// <summary>`inProgress` / `won` / `lost`（世界语言，直接显示）。</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "inProgress";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>回合预算（3 键）：客户端只显示，不自己算"还剩几个"。</summary>
public sealed class TurnStateDto
{
    [JsonPropertyName("used")] public int Used { get; init; }
    /// <summary>0 = 不限。</summary>
    [JsonPropertyName("limit")] public int Limit { get; init; }
    /// <summary>已经用完 —— 再推进也不会生效（内核会拒绝，见 <c>JarSystem.Tick</c>）。</summary>
    [JsonPropertyName("exhausted")] public bool Exhausted { get; init; }
}

/// <summary>
/// 过程状态（**13 键**，按 P0 规格 §3.2 冻结；键名即契约）。
///
/// 刻意**不下发**的字段：`priority`（同 tick 推进顺序）、`planSource`（计划来源）、`graceTicks` 原始值、
/// 计划明细 —— 暗箱原则：客户端只拿"世界语言 + 进度 + 时限 + 原因"。
/// `remainingTicks` 由**服务端算好**（客户端不知道 tick 速率，自己算必错）。
/// </summary>
public sealed class ProcessStateDto
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";

    /// <summary>世界语言描述（例："委托 #12"），前端直接显示。</summary>
    [JsonPropertyName("subject")] public string Subject { get; init; } = "";

    /// <summary>结构化主体标识（订单 id / 工作台下标）。</summary>
    [JsonPropertyName("subjectId")] public int? SubjectId { get; init; }

    /// <summary>过程状态（契约取值：created/ready/running/blocked/succeeded/failed/aborted）。</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "";

    /// <summary>结尾（未终结为 null）。</summary>
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }

    /// <summary>已完成的步数。</summary>
    [JsonPropertyName("currentStep")] public int CurrentStep { get; init; }

    /// <summary>声明的总步数（0 = 未声明）。</summary>
    [JsonPropertyName("totalSteps")] public int TotalSteps { get; init; }

    [JsonPropertyName("startedAtTick")] public int? StartedAtTick { get; init; }
    [JsonPropertyName("deadlineTick")] public int? DeadlineTick { get; init; }

    /// <summary>剩余刻数（服务端算好；无时限为 null）。</summary>
    [JsonPropertyName("remainingTicks")] public int? RemainingTicks { get; init; }

    /// <summary>距离过程下一次执行单位的刻数（服务端算好；已终结为 null）。</summary>
    [JsonPropertyName("ticksUntilNextUnit")] public int? TicksUntilNextUnit { get; init; }

    /// <summary>被阻断的原因（世界语言；未阻断为 null）。</summary>
    [JsonPropertyName("blockedReason")] public string? BlockedReason { get; init; }

    /// <summary>该过程最后一条过程日志的序号（便于客户端发现"我漏了消息"）。</summary>
    [JsonPropertyName("lastJournalSeq")] public int LastJournalSeq { get; init; }
}

/// <summary>工作台状态（12 个键）。</summary>
public sealed class WorkshopStateDto
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("baseMarks")] public IReadOnlyList<string> BaseMarks { get; init; } = [];
    [JsonPropertyName("additionalMarks")] public IReadOnlyList<string> AdditionalMarks { get; init; } = [];
    [JsonPropertyName("ban")] public IReadOnlyList<string> Ban { get; init; } = [];
    [JsonPropertyName("maxJars")] public int MaxJars { get; init; }
    [JsonPropertyName("maxLiquids")] public int MaxLiquids { get; init; }
    [JsonPropertyName("jars")] public List<JarStateDto> Jars { get; init; } = new();
    [JsonPropertyName("liquids")] public Dictionary<string, int> Liquids { get; init; } = new();
    [JsonPropertyName("canPourShapes")] public IReadOnlyList<string> CanPourShapes { get; init; } = [];
    [JsonPropertyName("isMaster")] public bool IsMaster { get; init; }
    [JsonPropertyName("isSlave")] public bool IsSlave { get; init; }
}

/// <summary>罐子状态（5 个键；<c>liquid</c> 为 null 表示空罐）。</summary>
public sealed class JarStateDto
{
    [JsonPropertyName("id")] public int? Id { get; init; }
    [JsonPropertyName("shape")] public string Shape { get; init; } = "";
    [JsonPropertyName("liquid")] public string? Liquid { get; init; }
    [JsonPropertyName("isMarked")] public bool IsMarked { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
}

/// <summary>订单状态（7 个键）。</summary>
public sealed class OrderStateDto
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("requirements")] public List<Requirement> Requirements { get; init; } = new();
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    /// <summary>
    /// 最近一次可行性分析结果。**与消息里的 `feasibilityResult` 是同一个类型**（2026-09 契约统一）——
    /// 此前这里是一份 10 键投影、消息里是本体，前端被迫写"两种形状都兼容"的代码。
    /// ⚠️ 当时的记录把本体写成"16 键"（另有"15 键"的写法），**两个数字都不对**：
    /// 本体是 **12 键**，"15 键"其实是 `autoCheckResults` 的**载荷**（3 + 12）。见 `00_从这里继续.md` §9 勘误 #8。
    /// </summary>
    [JsonPropertyName("feasibilityResult")] public FeasibilityResultData? FeasibilityResult { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
}

// 2026-09 契约统一：删除了 FeasibilityStateDto（原本只用于 state 的 10 键投影）。
// 现在 state 与消息共用 Orders.FeasibilityResultData（12 键），前端不再需要"两种形状都兼容"。

/// <summary>中央仓库状态（4 个键）。</summary>
public sealed class WarehouseStateDto
{
    [JsonPropertyName("jars")] public int Jars { get; init; }

    /// <summary>
    /// 仓库里**每一只**罐子（顺序 = 流入时主台取用的顺序：先主台基础形状、再按先进先出）。
    /// 只有数量时页面画不出"一仓库的小方块"，玩家也就看不出下一回合会流进哪几只。
    /// 这只是同一份状态的另一种投影，不是第二个真相源。
    /// </summary>
    [JsonPropertyName("jarList")] public List<JarStateDto> JarList { get; init; } = new();
    [JsonPropertyName("liquids")] public Dictionary<string, int> Liquids { get; init; } = new();
    [JsonPropertyName("discardedJars")] public int DiscardedJars { get; init; }
    [JsonPropertyName("discardedLiquids")] public Dictionary<string, int> DiscardedLiquids { get; init; } = new();
}

/// <summary>全局状态（5 个键：自动检测开关与主从选择）。</summary>
public sealed class GlobalStateDto
{
    [JsonPropertyName("autoCheckEnabled")] public bool AutoCheckEnabled { get; init; }
    [JsonPropertyName("currentMaster")] public int? CurrentMaster { get; init; }
    [JsonPropertyName("currentSlave")] public int? CurrentSlave { get; init; }
    [JsonPropertyName("masterName")] public string? MasterName { get; init; }
    [JsonPropertyName("slaveName")] public string? SlaveName { get; init; }
}

// ============================================================
// 产出模块状态（原先是 ProducerModule.GetProducerStatus() 组装的
// Dictionary<string, object> 树 —— 契约里最后一个"没有类型"的大块）。
//
// 键名与层级完全按原字典的插入顺序声明，因此序列化结果逐字节相同
// （历史口径：本批的等价性证明是"当时契约快照保持 165 行不变"。
//   快照后来因 ①契约统一 ②快照覆盖修复（补 consumed 与空容器占位行）增长到 **223 行**，
//   两次增长都有独立记录，与本次强类型化无关。）
// ============================================================

/// <summary>生产、产能槽与仓库流入状态（5 个键）。</summary>
public sealed class ProducerStatusDto
{
    [JsonPropertyName("producers")] public List<ProducerInfoDto> Producers { get; init; } = new();
    [JsonPropertyName("generators")] public List<GeneratorStatusDto> Generators { get; init; } = new();

    /// <summary>形状产线的产能槽占用情况（与液体、流入无关）。</summary>
    [JsonPropertyName("slots")] public ProducerSlotStatusDto Slots { get; init; } = new();

    /// <summary>
    /// 中央仓库 → 主台的流入上限。它是**搬运参数**，不是生产速率：
    /// 即使液体自产为 0，仓库现有库存仍可能按此上限进入主台。
    /// </summary>
    [JsonPropertyName("flow")] public ProductionFlowStatusDto Flow { get; init; } = new();

    /// <summary>
    /// 每种液体每 tick 产出几份（键序 = 词表顺序）。罐子有生产者的速率，液体同样有，
    /// 只是它不属于任何一个生产者 —— 它是"世界自己产"的资源，所以单独一格。
    /// 键序固定才能让契约快照逐行可比。
    /// </summary>
    [JsonPropertyName("liquidRates")] public Dictionary<string, double> LiquidRates { get; init; } = new();

    /// <summary>每种液体眼下攒了多少（0 ≤ 值 &lt; 1）。有了它，"为什么这回合没产"就有直接答案。</summary>
    [JsonPropertyName("liquidAccumulated")] public Dictionary<string, double> LiquidAccumulated { get; init; } = new();
    [JsonPropertyName("warehouse")] public ProducerWarehouseDto Warehouse { get; init; } = new();
}

/// <summary>产线槽状态（2 键）：used = 速率大于 0 的“生产者 × 形状”数量。</summary>
public sealed class ProducerSlotStatusDto
{
    [JsonPropertyName("used")] public int Used { get; init; }
    /// <summary>0 = 不限。</summary>
    [JsonPropertyName("limit")] public int Limit { get; init; }
}

/// <summary>
/// 每次“仓库 → 主台”流入的请求上限。液体上限是**每种液体**各自的上限，
/// 不是所有液体合计；实际接收量还会受主台容量与已有库存钳制。
/// </summary>
public sealed class ProductionFlowStatusDto
{
    [JsonPropertyName("jars")] public int Jars { get; init; }
    [JsonPropertyName("liquidPerLiquid")] public int LiquidPerLiquid { get; init; }
}

/// <summary>生产者（2 个键：名称 + 各形状速率）。</summary>
public sealed class ProducerInfoDto
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("rates")] public Dictionary<string, double> Rates { get; init; } = new();
}

/// <summary>某形状的生产线状态（4 个键）。</summary>
public sealed class GeneratorStatusDto
{
    [JsonPropertyName("shape")] public string Shape { get; init; } = "";
    [JsonPropertyName("rate")] public double Rate { get; init; }
    [JsonPropertyName("accumulated")] public double Accumulated { get; init; }
    [JsonPropertyName("producers")] public int Producers { get; init; }
}

/// <summary>产出模块视角的仓库（2 个键：罐子数 + 液体库存）。</summary>
public sealed class ProducerWarehouseDto
{
    [JsonPropertyName("jars")] public int Jars { get; init; }
    [JsonPropertyName("liquids")] public Dictionary<string, int> Liquids { get; init; } = new();
}

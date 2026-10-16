using System.Collections.Generic;
using System.Text.Json.Serialization;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Sessions;

/// <summary>
/// **世界命令**（Event Sourcing 的一条记录）：描述"宿主对世界下了一次什么意图"，
/// 而不是结果。配合"初始世界 + 随机种子"，整条日志可以**重建出完全相同的世界状态**。
///
/// 为什么这件事现在才做得了：批 E 把门面上的所有变更收口成了**命名的意图方法**
/// （`CreateOrder`/`SetMaster`/`Tick`/…），变更再也没有第二条路可走 ——
/// 于是"记录意图"这件事才有一个**封闭且穷尽**的写入口。
///
/// 边界（v1 诚实声明）：
/// <list type="bullet">
/// <item>日志记录的是**宿主意图**，不是求值结果；重建靠"同样的初始世界 + 同样的种子 + 同样的命令序"；</item>
/// <item>因此**随机性必须可复现**：`JarSystem` 的 <c>Rng</c> 由构造时的种子决定，
/// 而罐子/订单 ID 必须由**注入的 ID 源**产生（否则进程级计数器会让重建出的 ID 与原次不同）；</item>
/// <item>对外 JSON 契约（`GetState()`/快照）**不受影响** —— 日志是新增 API，不进状态。</item>
/// </list>
/// </summary>
public sealed class WorldCommand
{
    /// <summary>序号（从 1 开始，仅用于诊断与对账）。</summary>
    [JsonPropertyName("seq")] public int Seq { get; init; }

    /// <summary>操作名（取值见 <see cref="WorldOps"/>）。</summary>
    [JsonPropertyName("op")] public string Op { get; init; } = "";

    /// <summary>
    /// 本条意图发生的时间（ISO-8601）。**存档必须带上它** ——
    /// 事件历史（`eventHistory[].timestamp`）是墙钟时间，回放时若用"现在"就会与原次不同，
    /// 于是"读档后状态逐字节一致"这条判据立刻失败（实测踩过）。
    /// </summary>
    [JsonPropertyName("time")] public string? Time { get; init; }

    /// <summary>工作台名（createWorkshop / addWorkshop）。</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>订单类型（A/B）。</summary>
    [JsonPropertyName("orderType")] public string? OrderType { get; init; }

    /// <summary>需求清单（createOrder / addOrder）。</summary>
    [JsonPropertyName("requirements")] public List<Requirement>? Requirements { get; init; }

    /// <summary>基础标记（createWorkshop）；改生产者速率时只取第 1 项作为形状。</summary>
    [JsonPropertyName("baseMarks")] public List<string>? BaseMarks { get; init; }

    /// <summary>附加标记（createWorkshop）。</summary>
    [JsonPropertyName("additionalMarks")] public List<string>? AdditionalMarks { get; init; }

    /// <summary>禁令（createWorkshop）；改液体速率时只取第 1 项作为液体名。</summary>
    [JsonPropertyName("ban")] public List<string>? Ban { get; init; }

    /// <summary>整台注入（addWorkshop）用的一份完整参数。</summary>
    [JsonPropertyName("workshop")] public WorkshopPayload? Workshop { get; init; }

    /// <summary>整数参数 A（下标 / 数量 / 罐位上限）。</summary>
    [JsonPropertyName("intA")] public int? IntA { get; init; }

    /// <summary>整数参数 B（主台流入罐数 / 工作台罐位上限）。</summary>
    [JsonPropertyName("intB")] public int? IntB { get; init; }

    /// <summary>整数参数 C（工作台液体上限）。</summary>
    [JsonPropertyName("intC")] public int? IntC { get; init; }

    /// <summary>浮点参数（生产者速率）。</summary>
    [JsonPropertyName("doubleA")] public double? DoubleA { get; init; }

    /// <summary>布尔参数（setAutoCheck）。</summary>
    [JsonPropertyName("flag")] public bool? Flag { get; init; }
}

/// <summary>`addWorkshop` 的完整参数（工作台的构造参数本身就是可序列化的数据）。</summary>
public sealed class WorkshopPayload
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("baseMarks")] public List<string> BaseMarks { get; init; } = new();
    [JsonPropertyName("additionalMarks")] public List<string> AdditionalMarks { get; init; } = new();
    [JsonPropertyName("ban")] public List<string> Ban { get; init; } = new();
    [JsonPropertyName("maxJars")] public int MaxJars { get; init; }
    [JsonPropertyName("maxLiquids")] public int MaxLiquids { get; init; }
}

/// <summary>操作名常量（**日志的键字符串**：改名即改存档格式，勿轻动）。</summary>
public static class WorldOps
{
    public const string AddWorkshop = "addWorkshop";
    public const string ClearWorkshops = "clearWorkshops";
    public const string CreateWorkshop = "createWorkshop";
    public const string CreateRandomWorkshop = "createRandomWorkshop";
    public const string UpdateWorkshopCapacity = "updateWorkshopCapacity";
    public const string SetMasterIndex = "setMasterIndex";
    public const string SetSlaveIndex = "setSlaveIndex";
    public const string SetMaster = "setMaster";
    public const string SetSlave = "setSlave";
    public const string AddOrder = "addOrder";
    public const string ClearOrders = "clearOrders";
    public const string CreateOrder = "createOrder";
    public const string CreateRandomOrder = "createRandomOrder";
    public const string DeleteOrder = "deleteOrder";
    public const string CheckFeasibility = "checkFeasibility";
    public const string CheckAllFeasibility = "checkAllFeasibility";
    public const string ExecuteOrder = "executeOrder";

    /// <summary>**异步轨**：把订单执行起成一个由 tick 驱动的过程（P2b 新增；同步轨仍是 <see cref="ExecuteOrder"/>）。</summary>
    public const string StartOrder = "startOrder";

    /// <summary>取消一个进行中的过程（P4 新增；已完成的部分不回滚）。</summary>
    public const string CancelProcess = "cancelProcess";
    public const string Tick = "tick";
    public const string UpdateProducerRate = "updateProducerRate";
    public const string UpdateLiquidProductionRate = "updateLiquidProductionRate";
    public const string SetLiquidProductionRate = "setLiquidProductionRate";
    public const string SetFlowSettings = "setFlowSettings";
    public const string SetAutoCheck = "setAutoCheck";
    public const string ResetProducerModule = "resetProducerModule";
    public const string Reset = "reset";
}

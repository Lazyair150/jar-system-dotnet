using System.Collections.Generic;
using System.Text.Json.Serialization;
using JarSystem.Core.Orders;

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
    [JsonPropertyName("feasibilityResult")] public FeasibilityStateDto? FeasibilityResult { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
}

/// <summary>可行性结果状态（10 个键；不含 checkMethod，与重构前一致）。</summary>
public sealed class FeasibilityStateDto
{
    [JsonPropertyName("feasible")] public bool Feasible { get; init; }
    [JsonPropertyName("blockage")] public string? Blockage { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("timestamp")] public string? Timestamp { get; init; }
    [JsonPropertyName("directCount")] public int DirectCount { get; init; }
    [JsonPropertyName("operationCount")] public int OperationCount { get; init; }
    [JsonPropertyName("solverPath")] public List<SolverStepDto>? SolverPath { get; init; }
    [JsonPropertyName("masterPath")] public List<SolverStepDto>? MasterPath { get; init; }
    [JsonPropertyName("transferJarIds")] public List<int>? TransferJarIds { get; init; }
    [JsonPropertyName("transferLiquids")] public Dictionary<string, int>? TransferLiquids { get; init; }
}

/// <summary>中央仓库状态（4 个键）。</summary>
public sealed class WarehouseStateDto
{
    [JsonPropertyName("jars")] public int Jars { get; init; }
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
// （契约快照 state_keys.txt 必须保持 165 行不变，这是本批的等价性证明）。
// ============================================================

/// <summary>产出模块状态（3 个键）。</summary>
public sealed class ProducerStatusDto
{
    [JsonPropertyName("producers")] public List<ProducerInfoDto> Producers { get; init; } = new();
    [JsonPropertyName("generators")] public List<GeneratorStatusDto> Generators { get; init; } = new();
    [JsonPropertyName("warehouse")] public ProducerWarehouseDto Warehouse { get; init; } = new();
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

using System.Text.Json.Serialization;

namespace JarSystem.Core.Process;

/// <summary>
/// 过程日志的一条记录（**追加写、永不就地修改**；存档格式见设计规格 §3.5，`journalVersion = 1`）。
///
/// 十个键由 <see cref="JsonPropertyNameAttribute"/> 钉死 —— 与契约 DTO 同一手法：
/// 改名不再是"静默改存档格式"，而是编译期就看得见的改动（配合 <see cref="JournalCodec"/> 的
/// 未知键拒绝策略，读旧档遇到新键会**报错而不是静默丢字段**）。
///
/// 粒度约定：**只记"改变世界的步"与里程碑** —— 否则日志会被"什么都没做"的检查淹没。
/// </summary>
public sealed class JournalEntry
{
    /// <summary>全局序号（从 1 开始，跨过程连续；客户端据此发现"我漏了消息"）。</summary>
    [JsonPropertyName("seq")] public int Seq { get; init; }

    /// <summary>本条目发生的逻辑 tick（P1 按世界修订号取值：瞬时模式下全部步骤同属一次宿主意图）。</summary>
    [JsonPropertyName("tick")] public int Tick { get; init; }

    /// <summary>所属过程 id。</summary>
    [JsonPropertyName("processId")] public int ProcessId { get; init; }

    /// <summary>条目种类（取值见 <see cref="JournalKinds"/>）。</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";

    /// <summary>步骤下标（仅 step_* 类条目有值）。</summary>
    [JsonPropertyName("stepIndex")] public int? StepIndex { get; init; }

    /// <summary>操作名（沿用既有执行日志词汇：pourIn / reshape / reshape_full / …）。</summary>
    [JsonPropertyName("operation")] public string? Operation { get; init; }

    /// <summary>相关的修饰器名（P3 起使用；P1 恒为 null）。</summary>
    [JsonPropertyName("modifier")] public string? Modifier { get; init; }

    /// <summary>原因/阻塞说明（世界语言，可直接显示给玩家）。</summary>
    [JsonPropertyName("reason")] public string? Reason { get; init; }

    /// <summary>
    /// 世界修订号（本次步进观察到的世界版本）。
    /// P1 的定义：**宿主世界意图的序号**（<c>JarSystem</c> 的命令日志序号）—— 同一次 <c>ExecuteOrder</c>
    /// 内的所有步骤共享同一个修订号（它们发生在同一次意图里）。P2 起每个 tick 会各产生新的修订号。
    /// </summary>
    [JsonPropertyName("worldRevision")] public int WorldRevision { get; init; }

    /// <summary>人类可读摘要（世界语言；面向日志阅读与排障，不参与任何判定）。</summary>
    [JsonPropertyName("summary")] public string? Summary { get; init; }
}

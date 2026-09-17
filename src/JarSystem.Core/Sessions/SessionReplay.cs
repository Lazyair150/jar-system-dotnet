using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Sessions;

/// <summary>
/// **会话回放**：把一条 <see cref="WorldCommand"/> 日志重放进一个**全新**的 <see cref="JarSystem"/>，
/// 复现出同样的世界状态。
///
/// 前置条件（宿主负责）：目标系统必须用**同样的世界配置 + 同样的种子 + 全新的 ID 源**构造：
/// <code>
/// var replay = new JarSystem(world: world, randomSeed: seed,
///                           jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());
/// SessionReplay.Into(replay, commands);
/// </code>
/// 回放走的是**与宿主完全相同的意图方法**（不绕后门），因此回放本身也会记录日志 ——
/// 于是"回放出的系统，其日志应当与原日志逐条相同"成为一条可断言的不变量。
/// </summary>
public static class SessionReplay
{
    /// <summary>把命令依次施加到目标系统上（返回实际执行的条数）。</summary>
    public static int Into(JarSystem target, IEnumerable<WorldCommand> commands)
    {
        // 回放期间：不刷新"本次意图的时间"，改用日志里记下的时间 ⇒ 事件历史的时间戳也能复现
        target.Replaying = true;
        int count = 0;
        foreach (var c in commands)
        {
            if (c.Time != null && DateTimeOffset.TryParse(c.Time, out var t)) target.Now = t;
            Apply(target, c);
            count++;
        }
        return count;
    }

    private static void Apply(JarSystem s, WorldCommand c)
    {
        switch (c.Op)
        {
            case WorldOps.AddWorkshop:
            {
                var w = c.Workshop ?? throw new ArgumentException("addWorkshop 缺 workshop 载荷");
                s.AddWorkshop(new Workshop(w.Name, w.BaseMarks, w.AdditionalMarks, w.Ban,
                    w.MaxJars, w.MaxLiquids, s.JarIds, s.Vocabulary));
                break;
            }
            case WorldOps.ClearWorkshops: s.ClearWorkshops(); break;
            case WorldOps.CreateWorkshop:
                s.CreateWorkshop(c.Name ?? "", c.BaseMarks ?? new(), c.AdditionalMarks, c.Ban);
                break;
            case WorldOps.CreateRandomWorkshop: s.CreateRandomWorkshop(); break;
            case WorldOps.UpdateWorkshopCapacity:
                s.UpdateWorkshopCapacity(c.IntA ?? 0, c.IntB, c.IntC);
                break;
            case WorldOps.SetMasterIndex: s.SetMasterIndex(c.IntA); break;
            case WorldOps.SetSlaveIndex: s.SetSlaveIndex(c.IntA); break;
            case WorldOps.SetMaster: s.SetMaster(c.IntA ?? 0); break;
            case WorldOps.SetSlave: s.SetSlave(c.IntA ?? 0); break;
            case WorldOps.AddOrder:
                s.AddOrder(new Order(c.Requirements ?? new(), c.OrderType ?? "A", s.OrderIds, s.Vocabulary));
                break;
            case WorldOps.ClearOrders: s.ClearOrders(); break;
            case WorldOps.CreateOrder: s.CreateOrder(c.Requirements ?? new(), c.OrderType ?? "A"); break;
            case WorldOps.CreateRandomOrder: s.CreateRandomOrder(); break;
            case WorldOps.DeleteOrder: s.DeleteOrder(c.IntA ?? 0); break;
            case WorldOps.CheckFeasibility: s.CheckFeasibility(c.IntA ?? 0); break;
            case WorldOps.CheckAllFeasibility: s.CheckAllFeasibility(); break;
            case WorldOps.ExecuteOrder: s.ExecuteOrder(c.IntA ?? 0); break;
            case WorldOps.Tick: s.Tick(c.IntA ?? 1); break;
            case WorldOps.UpdateProducerRate:
                s.UpdateProducerRate(c.Name ?? "", c.BaseMarks is { Count: > 0 } ? c.BaseMarks[0] : "", c.DoubleA ?? 0);
                break;
            case WorldOps.UpdateLiquidProductionRate:
                s.UpdateLiquidProductionRate(c.Ban is { Count: > 0 } ? c.Ban[0] : "", c.IntA ?? 0);
                break;
            case WorldOps.SetLiquidProductionRate:
                s.SetLiquidProductionRate(c.Ban is { Count: > 0 } ? c.Ban[0] : "", c.IntA ?? 0);
                break;
            case WorldOps.SetFlowSettings: s.SetFlowSettings(c.IntA ?? 0, c.IntB ?? 0); break;
            case WorldOps.SetAutoCheck: s.SetAutoCheck(c.Flag ?? false); break;
            case WorldOps.ResetProducerModule: s.ResetProducerModule(); break;
            case WorldOps.Reset: s.Reset(); break;
            default: throw new ArgumentException("未知的世界命令: " + c.Op);
        }
    }
}

/// <summary>命令日志的 JSON 读写（存档格式 v1）。</summary>
public static class WorldCommandJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        // 存档是**外部输入**（手改过的存档 / 旧版本的字段）：键名拼错必须报错，
        // 否则那条命令会带着"全是默认值"的载荷被重放 —— 静默重放出一个错的世界，
        // 比读档失败危险得多。与 WorldOptionsJson 同一条策略（见那里的注释）。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>导出为 JSON 文本（**存档格式**：字段名即 <see cref="JsonPropertyNameAttribute"/>）。</summary>
    public static string ToJson(IEnumerable<WorldCommand> commands) => JsonSerializer.Serialize(commands, Options);

    /// <summary>从 JSON 文本读回命令日志。</summary>
    public static List<WorldCommand> FromJson(string json) =>
        JsonSerializer.Deserialize<List<WorldCommand>>(json, Options) ?? new List<WorldCommand>();
}

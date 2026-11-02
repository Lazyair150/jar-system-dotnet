using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Process;

namespace JarSystem.Core.Services;

/// <summary>
/// 一条**世界不变量违规**。`Code` 是稳定标识（客户端/测试按它判断，不解析自然语言），
/// `Message` 给人看。
/// </summary>
public sealed class InvariantViolation
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string Severity { get; init; } = "error";

    public override string ToString() => "[" + Code + "] " + Message;
}

/// <summary>
/// **世界不变量检查器** —— 回答一个具体问题：*这个状态是否自相矛盾？*
///
/// 为什么需要它：世界里有**跨实体的引用**（过程 → 订单、过程 → 工作台、罐子 → 工作台）。
/// 这些引用一旦断开，程序**不会崩**，而是继续运行在一个说不通的状态上 ——
/// 例如"过程在跑，但它服务的订单已经不存在"、"过程在跑，但没有选中主台"。
/// 这类 bug 不会抛异常、不会让测试红，只会让玩法变得莫名其妙（这正是本轮实测撞到的：
/// 未选主台时 `startOrder` 仍起了过程，回包却带"未选择主工作台"）。
///
/// 三条纪律：
/// <list type="number">
/// <item>**只读**：不修改世界、不推进时间、不写日志 —— 因此可以在任何时刻调用，
/// 也可以在每帧之后调用（`REPLAY-OK` 的字节不受影响）。</item>
/// <item>**不抛异常**：违规是**数据**，不是异常。调用方决定怎么处理
/// （宿主记日志、页面显示、测试断言为空）。</item>
/// <item>**按"必须有"写**：每条检查都对应一句"某事件/某状态下，某个实体必须存在"，
/// 而不是"某字段看起来不对"。这样新增玩法时能一眼看出该补哪条。</item>
/// </list>
///
/// 刻意**不做**的事：不检查"罐子形状是否合法"这类**领域自己会拒绝**的事
/// （那是 `Workshop` 端口的职责，重复检查只会掩盖真正的端口漏洞）；
/// 这里只查**跨实体关系**与**容量账目**。
/// </summary>
public static class WorldInvariants
{
    /// <summary>当前世界的全部违规（空列表 = 自洽）。</summary>
    public static IReadOnlyList<InvariantViolation> Check(JarSystem system)
    {
        var v = new List<InvariantViolation>();
        var workshops = system.Workshops;
        int? master = system.CurrentMaster;
        int? slave = system.CurrentSlave;
        var active = system.Processes.ActiveProcesses;

        // ---------- 选择：主/从台的索引 ----------
        if (master is { } m && (m < 0 || m >= workshops.Count))
            Add(v, "MASTER-INDEX-OUT-OF-RANGE", "主台索引 " + m + " 越界（工作台数 " + workshops.Count + "）");
        if (slave is { } s && (s < 0 || s >= workshops.Count))
            Add(v, "SLAVE-INDEX-OUT-OF-RANGE", "从台索引 " + s + " 越界（工作台数 " + workshops.Count + "）");
        if (master is { } mm && slave is { } ss && mm == ss)
            Add(v, "SLAVE-EQUALS-MASTER", "从台与主台是同一个工作台（索引 " + mm + "）");

        // ---------- 过程 → 它服务的实体必须存在 ----------
        foreach (var p in active)
        {
            if (p.Kind == ProcessKinds.OrderExecution)
            {
                if (p.SubjectId is not { } orderId)
                {
                    Add(v, "PROCESS-ORDER-MISSING-ID",
                        "订单执行过程 #" + p.Id + " 没有 subjectId，无法确定它在服务哪张订单");
                    continue;
                }

                var order = system.Orders.FirstOrDefault(o => o.Id == orderId);
                if (order == null)
                    Add(v, "PROCESS-ORDER-MISSING",
                        "订单执行过程 #" + p.Id + " 指向订单 #" + orderId + "，但该订单已不在世界里的订单表中");
                if (master is null)
                    Add(v, "PROCESS-ORDER-WITHOUT-MASTER",
                        "订单执行过程 #" + p.Id + "（订单 #" + orderId + "）在跑，但没有选中主工作台");
            }
            else if (p.Kind == ProcessKinds.WorkshopMigration)
            {
                if (master is null)
                    Add(v, "PROCESS-MIGRATION-WITHOUT-MASTER",
                        "迁徙过程 #" + p.Id + " 在跑，但没有选中主工作台（它不知道该从哪搬）");
                if (p.SubjectId is { } target && (target < 0 || target >= workshops.Count))
                    Add(v, "PROCESS-MIGRATION-TARGET-MISSING",
                        "迁徙过程 #" + p.Id + " 的目标工作台索引 " + target + " 不存在（工作台数 " + workshops.Count + "）");
            }
        }

        // ---------- 同一主体不得有两个并行的同类过程 ----------
        foreach (var g in active.GroupBy(p => (p.Kind, p.SubjectId)))
            if (g.Count() > 1)
                Add(v, "PROCESS-DUPLICATE-FOR-SUBJECT",
                    "主体 " + g.Key.Kind + "/" + (g.Key.SubjectId?.ToString() ?? "null") + " 上有 " + g.Count() +
                    " 个并行过程（" + string.Join(", ", g.Select(p => "#" + p.Id)) + "）");

        // ---------- 订单状态 → 必须有过程 ----------
        foreach (var o in system.Orders)
        {
            if (o.Status == Constants.OrderStatus.EXECUTING &&
                !active.Any(p => p.Kind == ProcessKinds.OrderExecution && p.SubjectId == o.Id))
                Add(v, "ORDER-EXECUTING-WITHOUT-PROCESS",
                    "订单 #" + o.Id + " 处于「执行中」，但没有任何过程在推进它");

            if (o.Requirements.Count == 0 || o.Requirements.Count > Constants.MAX_ORDER_REQUIREMENTS)
                Add(v, "ORDER-REQUIREMENTS-COUNT",
                    "订单 #" + o.Id + " 的需求数 " + o.Requirements.Count + " 不在 1.." +
                    Constants.MAX_ORDER_REQUIREMENTS + " 之间");
        }

        // ---------- 工作台的资源账目 ----------
        for (var i = 0; i < workshops.Count; i++)
        {
            var w = workshops[i];
            var label = "工作台 " + i + "（" + w.Name + "）";

            if (w.Jars.Count > w.MaxJars)
                Add(v, "JAR-COUNT-OVER-CAPACITY", label + " 罐子 " + w.Jars.Count + " 只，超过罐位上限 " + w.MaxJars);

            var ids = w.Jars.Where(j => j.Id.HasValue).Select(j => j.Id!.Value).ToList();
            if (ids.Count != ids.Distinct().Count())
                Add(v, "JAR-DUPLICATE-ID", label + " 里存在重复的罐子 id");

            foreach (var kv in w.LiquidStorage)
            {
                if (kv.Value < 0)
                    Add(v, "LIQUID-NEGATIVE", label + " 的 " + kv.Key + " 存量是负数（" + kv.Value + "）");
                if (kv.Value > w.MaxLiquids)
                    Add(v, "LIQUID-OVER-CAPACITY",
                        label + " 的 " + kv.Key + " 存量 " + kv.Value + " 超过上限 " + w.MaxLiquids);
                if (kv.Value > 0 && w.IsBanned(kv.Key))
                    Add(v, "BANNED-LIQUID-STORED", label + " 存着被禁的液体 " + kv.Key + "（" + kv.Value + "）");
            }
        }

        // ---------- 罐子 id 全局唯一（转移把罐子搬台，绝不复制） ----------
        var allIds = workshops.SelectMany(w => w.Jars)
            .Where(j => j.Id.HasValue)
            .GroupBy(j => j.Id!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (allIds.Count > 0)
            Add(v, "JAR-ID-SHARED-ACROSS-WORKSHOPS",
                "罐子 id 在不同工作台里重复出现（" + string.Join(",", allIds.Take(5)) + "）：转移是搬运，不该复制");

        return v;
    }

    private static void Add(List<InvariantViolation> list, string code, string message) =>
        list.Add(new InvariantViolation { Code = code, Message = message });
}

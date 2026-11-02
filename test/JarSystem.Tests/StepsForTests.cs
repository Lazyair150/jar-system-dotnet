using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Process;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// **B2「步骤可观测」的门禁**：帧只带引用（`PresentationFrame.Subject` / `.SubjectId`），
/// 消费方必须能拿着这两个值把"这一步到底做了什么"查回来。
///
/// 这一层缺口的性质值得写下来：执行器早就把每一步记进了 `ProcessJournal`
/// （`JournalEntry` 十键含 `StepIndex` / `Operation` / `Reason`），
/// 但帧里的 `subjectId` 是**订单 id**、日志里的 `ProcessId` 是**过程 id**，
/// 两者之间此前没有任何公开映射 ⇒ 数据在、通路不在。
/// `ProcessSystem.TryGetProcessId` 补指针，`JarSystem.StepsFor` 补查询。
///
/// 断言一律钉**结构不变量**（"条目的 ProcessId 全是本过程"、"步骤下标单调不减"、
/// "步骤条目数 == 实例自报的已执行步数"、"计数恒等式"），不钉任何字符串内容
/// —— 摘要文案是给人读的，随时可以改。
/// </summary>
public class StepsForTests
{
    private static JarSystem.Core.JarSystem NewSystem() => new(
        randomSeed: 41,
        jarIds: new SequentialIdGenerator(),
        orderIds: new SequentialIdGenerator());

    /// <summary>
    /// 装配一个**确定性**世界：一台主工作台，台上按 `jarShapes` 各放一个罐子。
    /// 不靠产线送罐（那要 tick 几十下才能有东西倒进主台），让用例把注意力留在过程日志上。
    /// </summary>
    private static JarSystem.Core.JarSystem NewWorld(params string[] jarShapes)
    {
        var system = NewSystem();
        var master = new Workshop("M", new() { "圆罐", "方罐", "高罐" }, new(), new(), 20, 30);
        foreach (var shape in jarShapes)
            master.AddJar(new Jar(shape));

        system.ClearWorkshops();
        system.AddWorkshop(master);
        system.SetMasterIndex(0);
        return system;
    }

    private static List<Requirement> Reqs(params string[] shapes)
        => shapes.Select(s => new Requirement { Shape = s }).ToList();

    /// <summary>过程日志的紧凑视图，只用在失败消息里（不进任何断言）。</summary>
    private static string Dump(JarSystem.Core.JarSystem system)
        => string.Join(" | ", system.Journal.Entries.Select(e => $"{e.Kind}#{e.StepIndex}"));

    /// <summary>建世界（台上具备 `jarShapes` 各一个罐）、下一单、跑到终态，返回 (系统, 该单的过程实例)。</summary>
    private static (JarSystem.Core.JarSystem System, ProcessInstance Process) RunOneOrder(params string[] shapes)
    {
        var system = NewWorld(shapes);
        system.CreateOrder(Reqs(shapes), Constants.OrderType.A);

        var started = system.StartOrder(0);
        Assert.True(started.Started, started.Error);
        system.Tick(30);

        var process = system.Processes.RecentProcesses
            .Single(p => p.Kind == ProcessKinds.OrderExecution);
        Assert.True(process.IsTerminal, "过程应当已经结束： " + Dump(system));
        return (system, process);
    }

    // ---------------- ① 按 (subject, subjectId) 能定位到步骤 ----------------

    [Fact]
    public void StepsFor_LocatesTheStepsOfAFinishedSubject()
    {
        var (system, process) = RunOneOrder("圆罐");
        Assert.NotNull(process.SubjectId);

        var steps = system.StepsFor(process.Subject, process.SubjectId!.Value);

        // 结构不变量 0：查到的确实是执行过的步，而不是只有 plan_accepted/succeeded 这类里程碑。
        Assert.True(
            process.AppliedSteps > 0,
            $"这条用例要求过程确实执行过步骤（否则查到的只是里程碑）：{Dump(system)}");

        // 结构不变量 1：查到的条目**全部**属于这一个过程（不是别的过程渗进来）。
        Assert.NotEmpty(steps);
        Assert.All(steps, entry => Assert.Equal(process.Id, entry.ProcessId));

        // 结构不变量 2：过程结束后指针仍在（"跑完就查不到"与 B2 的目的相反）。
        Assert.True(system.Processes.TryGetProcessId(process.Subject, process.SubjectId.Value, out var viaIndex));
        Assert.Equal(process.Id, viaIndex);

        // 结构不变量 3：查到的步数与实例自报的已执行步数一致（两个视图不能各说各话）。
        Assert.Equal(process.AppliedSteps, steps.Count(entry => entry.StepIndex.HasValue));
    }

    // ---------------- ② 顺序 = StepIndex 升序（同 StepIndex 按 Seq） ----------------

    [Fact]
    public void StepsFor_OrdersStepsByStepIndexThenSeq()
    {
        // 至少要两步，才有"顺序"可验证；一步的过程对顺序是盲的。
        var (system, process) = RunOneOrder("圆罐", "方罐");
        var steps = system.StepsFor(process.Subject, process.SubjectId!.Value)
            .Where(entry => entry.StepIndex.HasValue)
            .ToList();

        Assert.True(steps.Count >= 2, $"需要至少两步才能验证顺序，实际 {steps.Count} 步：{Dump(system)}");

        for (int i = 1; i < steps.Count; i++)
        {
            int previous = steps[i - 1].StepIndex!.Value;
            int current = steps[i].StepIndex!.Value;
            // 升序；同一步下标（同一逻辑步的多条条目）退化为按序号升序。
            Assert.True(
                previous < current || (previous == current && steps[i - 1].Seq < steps[i].Seq),
                $"第 {i} 条的顺序违反 (stepIndex, seq) 升序：({previous},{steps[i - 1].Seq}) → ({current},{steps[i].Seq})");
        }
    }

    // ---------------- ③ 活动中的过程也能查到 ----------------

    [Fact]
    public void StepsFor_AlsoCoversAProcessThatIsStillRunning()
    {
        var system = NewWorld("圆罐", "方罐");
        system.CreateOrder(Reqs("圆罐", "方罐"), Constants.OrderType.A);

        // **前提必须钉住**：先做可行性分析，让会话拿到求解器路径（2 条 MATCH = 2 个单位）。
        // 没有这一步的话会话会走 `MasterGreedy`（一个单位就把整单跑完）——
        // 于是 `Tick(2)` 之后根本没有"进行中"可看，用例会以一条无关的
        // `Sequence contains no matching element` 失败（本会话实测踩到）。
        var feas = system.CheckFeasibility(0);
        Assert.NotNull(feas);
        Assert.True(feas!.Feasible, "用例前提：这一单必须可行，否则过程走贪心、一开始就结束");

        var started = system.StartOrder(0);
        Assert.True(started.Started, started.Error);

        system.Tick(2);   // 求解器路径逐单位推进 ⇒ tick 1 落一步，tick 2 未到步间隔 ⇒ 过程仍在跑

        var active = system.Processes.ActiveProcesses
            .Single(p => p.Kind == ProcessKinds.OrderExecution);
        Assert.False(active.IsTerminal, $"这条用例要的正是「进行中」，用前先证明它确实还在跑：{Dump(system)}");
        Assert.NotNull(active.SubjectId);
        Assert.True(active.AppliedSteps > 0, $"推进一刻后至少该有一步落账：{Dump(system)}");

        var steps = system.StepsFor(active.Subject, active.SubjectId!.Value);

        // 活动中的过程同样可查 —— 这一条是 B2 明确要求覆盖的（不只已结束）。
        Assert.NotEmpty(steps);
        Assert.Equal(active.AppliedSteps, steps.Count(entry => entry.StepIndex.HasValue));
    }

    // ---------------- ④ 映射有界，且"丢了要能看出来" ----------------

    [Fact]
    public void SubjectIndex_IsBoundedAndDroppedMappingsAreVisible()
    {
        var processes = new ProcessSystem(() => 0);
        const int capacity = ProcessSystem.SubjectIndexCapacity;
        int total = capacity + 4;   // 越过容量一批

        for (int i = 0; i < total; i++)
            processes.Begin(new ProcessDefinition(ProcessKinds.OrderExecution, "委托 #" + i, i));

        // ① 有界：条目数不超过容量（长跑世界不会把这张表撑爆）。
        Assert.True(
            processes.IndexedSubjectCount <= capacity,
            $"索引 {processes.IndexedSubjectCount} 条，超过容量 {capacity}");

        // ② 丢了要能看出来：既要有非零丢弃计数，也要满足
        //    `IndexedSubjectCount + DroppedSubjectMappings == IndexedSubjectKeys`
        //    —— 与 ProcessJournal 的 `FirstSeq == DroppedEntries + 1` 同一手法，
        //    消费方据此能判断"这份指针表是完整的、缺口正好在它前面"。
        Assert.True(processes.DroppedSubjectMappings > 0, "越过容量后应当有丢弃计数");
        Assert.Equal(
            processes.IndexedSubjectCount + processes.DroppedSubjectMappings,
            processes.IndexedSubjectKeys);

        // ③ 淘汰的是**最旧**的，最新的仍然可查（FIFO，不是随机/整表清空）。
        Assert.False(processes.TryGetProcessId("委托 #0", 0, out _), "最旧的主体应当已被淘汰");
        Assert.True(
            processes.TryGetProcessId("委托 #" + (total - 1), total - 1, out var newestId),
            "最新的主体必须仍然可查");
        Assert.Equal(total, newestId);   // 过程 id 从 1 起，第 total 个即 total
    }
}

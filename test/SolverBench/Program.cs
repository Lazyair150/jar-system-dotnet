using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Solver;
using JarSystem.Core.Abstractions;

namespace SolverBench;

/// <summary>
/// 求解器升级基准：**同一批场景**分别喂给裸 <see cref="StateSpaceSolver"/> 与
/// <see cref="PrecheckedStateSpaceSolver"/>，比对"结论是否一致"与"耗时/节点/未判定率"。
///
/// 判定一致性是本基准的**正确性门槛**：只要出现一次"旧说可行、新说不可行"，
/// 就说明前置检查错杀了，直接判定基准失败（退出码 1）。
/// </summary>
internal static class Program
{
    private const int NodeBudget = 400_000;

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var perKind = new[] { 400, 400, 60, 40, 400 };  // random / capacity / adversarial / exploding / 四签名扫描
        var rnd = new Random(20261101);

        long oldMs = 0, newMs = 0, oldTicks = 0, newTicks = 0;
        int compared = 0, agree = 0, oldUndetermined = 0, newUndetermined = 0;
        int oldSolvedByPrecheck = 0;                   // 旧"未判定"→ 新给出确定结论
        int verdictMismatch = 0, unsound = 0;
        long nodesOld = 0, nodesNew = 0;
        long matchMs = 0, matchTicks = 0;
        var kindDirect = new int[5];
        var kindFallback = new int[5];
        var kindOldNodes = new long[5];
        var kindMatchMs = new long[5];
        var kindOldMs = new long[5];
        int directHits = 0, fallbacks = 0, matchUndetermined = 0, unsoundMatch = 0, matchMismatch = 0;

        for (int kind = 0; kind < perKind.Length; kind++)
        {
            string kindName = kind switch { 0 => "random", 1 => "capacity", 2 => "adversarial", 3 => "exploding", _ => "banSweep" };
            for (int i = 0; i < perKind[kind]; i++)
            {
                var (reqs, wi, jars, liquids) = Make(rnd, kind, i);

                // ---- 旧：裸精确求解器 ----
                var a = new StateSpaceSolver();
                a.BeginCheck(NodeBudget);
                var sw = Stopwatch.StartNew();
                var ra = Timed(() => a.Solve(Order(reqs), wi, jars, liquids), sw);
                oldMs += sw.ElapsedMilliseconds; oldTicks += sw.ElapsedTicks;
                nodesOld += a.Stats.NodesExplored;
                kindOldNodes[kind] += a.Stats.NodesExplored;
                kindOldMs[kind] += sw.ElapsedMilliseconds;

                // ---- 新：前置检查 + 精确求解器 ----
                var b = new PrecheckedStateSpaceSolver();
                b.BeginCheck(NodeBudget);
                var sw2 = Stopwatch.StartNew();
                var rb = Timed(() => b.Solve(Order(reqs), wi, jars, liquids), sw2);
                newMs += sw2.ElapsedMilliseconds; newTicks += sw2.ElapsedTicks;
                nodesNew += b.Stats.NodesExplored;

                // ---- 三：匹配形求解器（分配优先，构造不出就回退到"前置检查 + 精确") ----
                var c = new MatchingSolver();
                c.BeginCheck(NodeBudget);
                var sw3 = Stopwatch.StartNew();
                var rc = Timed(() => c.Solve(Order(reqs), wi, jars, liquids), sw3);
                matchMs += sw3.ElapsedMilliseconds; matchTicks += sw3.ElapsedTicks;
                directHits += c.DirectHits;
                fallbacks += c.Fallbacks;
                kindDirect[kind] += c.DirectHits;
                kindFallback[kind] += c.Fallbacks;
                kindMatchMs[kind] += sw3.ElapsedMilliseconds;

                compared++;
                if (ra.BudgetExceeded) oldUndetermined++;
                if (rb.BudgetExceeded) newUndetermined++;
                if (rc.BudgetExceeded) matchUndetermined++;

                // 健全性铁律：旧说"可行"时新**绝不**能说"不可行"
                if (ra.Feasible && !rb.Feasible && !rb.BudgetExceeded)
                {
                    unsound++;
                    Console.WriteLine($"UNSOUND [{kindName} #{i}] 旧=可行 新=不可行 原因={rb.Blockage}");
                    Console.WriteLine("  需求=" + string.Join(",", reqs.Select(Describe)));
                    continue;
                }

                if (ra.BudgetExceeded && !rb.BudgetExceeded) oldSolvedByPrecheck++;

                if (ra.Feasible && !rc.Feasible && !rc.BudgetExceeded)
                {
                    unsoundMatch++;
                    Console.WriteLine($"UNSOUND-MATCH [{kindName} #{i}] 旧=可行 匹配形=不可行 原因={rc.Blockage}");
                    continue;
                }

                if (!ra.BudgetExceeded && !rc.BudgetExceeded && ra.Feasible != rc.Feasible) matchMismatch++;

                if (ra.BudgetExceeded || rb.BudgetExceeded) continue;
                if (ra.Feasible == rb.Feasible) agree++;
                else
                {
                    verdictMismatch++;
                    Console.WriteLine($"MISMATCH [{kindName} #{i}] 旧={ra.Feasible} 新={rb.Feasible}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("========== 求解器升级基准 ==========");
        Console.WriteLine($"场景数                : {compared}");
        Console.WriteLine($"结论可比较数          : {agree}（一致 {agree}，不一致 {verdictMismatch}）");
        Console.WriteLine($"旧·未判定             : {oldUndetermined}");
        Console.WriteLine($"新·未判定             : {newUndetermined}");
        Console.WriteLine($"前置检查救回的未判定  : {oldSolvedByPrecheck}");
        Console.WriteLine($"旧·总节点             : {nodesOld:N0}");
        Console.WriteLine($"新·总节点             : {nodesNew:N0}");
        Console.WriteLine($"旧·总耗时             : {oldMs:N0} ms");
        Console.WriteLine($"新·总耗时             : {newMs:N0} ms");
        if (newTicks > 0)
            Console.WriteLine($"加速比（总耗时）      : {(double)oldTicks / newTicks:F2}x");
        Console.WriteLine($"健全性违反            : {unsound}");
        Console.WriteLine();
        Console.WriteLine("---------- 按场景类拆分 ----------");
        Console.WriteLine("类别          场景  匹配形直接  回退   旧节点     旧耗时   匹配形耗时");
        for (int k = 0; k < 5; k++)
        {
            string kn = k switch { 0 => "random", 1 => "capacity", 2 => "adversarial", 3 => "exploding", _ => "banSweep" };
            Console.WriteLine($"{kn,-12}  {perKind[k],5}  {kindDirect[k],9}  {kindFallback[k],5}  {kindOldNodes[k],9:N0}  {kindOldMs[k],6:N0}ms  {kindMatchMs[k],6:N0}ms");
        }
        Console.WriteLine();
        Console.WriteLine("---------- 匹配形求解器（分配优先） ----------");
        Console.WriteLine($"直接解出（不回退）    : {directHits}");
        Console.WriteLine($"回退到精确            : {fallbacks}");
        Console.WriteLine($"匹配形·未判定         : {matchUndetermined}");
        Console.WriteLine($"匹配形·结论不一致     : {matchMismatch}");
        Console.WriteLine($"匹配形·健全性违反     : {unsoundMatch}");
        Console.WriteLine($"匹配形·总耗时         : {matchMs:N0} ms");
        if (matchTicks > 0)
            Console.WriteLine($"相对旧的加速比        : {(double)oldTicks / matchTicks:F2}x");
        Console.WriteLine();
        bool ok = unsound == 0 && verdictMismatch == 0 && unsoundMatch == 0 && matchMismatch == 0;
        Console.WriteLine(ok ? "BENCH-OK" : "BENCH-FAILED");
        return ok ? 0 : 1;
    }

    private static SolverResult Timed(Func<SolverResult> work, Stopwatch sw)
    {
        sw.Restart();
        var r = work();
        sw.Stop();
        return r;
    }

    private static List<Requirement> Order(List<Requirement> reqs) => RequirementOrdering.FullFirst(reqs);

    private static (List<Requirement> Reqs, WorkshopInfo Wi, List<JarState> Jars, Dictionary<string, int> Liquids)
        Make(Random rnd, int kind, int index)
    {
        var shapes = Constants.SHAPES.ToList();
        var liquids = Constants.LIQUIDS.ToList();

        // 工作台：基础标记 1~2 个，附加标记 1 个，可能禁一种液体（A 类：无主台来源罐）
        var baseMarks = shapes.OrderBy(_ => rnd.Next()).Take(kind >= 2 ? 2 : rnd.Next(1, 3)).ToList();
        var addMarks = shapes.OrderBy(_ => rnd.Next()).Where(s => !baseMarks.Contains(s)).Take(1).ToList();
        // kind==4：**穷举四种禁令签名**（0/1 图上的四格），确保每一种都被算法覆盖到。
        // 当前世界的 4 个工作台只是样本（3 个禁酒、1 个都不禁），未来可能出现任意组合，
        // 所以这里不靠随机撞，而是按 index % 4 强制轮流走四格。
        List<string> ban;
        if (kind == 4)
        {
            var mask = index % 4;                       // bit0="酒"被禁, bit1="蜜"被禁
            ban = new List<string>();
            if ((mask & 1) == 1) ban.Add(liquids[0]);
            if ((mask & 2) == 2) ban.Add(liquids[1]);
        }
        else
        {
            ban = rnd.Next(3) == 0 ? new List<string> { liquids[rnd.Next(liquids.Count)] } : new List<string>();
        }

        var wi = new WorkshopInfo
        {
            Capability = new WorkshopCapability(baseMarks, addMarks, ban),
            MaxJars = 20,
            MaxLiquids = kind == 1 ? rnd.Next(2, 5) : 30
        };

        int jarCount = kind switch { 0 => rnd.Next(6, 15), 1 => rnd.Next(12, 21), 2 => rnd.Next(16, 21), 3 => rnd.Next(18, 21), _ => rnd.Next(6, 15) };
        var jars = new List<JarState>();
        for (int i = 0; i < jarCount; i++)
        {
            // 随机场景偏向"能被本台做出来的形状"，对抗场景故意掺入不可达形状
            var shape = kind == 2 && rnd.Next(4) == 0
                ? shapes[rnd.Next(shapes.Count)]
                : (rnd.Next(3) == 0 ? addMarks[0] : baseMarks[rnd.Next(baseMarks.Count)]);
            string? liquid = rnd.Next(2) == 0 ? null : liquids[rnd.Next(liquids.Count)];
            jars.Add(new JarState { Shape = shape, Liquid = liquid, IsMarked = false });
        }

        int reqCount = kind switch { 0 => rnd.Next(2, 7), 1 => rnd.Next(4, 9), 2 => rnd.Next(8, 11), 3 => rnd.Next(8, 11), _ => rnd.Next(2, 8) };
        var reqs = new List<Requirement>();
        for (int i = 0; i < reqCount; i++)
        {
            var shape = kind == 2 && rnd.Next(3) == 0
                ? shapes[rnd.Next(shapes.Count)]
                : (rnd.Next(3) == 0 ? addMarks[0] : baseMarks[rnd.Next(baseMarks.Count)]);
            string? liquid = kind == 3
                ? liquids[1]                                   // 全部要「蜜」
                : (rnd.Next(2) == 0 ? null : liquids[rnd.Next(liquids.Count)]);
            reqs.Add(new Requirement { Shape = shape, Liquid = liquid });
        }

        var pool = new Dictionary<string, int>();
        foreach (var l in liquids)
            pool[l] = kind switch { 1 => rnd.Next(0, 2), 3 => 0, _ => rnd.Next(0, 6) };

        return (reqs, wi, jars, pool);
    }

    private static string Describe(Requirement r) =>
        r.Liquid == null ? $"空({r.Shape})" : $"满({r.Shape},{r.Liquid})";
}

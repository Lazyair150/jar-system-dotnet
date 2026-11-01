import io, re, sys

p = r"D:\codex\jar-system-dotnet\test\SolverBench\Program.cs"
s = io.open(p, encoding="utf-8").read()

# 1) 计数器
old = "        long nodesOld = 0, nodesNew = 0;"
new = """        long nodesOld = 0, nodesNew = 0;
        long matchMs = 0, matchTicks = 0;
        int directHits = 0, fallbacks = 0, matchUndetermined = 0, unsoundMatch = 0, matchMismatch = 0;"""
assert old in s
s = s.replace(old, new, 1)

# 2) 第三个实现
anchor = """                compared++;
                if (ra.BudgetExceeded) oldUndetermined++;"""
assert anchor in s
block = """                // ---- 三：匹配形求解器（分配优先，构造不出就回退到"前置检查 + 精确") ----
                var c = new MatchingSolver();
                c.BeginCheck(NodeBudget);
                var sw3 = Stopwatch.StartNew();
                var rc = Timed(() => c.Solve(Order(reqs), wi, jars, liquids), sw3);
                matchMs += sw3.ElapsedMilliseconds; matchTicks += sw3.ElapsedTicks;
                directHits += c.DirectHits;
                fallbacks += c.Fallbacks;

                compared++;
                if (ra.BudgetExceeded) oldUndetermined++;"""
s = s.replace(anchor, block, 1)

# 3) 未判定与健全性
old2 = "                if (rb.BudgetExceeded) newUndetermined++;"
new2 = old2 + """
                if (rc.BudgetExceeded) matchUndetermined++;"""
assert old2 in s
s = s.replace(old2, new2, 1)

old3 = """                if (ra.BudgetExceeded || rb.BudgetExceeded) continue;"""
new3 = """                if (ra.Feasible && !rc.Feasible && !rc.BudgetExceeded)
                {
                    unsoundMatch++;
                    Console.WriteLine($"UNSOUND-MATCH [{kindName} #{i}] 旧=可行 匹配形=不可行 原因={rc.Blockage}");
                    continue;
                }

                if (!ra.BudgetExceeded && !rc.BudgetExceeded && ra.Feasible != rc.Feasible) matchMismatch++;

                if (ra.BudgetExceeded || rb.BudgetExceeded) continue;"""
assert old3 in s
s = s.replace(old3, new3, 1)

# 4) 报告
old4 = '''        Console.WriteLine($"健全性违反            : {unsound}");
        Console.WriteLine();
        Console.WriteLine(unsound == 0 && verdictMismatch == 0 ? "BENCH-OK" : "BENCH-FAILED");
        return unsound == 0 && verdictMismatch == 0 ? 0 : 1;'''
new4 = '''        Console.WriteLine($"健全性违反            : {unsound}");
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
        return ok ? 0 : 1;'''
assert old4 in s
s = s.replace(old4, new4, 1)

io.open(p, "w", encoding="utf-8").write(s)
print("patched OK")

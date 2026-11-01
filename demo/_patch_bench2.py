import io

p = r"D:\codex\jar-system-dotnet\test\SolverBench\Program.cs"
s = io.open(p, encoding="utf-8").read()

old = "        long matchMs = 0, matchTicks = 0;"
new = """        long matchMs = 0, matchTicks = 0;
        var kindDirect = new int[4];
        var kindFallback = new int[4];
        var kindOldNodes = new long[4];
        var kindMatchMs = new long[4];
        var kindOldMs = new long[4];"""
assert old in s
s = s.replace(old, new, 1)

old = """                nodesOld += a.Stats.NodesExplored;"""
new = """                nodesOld += a.Stats.NodesExplored;
                kindOldNodes[kind] += a.Stats.NodesExplored;
                kindOldMs[kind] += sw.ElapsedMilliseconds;"""
assert old in s
s = s.replace(old, new, 1)

old = """                directHits += c.DirectHits;
                fallbacks += c.Fallbacks;"""
new = """                directHits += c.DirectHits;
                fallbacks += c.Fallbacks;
                kindDirect[kind] += c.DirectHits;
                kindFallback[kind] += c.Fallbacks;
                kindMatchMs[kind] += sw3.ElapsedMilliseconds;"""
assert old in s
s = s.replace(old, new, 1)

old = '''        Console.WriteLine("---------- 匹配形求解器（分配优先） ----------");'''
new = '''        Console.WriteLine("---------- 按场景类拆分 ----------");
        Console.WriteLine("类别          场景  匹配形直接  回退   旧节点     旧耗时   匹配形耗时");
        for (int k = 0; k < 4; k++)
        {
            string kn = k switch { 0 => "random", 1 => "capacity", 2 => "adversarial", _ => "exploding" };
            Console.WriteLine($"{kn,-12}  {perKind[k],5}  {kindDirect[k],9}  {kindFallback[k],5}  {kindOldNodes[k],9:N0}  {kindOldMs[k],6:N0}ms  {kindMatchMs[k],6:N0}ms");
        }
        Console.WriteLine();
        Console.WriteLine("---------- 匹配形求解器（分配优先） ----------");'''
assert old in s
s = s.replace(old, new, 1)

io.open(p, "w", encoding="utf-8").write(s)
print("patched OK")

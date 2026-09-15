using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.Solver;

public class SolverState
{
    public List<JarState> Jars { get; set; }
    public Dictionary<string, int> Liquids { get; set; }
    public WorkshopInfo WorkshopInfo { get; set; }
    // 选择性转移预算：从台阶段最多可「使用」的主台来源罐子数（未使用的主台罐子留在主台）
    public int TransferBudget { get; set; } = int.MaxValue;
    public int TransferUsed { get; set; }

    public SolverState(List<JarState> jars, Dictionary<string, int> liquids, WorkshopInfo workshopInfo)
    {
        Jars = jars.Select(j => JarState.Copy(j)).ToList();
        Liquids = new Dictionary<string, int>(liquids);
        WorkshopInfo = workshopInfo;
    }

    public SolverState Clone() => new SolverState(Jars, Liquids, WorkshopInfo)
    {
        TransferBudget = TransferBudget,
        TransferUsed = TransferUsed
    };
    public int FindJarIndex(Func<JarState, bool> pred) => Jars.FindIndex(j => pred(j));

    // ---- 液体账目 ----
    public int LiquidCount(string l) => Liquids.GetValueOrDefault(l, 0);
    public bool HasLiquid(string l, int a = 1) => LiquidCount(l) >= a;
    public bool ConsumeLiquid(string l, int a = 1) { if (!HasLiquid(l, a)) return false; Liquids[l] -= a; return true; }
    public void AddLiquid(string l, int a = 1) => Liquids[l] = Liquids.GetValueOrDefault(l, 0) + a;

    // ---- 罐子状态（求解器自有的快照态） ----
    public void MarkJar(int i) => Jars[i].IsMarked = true;
    public void SetShape(int i, string s) => Jars[i].Shape = s;
    public void SetLiquid(int i, string l) => Jars[i].Liquid = l;
    public string? TakeLiquid(int i) { var l = Jars[i].Liquid; Jars[i].Liquid = null; return l; }
    public bool IsEmpty(JarState j) => j.Liquid is null;

    // ---- 规则查询 ----
    public bool IsBanned(string l) => WorkshopInfo.Capability.IsBanned(l);
    public bool CanTransform(string f, string t) => WorkshopInfo.Capability.CanTransform(f, t);

    /// <summary>可用成本液体（未被禁 + 有库存 + 不在排除列表）。</summary>
    public List<string> AvailableCostLiquids(List<string>? exclude = null)
    {
        exclude ??= new();
        return Constants.LIQUIDS.Where(l => !IsBanned(l) && HasLiquid(l, 1) && !exclude.Contains(l)).ToList();
    }
}

/// <summary>
/// 求解器侧的工作台规则信息。
///
/// S1：能力参数（标记/禁令）改由 <see cref="Domain.WorkshopCapability"/> 承载 ——
/// 与域层 <c>Workshop</c>、快照 <c>WorkshopSnapshot</c> **共用同一份不可变对象**，
/// 不再各存一份标记表；因此"求解器的规则视图"与"领域层的规则视图"不可能漂移。
/// </summary>
public class WorkshopInfo
{
    /// <summary>能力参数（与域层同源）。</summary>
    public WorkshopCapability Capability { get; set; } = new();

    public int MaxJars { get; set; } = 20;
    public int MaxLiquids { get; set; } = 30;

    public List<string> GetPossibleTransforms(string shape) => Capability.GetPossibleTransforms(shape);
}

public class SolverStats
{
    public int NodesExplored { get; set; }
    public int SolutionsFound { get; set; }
    public int DeadEnds { get; set; }
    public int PourOuts { get; set; }
}

public class SolverResult
{
    public bool Feasible { get; set; }
    public List<SolverStep>? Path { get; set; }
    public string? Blockage { get; set; }
    public SolverStats? Stats { get; set; }
}

/// <summary>
/// StateSpaceSolver v6 — 状态空间搜索求解器（实现 <see cref="ISolver"/>）
/// 合法操作: MATCH(预定), POUR_IN(倒液), POUR_OUT(倒空), RESHAPE(改形)
/// MATCH: 不标记罐子
/// RESHAPE: 适用于空罐(用工作台液体)和满罐(用自身液体)
/// </summary>
public class StateSpaceSolver : ISolver
{
    public SolverStats Stats { get; private set; } = new();
    public List<SolverStep> BestPartialPath { get; private set; } = new();

    public SolverResult Solve(List<Requirement> requirements, WorkshopInfo workshopInfo,
        List<JarState> jars, Dictionary<string, int> liquids, int transferBudget = int.MaxValue)
    {
        Stats = new SolverStats();
        BestPartialPath = new();
        var st = new SolverState(jars, liquids, workshopInfo) { TransferBudget = transferBudget };
        var path = new List<SolverStep>();
        var r = Bt(requirements, 0, st, path);
        return r != null
            ? new SolverResult { Feasible = true, Path = r, Stats = Stats }
            : new SolverResult { Feasible = false, Path = BestPartialPath, Blockage = Block(requirements, st), Stats = Stats };
    }

    private List<SolverStep>? Bt(List<Requirement> reqs, int idx, SolverState st, List<SolverStep> path)
    {
        if (path.Count > BestPartialPath.Count)
            BestPartialPath = new List<SolverStep>(path);
        if (idx >= reqs.Count) { Stats.SolutionsFound++; return new List<SolverStep>(path); }

        var ops = GenOps(reqs[idx], st);
        foreach (var op in ops)
        {
            Stats.NodesExplored++;
            var ns = st.Clone();
            if (!Apply(op, ns)) continue;
            var step = new SolverStep
            {
                Step = idx, Requirement = reqs[idx], Operation = op,
                Before = st.Jars.Select(j => JarState.Copy(j)).ToList(),
                After = ns.Jars.Select(j => JarState.Copy(j)).ToList(),
                AfterLiquids = new Dictionary<string, int>(ns.Liquids)
            };
            path.Add(step);
            int ni = idx + 1;
            if (op.Type == "POUR_OUT")
            {
                var jBefore = st.Jars[op.JarIndex!.Value];
                // Only advance if POUR_OUT satisfies an EMPTY requirement:
                // the jar becomes empty of the matching shape.
                bool shapeMatch = jBefore.Shape == reqs[idx].Shape;
                bool isEmptyReq = reqs[idx].Liquid == null;
                ni = (shapeMatch && isEmptyReq) ? idx + 1 : idx;
            }
            var r = Bt(reqs, ni, ns, path);
            if (r != null) return r;
            path.RemoveAt(path.Count - 1);
        }
        Stats.DeadEnds++;
        return null;
    }

    private List<SolverOperation> GenOps(Requirement req, SolverState st)
    {
        var ops = new List<SolverOperation>();
        if (req.Liquid != null) FullOps(req.Shape, req.Liquid, st, ops);
        else EmptyOps(req.Shape, st, ops);
        for (int i = 0; i < st.Jars.Count; i++)
        {
            var j = st.Jars[i];
            if (j.Liquid != null && !j.IsMarked && !st.IsBanned(j.Liquid))
                if (st.LiquidCount(j.Liquid) < st.WorkshopInfo.MaxLiquids)
                    ops.Add(new SolverOperation { Type = "POUR_OUT", Description = "倒出 " + j.Shape + " 中的 " + j.Liquid, JarIndex = i, Consumed = new() });
        }
        return ops;
    }

    private void FullOps(string shape, string liquid, SolverState st, List<SolverOperation> ops)
    {
        var di = st.FindJarIndex(j => j.Shape == shape && j.Liquid == liquid && !j.IsMarked);
        if (di != -1) ops.Add(new SolverOperation { Type = "MATCH", Description = "直接使用 " + shape + "/" + liquid, JarIndex = di, Consumed = new() });
        if (!st.IsBanned(liquid) && st.WorkshopInfo.Capability.IsBaseMarked(shape))
        {
            var pi = st.FindJarIndex(j => j.Shape == shape && st.IsEmpty(j) && !j.IsMarked);
            if (pi != -1) ops.Add(new SolverOperation { Type = "POUR_IN", Description = "倒入 " + liquid + " 到 " + shape + "空罐", JarIndex = pi, TargetLiquid = liquid, Consumed = new() { [liquid] = 1 } });
        }
    }

    private void EmptyOps(string shape, SolverState st, List<SolverOperation> ops)
    {
        var di = st.FindJarIndex(j => j.Shape == shape && st.IsEmpty(j) && !j.IsMarked);
        if (di != -1) ops.Add(new SolverOperation { Type = "MATCH", Description = "直接使用 " + shape + "空罐", JarIndex = di, Consumed = new() });
        foreach (var cl in st.AvailableCostLiquids())
            for (int i = 0; i < st.Jars.Count; i++)
                if (st.IsEmpty(st.Jars[i]) && !st.Jars[i].IsMarked && st.CanTransform(st.Jars[i].Shape, shape))
                    ops.Add(new SolverOperation { Type = "RESHAPE", Description = "改形 " + st.Jars[i].Shape + "->" + shape, JarIndex = i, TargetShape = shape, CostLiquid = cl, Consumed = new() { [cl] = 1 } });
        for (int i = 0; i < st.Jars.Count; i++)
        {
            var j = st.Jars[i];
            if (j.Liquid != null && !j.IsMarked && !st.IsBanned(j.Liquid) && st.CanTransform(j.Shape, shape))
                ops.Add(new SolverOperation { Type = "RESHAPE", Description = "满罐改形 " + j.Shape + "->" + shape + " 自耗" + j.Liquid, JarIndex = i, TargetShape = shape, CostLiquid = j.Liquid, Consumed = new(), FullReshape = true });
        }
    }

    private bool Apply(SolverOperation op, SolverState st)
    {
        int i = op.JarIndex!.Value;
        if (i < 0 || i >= st.Jars.Count) return false;
        if (st.Jars[i].IsMarked) return false;
        // 选择性转移预算：从台阶段使用主台来源罐子会消耗预算，超限则不可用
        if (st.Jars[i].MasterOrigin && st.TransferUsed >= st.TransferBudget) return false;
        foreach (var kv in op.Consumed ?? new())
            if (!st.HasLiquid(kv.Key, kv.Value)) return false;
        switch (op.Type)
        {
            case "MATCH": st.MarkJar(i); break;
            case "POUR_IN":
                if (!st.IsEmpty(st.Jars[i])) return false;
                if (op.TargetLiquid == null || !st.ConsumeLiquid(op.TargetLiquid, 1)) return false;
                st.SetLiquid(i, op.TargetLiquid); st.MarkJar(i); break;
            case "RESHAPE":
                if (st.IsEmpty(st.Jars[i])) { if (op.CostLiquid == null || !st.ConsumeLiquid(op.CostLiquid, 1)) return false; }
                else { if (st.IsBanned(st.Jars[i].Liquid!)) return false; st.TakeLiquid(i); }
                st.SetShape(i, op.TargetShape!); st.MarkJar(i); break;
            case "POUR_OUT":
                if (st.IsEmpty(st.Jars[i])) return false;
                var lq = st.TakeLiquid(i); st.MarkJar(i); st.AddLiquid(lq!, 1); Stats.PourOuts++; break;
            default: return false;
        }
        if (st.Jars[i].MasterOrigin) st.TransferUsed++;
        return true;
    }

    private string Block(List<Requirement> reqs, SolverState st)
    {
        for (int i = 0; i < reqs.Count; i++)
        {
            var ops = GenOps(reqs[i], st);
            if (ops.All(o => o.Type == "POUR_OUT"))
            {
                var d = reqs[i].Liquid != null ? "满罐(" + reqs[i].Shape + "," + reqs[i].Liquid + ")" : "空罐(" + reqs[i].Shape + ")";
                var liq = reqs[i].Liquid;
                if (liq != null && st.IsBanned(liq)) return "需求 " + d + " 液体" + liq + "被禁止";
                return "需求 " + d + " 无任何可行路径";
            }
        }
        return "资源冲突或液体不足";
    }
}

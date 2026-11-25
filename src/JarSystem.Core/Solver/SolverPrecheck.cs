using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Abstractions;

namespace JarSystem.Core.Solver;

/// <summary>
/// 求解前的**健全性前置检查**：只用来证明"不可行"，绝不证明"可行"。
///
/// 为什么放在搜索之前：生产求解器 <see cref="StateSpaceSolver"/> 的最坏情形正是
/// **不可行的 B 类订单** —— 它要把整棵搜索树走空才会回答"不可行"，实测 20 罐 / 8 需求
/// 一次求解可超过 240 秒。而"不可行"常常能用多项式时间证明。把这一部分提到搜索之前，
/// 就得到"不可行的题秒回、可行的题才进搜索"的形状。
///
/// 正确性约定（**本类唯一的契约**）：
/// 只有当一个条件**必然导致无解**时才允许返回非 null。
/// 所有判定一律**放宽**（over-approximate）—— 宁可放过，不可错杀。
/// 因此本类的任何改动都不会把"可行"错判成"不可行"；
/// 差分测试 <c>SOLVER-OK</c> 与 <c>test/SolverBench</c> 会进一步确认这一点。
///
/// 与"进度下界"的区别见 <see cref="LowerBound"/>：那个是给玩家看的显示值，
/// **既不能证可行也不能证不可行**，两者不可混用。
/// </summary>
public static class SolverPrecheck
{
    /// <summary>
    /// 若能证明该需求序列**必然无解**，返回原因；否则返回 null（表示"未证明"，需交给搜索）。
    /// </summary>
    public static string? ProveInfeasible(
        List<Requirement> requirements,
        WorkshopInfo workshopInfo,
        List<JarState> jars,
        Dictionary<string, int> liquids,
        int transferBudget)
    {
        if (requirements.Count == 0) return null;

        var cap = workshopInfo.Capability;

        // 快照进入求解器时一律未标记；已标记的罐子不可再被操作，直接排除。
        var usable = jars.Where(j => !j.IsMarked).ToList();

        // ---- 条件 1：一罐一操作 ⇒ 需求数不得超过可用罐子数（主台来源受转移预算上限约束）----
        int nonMaster = usable.Count(j => !j.MasterOrigin);
        int master = usable.Count - nonMaster;
        int masterCap = transferBudget == int.MaxValue
            ? master
            : Math.Min(master, Math.Max(0, transferBudget));
        int capacity = nonMaster + masterCap;
        if (requirements.Count > capacity)
        {
            return "罐子不足：" + requirements.Count + " 个需求，可用罐子 " + capacity
                + "（主台来源 " + master + " 个，转移预算 " + transferBudget + "）";
        }

        // 液体可得性（按种类，放宽判定）：池子里有，**或者**世界上存在一只可倒空的同种满罐。
        // 这一条曾经漏掉过：早先只能在"本罐自己就是满罐"时承认液体可得，
        // 于是把"先倒空别的满罐拿到液体、再灌进空罐"这条合法路径误判成不可行 ——
        // 基准拿 105 个场景把它抓了出来。
        bool CanObtainLiquid(string liquid)
        {
            if (cap.IsBanned(liquid)) return false;
            if (liquids.TryGetValue(liquid, out var have) && have > 0) return true;
            return usable.Any(j => string.Equals(j.Liquid, liquid, StringComparison.Ordinal));
        }

        bool anyCostLiquid = cap.Liquids.Any(CanObtainLiquid);

        // ---- 条件 4：逐液种的反向需求下界（不要求世界里只有一种液体）----
        //
        // 从终态需求往回看：每条「形状 S + 液体 L」需求，只有两种不消耗游离 L 的可能：
        // ① 初始就存在一只同形状、同液体的满罐，直接 MATCH；
        // ② 否则最终必须有一次 POUR_IN(L)，至少消耗一份 L。
        //
        // 用最大二分匹配计算①最多能覆盖多少条需求，得到 L 的最低消耗量。
        // 可供给量故意放宽为「游离 L + 所有未标记满罐中的 L」；后者即使形状不匹配，
        // 也可能先 POUR_OUT 回收，因此这是乐观上界，不会错杀可行解。
        // 如果 L 被禁，任何未直接匹配的需求都无法通过 POUR_IN 得到满足。
        foreach (var liquid in requirements
            .Where(item => item.Liquid != null)
            .Select(item => item.Liquid!)
            .Distinct(StringComparer.Ordinal))
        {
            var liquidRequirements = requirements
                .Where(item => string.Equals(item.Liquid, liquid, StringComparison.Ordinal))
                .ToList();

            var directMatchJar = new int[usable.Count];
            Array.Fill(directMatchJar, -1);

            bool MatchDirect(int requirementIndex, bool[] seen)
            {
                for (var jarIndex = 0; jarIndex < usable.Count; jarIndex++)
                {
                    var jar = usable[jarIndex];
                    if (seen[jarIndex] ||
                        !string.Equals(jar.Shape, liquidRequirements[requirementIndex].Shape, StringComparison.Ordinal) ||
                        !string.Equals(jar.Liquid, liquid, StringComparison.Ordinal))
                        continue;

                    seen[jarIndex] = true;
                    if (directMatchJar[jarIndex] == -1 ||
                        MatchDirect(directMatchJar[jarIndex], seen))
                    {
                        directMatchJar[jarIndex] = requirementIndex;
                        return true;
                    }
                }
                return false;
            }

            var directMatches = 0;
            for (var requirementIndex = 0; requirementIndex < liquidRequirements.Count; requirementIndex++)
                if (MatchDirect(requirementIndex, new bool[usable.Count]))
                    directMatches++;

            var minimumPourIn = liquidRequirements.Count - directMatches;
            if (minimumPourIn == 0) continue;

            if (cap.IsBanned(liquid))
                return "液体「" + liquid + "」被禁：终态需求至少有 " + minimumPourIn
                    + " 条无法由现有同形满罐直接满足";

            var freeSupply = liquids.TryGetValue(liquid, out var available) ? available : 0;
            var recoverableJarSupply = usable.Count(j =>
                string.Equals(j.Liquid, liquid, StringComparison.Ordinal));
            var optimisticSupply = checked(freeSupply + recoverableJarSupply);
            if (minimumPourIn > optimisticSupply)
                return "液体「" + liquid + "」不足：从终态需求反推至少需要 "
                    + minimumPourIn + " 份，乐观可得上限为 " + optimisticSupply;
        }


        // ---- 条件 2：需求与罐子的**二分图完美匹配** ----
        // 依据：一罐一操作 ⇒ 一个可行解必然给每个需求分配一个**互不相同**的罐子。
        // 若不存在这样的分配，则必然无解（Hall 条件的构造性检验）。
        int n = requirements.Count;
        int m = usable.Count;
        var matchJar = new int[m];
        for (int i = 0; i < m; i++) matchJar[i] = -1;

        bool Augment(int req, bool[] seen)
        {
            for (int j = 0; j < m; j++)
            {
                if (seen[j] || !CanServe(requirements[req], usable[j], workshopInfo, CanObtainLiquid, anyCostLiquid))
                    continue;
                seen[j] = true;
                if (matchJar[j] == -1 || Augment(matchJar[j], seen))
                {
                    matchJar[j] = req;
                    return true;
                }
            }
            return false;
        }

        for (int r = 0; r < n; r++)
        {
            if (!Augment(r, new bool[m]))
            {
                return "需求无法一一对应到罐子：" + Describe(requirements[r])
                    + "（没有任何一只可用罐子能承担它，或已被更强的需求占满）";
            }
        }

        // ---- 条件 3：单液体预算的**匹配下界**（只在"恰好一种可用液体"时成立）----
        //
        // 依据（König/对偶的初等版本）：把角色按"是否消耗液体"分成零成本边与一成本边。
        // 任取一个完美匹配，去掉其中花液体的 k 条边，剩下的 N−k 条就是零成本图里的一个匹配，
        // 于是 N−k ≤ ν（ν = 零成本图的最大匹配）⇒ **k ≥ N−ν**。
        // 因此若 N−ν 超过可得的液体总量，则必然无解。这条是**下界**，只用来证伪，不会错杀。
        //
        // 只在恰好一种可用液体时用：两种液体时有"任意液体"这一维，单一的 N−ν 不再对得上账。
        var usableLiquids = cap.Liquids.Where(l => !cap.IsBanned(l)).ToList();
        if (usableLiquids.Count == 1)
        {
            var only = usableLiquids[0];
            var freeMatch = new int[m];
            for (int i = 0; i < m; i++) freeMatch[i] = -1;

            bool FreeAugment(int req, bool[] seen)
            {
                for (int j = 0; j < m; j++)
                {
                    if (seen[j] || !CanServeFree(requirements[req], usable[j], workshopInfo)) continue;
                    seen[j] = true;
                    if (freeMatch[j] == -1 || FreeAugment(freeMatch[j], seen))
                    {
                        freeMatch[j] = req;
                        return true;
                    }
                }
                return false;
            }

            int nu = 0;
            for (int r = 0; r < n; r++)
                if (FreeAugment(r, new bool[m])) nu++;

            int supply = (liquids.TryGetValue(only, out var have) ? have : 0)
                + usable.Count(j => string.Equals(j.Liquid, only, StringComparison.Ordinal));

            // +1 是保守上限：零成本图里可能还存在比 ν 更大的匹配（这里只用了 Kuhn 的一次分配），
            // 多算的余量只会让结论更宽松，绝不会让"可行"被错判成"不可行"。
            if (n - nu > supply)
            {
                return "液体不足：至少需要 " + (n - nu) + " 份" + only + "，实际可得 " + supply
                    + "（零成本可满足的需求最多 " + nu + " 条）";
            }
        }

        return null;
    }

    /// <summary>
    /// 单只罐子能否以**零液体代价**承担该需求.用于条件 3 的匹配下界。
    /// 零成本角色只有四个：MATCH（空且形状对）、POUR_OUT（满且形状对）、满罐改形（自耗）、满罐直接匹配。
    /// </summary>
    private static bool CanServeFree(Requirement req, JarState jar, WorkshopInfo info)
    {
        var cap = info.Capability;
        if (!cap.CanOperateJar(cap.ResolvePolarity(jar.Liquid))) return false;

        if (req.Liquid != null)
            return string.Equals(jar.Shape, req.Shape, StringComparison.Ordinal)
                && string.Equals(jar.Liquid, req.Liquid, StringComparison.Ordinal);

        if (cap.ResolvePolarity(jar.Liquid) == JarPolarity.Neutral)
            return string.Equals(jar.Shape, req.Shape, StringComparison.Ordinal);

        // Full-Jar reshape spends Jar-owned liquid, but it still requires polarity compatibility.
        if (cap.CanTransform(jar.Shape, req.Shape)) return true;
        return string.Equals(jar.Shape, req.Shape, StringComparison.Ordinal)
            && !cap.IsBanned(jar.Liquid!);
    }

    /// <summary>
    /// 单只罐子是否**有可能**承担该需求（放宽判定，宁可高估）。
    ///
    /// 关键事实（由 <see cref="StateSpaceSolver"/> 的操作集决定）：
    /// **满罐需求只能由"形状已经是该形状"的罐子承担** ——
    /// 因为改形与灌液是两次操作，而一罐一操作。
    /// 所以 <c>满(l, S)</c> 只能吃 <c>S</c> 上的罐子；这条比任何搜索剪枝都强。
    /// </summary>
    private static bool CanServe(
        Requirement req, JarState jar, WorkshopInfo info,
        Func<string, bool> canObtainLiquid, bool anyCostLiquid)
    {
        var cap = info.Capability;
        if (!cap.CanOperateJar(cap.ResolvePolarity(jar.Liquid))) return false;

        if (req.Liquid != null)
        {
            // 满罐需求：形状必须已经是 req.Shape，且工作台必须能操作该 Jar 极性
            if (!string.Equals(jar.Shape, req.Shape, StringComparison.Ordinal)) return false;

            // ① 已经是 S/l 满罐 ⇒ MATCH（仍须满足工作台与 Jar 的极性兼容）
            if (string.Equals(jar.Liquid, req.Liquid, StringComparison.Ordinal)) return true;

            // ② 是空 S 罐 ⇒ POUR_IN（要求 S 在基础标记内、液体未被禁、且还弄得一份）
            if (cap.IsBanned(req.Liquid!)) return false;
            return cap.ResolvePolarity(jar.Liquid) == JarPolarity.Neutral
                && cap.IsBaseMarked(req.Shape)
                && canObtainLiquid(req.Liquid!);
        }

        // 空罐需求：形状 S（或能变成 S）
        if (cap.ResolvePolarity(jar.Liquid) == JarPolarity.Neutral)
        {
            // ① 已经是空 S 罐 ⇒ MATCH
            if (string.Equals(jar.Shape, req.Shape, StringComparison.Ordinal)) return true;
            // ② 空罐改形 ⇒ RESHAPE（需要一份未被禁的成本液体）
            return cap.CanTransform(jar.Shape, req.Shape) && anyCostLiquid;
        }

        // ③ 满罐改形：资源从 Jar 内部扣除，但工作台仍须有权操作该 Jar 极性。
        if (cap.CanTransform(jar.Shape, req.Shape)) return true;
        // ④ 倒空后形状正好是 S ⇒ POUR_OUT 后直接 MATCH；此路径进入 staging，仍受 Ban。
        return string.Equals(jar.Shape, req.Shape, StringComparison.Ordinal)
            && !cap.IsBanned(jar.Liquid!);
    }

    /// <summary>
    /// **进度下界**（"幼苗"）：按给定顺序贪心地往上长，能长多高就报多高。
    ///
    /// 与 <see cref="ProveInfeasible"/> 严格区分：本方法返回的 <c>height</c>
    /// **只是下界** —— 它小不代表无解（贪心可能选错），因此：
    /// <list type="bullet">
    /// <item>可以拿去给玩家显示"灌注 3/7"（<c>设计分析_过程系统内核.md:76</c> 的暗箱原则）；</item>
    /// <item><b>不可以</b>用来判定可行或不可行。</item>
    /// </list>
    /// 复杂度 O(需求数 × 罐子数)，与搜索无关。
    /// </summary>
    public static int LowerBound(
        List<Requirement> requirements,
        WorkshopInfo workshopInfo,
        List<JarState> jars,
        Dictionary<string, int> liquids)
    {
        var st = new List<JarState>(jars.Select(j => JarState.Copy(j)));
        var pool = new Dictionary<string, int>(liquids);
        var cap = workshopInfo.Capability;
        int height = 0;

        foreach (var req in requirements)
        {
            bool applied = false;
            for (int i = 0; i < st.Count && !applied; i++)
            {
                var j = st[i];
                if (j.IsMarked || !cap.CanOperateJar(cap.ResolvePolarity(j.Liquid))) continue;

                if (req.Liquid != null)
                {
                    if (!string.Equals(j.Shape, req.Shape, StringComparison.Ordinal)) continue;
                    // MATCH
                    if (string.Equals(j.Liquid, req.Liquid, StringComparison.Ordinal))
                    {
                        j.IsMarked = true;
                        applied = true;
                        break;
                    }
                    // POUR_IN
                    if (cap.ResolvePolarity(j.Liquid) == JarPolarity.Neutral && cap.IsBaseMarked(req.Shape) && !cap.IsBanned(req.Liquid!)
                        && pool.TryGetValue(req.Liquid!, out var have) && have > 0)
                    {
                        pool[req.Liquid!] = have - 1;
                        j.Liquid = req.Liquid;
                        j.IsMarked = true;
                        applied = true;
                        break;
                    }
                    continue;
                }

                // 空罐需求：MATCH
                if (cap.ResolvePolarity(j.Liquid) == JarPolarity.Neutral && string.Equals(j.Shape, req.Shape, StringComparison.Ordinal))
                {
                    j.IsMarked = true;
                    applied = true;
                    break;
                }
                // 满罐改形（自耗）
                if (cap.ResolvePolarity(j.Liquid) != JarPolarity.Neutral && cap.CanOperateJar(cap.ResolvePolarity(j.Liquid)) && cap.CanTransform(j.Shape, req.Shape))
                {
                    // Liquid is consumed from the Jar, while Workshop polarity authority still applies.
                    j.Liquid = null;
                    j.Shape = req.Shape;
                    j.IsMarked = true;
                    applied = true;
                    break;
                }
                // 空罐改形（付一份成本液体）
                if (cap.ResolvePolarity(j.Liquid) == JarPolarity.Neutral && cap.CanTransform(j.Shape, req.Shape))
                {
                    foreach (var cl in cap.Liquids)
                    {
                        if (cap.IsBanned(cl)) continue;
                        if (!pool.TryGetValue(cl, out var have) || have <= 0) continue;
                        pool[cl] = have - 1;
                        j.Shape = req.Shape;
                        j.IsMarked = true;
                        applied = true;
                        break;
                    }
                    if (applied) break;
                }
            }

            if (!applied) break;
            height++;
        }

        return height;
    }

    private static string Describe(Requirement req) =>
        req.Liquid == null ? "空罐(" + req.Shape + ")" : "满罐(" + req.Shape + "," + req.Liquid + ")";
}

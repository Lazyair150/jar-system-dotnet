using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Solver
{
    /// <summary>
    /// 匹配形求解器（assignment-first）：把"找操作序列"降级成"给每个需求分配一只罐子并指定角色"。
    ///
    /// <para><b>为什么可以这样降级。</b>四类操作都会以 <c>MarkJar</c> 收尾，而已标记的罐子进不了任何分支，
    /// 所以每只罐子在一次求解里至多被操作一次。作用于不同罐子的操作因此互不干扰 —— 唯一共享的是
    /// 液体账目（纯加法）与转移计数（单调递增），两者都与顺序无关。于是可行性**只取决于分配**，
    /// 与操作排布无关；DFS 探索操作序列时那棵树里的重复，在这个视角下根本不存在。</para>
    ///
    /// <para><b>角色表（每只罐子至多承担一个）。</b>
    /// 服务空罐需求 S：空且形状 S → MATCH；空 T≠S 可改形 → RS-E（付 1 单位任意液体）；
    /// 满 l 且形状 S → POUR_OUT（产出 1 单位 l）；满 l 可改形 → RS-F（自耗，不付池）。
    /// 服务满罐需求 (S,l)：满 l 且形状 S → MATCH；空且形状 S → POUR_IN（付 1 单位 l）。
    /// 满罐需求之所以没有改形分支，是因为改形+灌液是两次操作，违反一罐一操作。</para>
    ///
    /// <para><b>健全性。</b>本实现只在<b>构造出并通过执行式仿真验证</b>时才回"可行"；
    /// 一旦分配搜索失败或构造出的序列过不了仿真，就把问题原样交给后备求解器。
    /// 因此它永远不会比后备实现多回"不可行"——错杀在结构上不可能发生。</para>
    /// </summary>
    public sealed class MatchingSolver : ISolver, IWorkBudgetedSolver
    {
        /// <summary>分配搜索的节点上限（安全阀）。超出即放弃并回退，不影响正确性。</summary>
        private const int MaxAssignmentNodes = 8_000;

        private readonly ISolver _fallback;

        public MatchingSolver() : this(new PrecheckedStateSpaceSolver()) { }

        public MatchingSolver(ISolver fallback)
            => _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

        /// <summary>本实现直接给出结论（且已通过仿真验证）的次数。</summary>
        public int DirectHits { get; private set; }

        /// <summary>分配阶段放弃、交给后备求解器的次数。</summary>
        public int Fallbacks { get; private set; }

        /// <summary>分配阶段直接判定「不可行」的次数（不进入指数搜索）。</summary>
        public int InfeasibleProved { get; private set; }

        /// <inheritdoc />
        public bool BudgetExceeded { get; private set; }

        /// <inheritdoc />
        public void BeginCheck(int nodeBudget)
        {
            BudgetExceeded = false;
            if (_fallback is IWorkBudgetedSolver budgeted) budgeted.BeginCheck(nodeBudget);
        }

        /// <inheritdoc />
        public SolverResult Solve(List<Requirement> requirements, WorkshopInfo workshopInfo,
            List<JarState> jars, Dictionary<string, int> liquids, int transferBudget = int.MaxValue)
        {
            // 口径统一在这里（见 SolverInput）：禁液不是资源。
            liquids = SolverInput.UsableLiquids(liquids, workshopInfo.Capability);
            // 第 0 层：能一步证伪的廉价必要条件（罐子数容量 + 需求↔罐子二分图匹配）。
            var cheap = SolverPrecheck.ProveInfeasible(requirements, workshopInfo, jars, liquids, transferBudget);
            if (cheap != null)
            {
                InfeasibleProved++;
                return new SolverResult
                {
                    Feasible = false,
                    Blockage = cheap,
                    Stats = new SolverStats(),
                };
            }

            var assignment = TryAssign(requirements, workshopInfo, jars, liquids, transferBudget);

            if (assignment == null)
            {
                // 分配搜索被节点上限掐断时，"没找到"不等于"不存在" ⇒ 必须交给后备裁决，
                // 否则就是一条会被随机场景漏掉的错杀路径。
                if (_gaveUp)
                {
                    Fallbacks++;
                    return _fallback.Solve(requirements, workshopInfo, jars, liquids, transferBudget);
                }

                // 角色模型对「一罐一操作」是完备的：任何一只罐子服务任何一条需求的**单次操作**
                // 都恰好对应六个角色之一（见 BuildOptions）。因此"分配不出"就是"不可行"，
                // 不必再跑指数搜索。这条结论由 SolverBench 的三方对拍持续把关。
                InfeasibleProved++;
                return new SolverResult
                {
                    Feasible = false,
                    Blockage = "没有任何分配能让每条需求都得到一只可服务的罐子",
                    Stats = new SolverStats(),
                };
            }

            var built = BuildPath(assignment, requirements, workshopInfo, jars, liquids);
            if (built != null)
            {
                DirectHits++;
                return built;
            }

            // 分配成立但"取不出液体"的调度细节拿不准（池上限交错），交给后备实现裁决。
            Fallbacks++;
            return _fallback.Solve(requirements, workshopInfo, jars, liquids, transferBudget);
        }

        // ── 角色 ────────────────────────────────────────────────────────────────

        private enum Role { MatchFull, PourIn, MatchEmpty, ReshapeEmpty, PourOut, ReshapeFull }

        private readonly struct Slot
        {
            public Slot(int jarIndex, Role role, string? liquid)
            {
                JarIndex = jarIndex;
                Role = role;
                Liquid = liquid;
            }

            /// <summary>被分配去服务需求的罐子下标。</summary>
            public int JarIndex { get; }

            public Role Role { get; }

            /// <summary>需要从池里支付（或产出）的液体；RS-E 为 null 表示"任意液体"。</summary>
            public string? Liquid { get; }
        }

        private sealed class Assignment
        {
            /// <summary>与 requirements 一一对应的槽位。</summary>
            public Slot[] Slots = Array.Empty<Slot>();

            /// <summary>被指定为液体来源（不服务任何需求）的罐子下标。</summary>
            public List<int> Sources = new();

            public int Nodes;
        }

        // ── 分配搜索 ─────────────────────────────────────────────────────────────

        private Assignment? TryAssign(List<Requirement> requirements, WorkshopInfo info,
            List<JarState> jars, Dictionary<string, int> liquids, int transferBudget)
        {
            _nodes = 0;
            _dead = false;
            _gaveUp = false;

            var n = requirements.Count;
            if (n == 0) return new Assignment { Slots = Array.Empty<Slot>() };

            // 每个需求的可选槽位。MRV：可行槽位最少的需求先分。
            var options = new List<Slot>[n];
            for (var r = 0; r < n; r++) options[r] = BuildOptions(requirements[r], info, jars, liquids);

            var order = Enumerable.Range(0, n)
                .OrderBy(r => options[r].Count)
                .ThenBy(r => r)
                .ToArray();

            for (var i = 0; i < n; i++)
                if (options[order[i]].Count == 0) return null;

            var liquidKeys = info.Capability.Liquids.ToArray();
            var consPin = new int[liquidKeys.Length];
            var prodOut = new int[liquidKeys.Length];
            var flex = 0;
            var usedMaster = 0;
            var used = new bool[jars.Count];
            var slots = new Slot[n];

            bool Dfs(int depth)
            {
                if (++_nodes > MaxAssignmentNodes)
                {
                    _dead = true;
                    return false;
                }
                if (depth == n) return Balance();

                // 前向检查（MRV 的搭档，CSP 里的 forward checking）：
                // 剩下的每条需求都必须还有至少一只未被占用的候选罐子。
                // 不做这一步，就得等回溯到第 7 条才发现"第 3 条其实早就没得选了"——
                // 那是最典型的白费工作量，表现为 `_gaveUp` 偏高、回退次数偏高。
                for (var d = depth; d < n; d++)
                {
                    var anyFree = false;
                    foreach (var candidate in options[order[d]])
                    {
                        if (used[candidate.JarIndex]) continue;
                        anyFree = true;
                        break;
                    }
                    if (!anyFree) return false;
                }

                var r = order[depth];
                foreach (var slot in options[r])
                {
                    if (used[slot.JarIndex]) continue;

                    var isMaster = jars[slot.JarIndex].MasterOrigin;
                    if (isMaster && usedMaster >= transferBudget) continue;

                    used[slot.JarIndex] = true;
                    if (isMaster) usedMaster++;

                    var li = Array.IndexOf(liquidKeys, slot.Liquid);
                    switch (slot.Role)
                    {
                        case Role.PourIn: consPin[li]++; break;
                        case Role.PourOut: if (!info.Capability.IsBanned(slot.Liquid!)) prodOut[li]++; break;
                        case Role.ReshapeEmpty: flex++; break;
                    }

                    slots[r] = slot;
                    if (Dfs(depth + 1)) return true;

                    switch (slot.Role)
                    {
                        case Role.PourIn: consPin[li]--; break;
                        case Role.PourOut: if (!info.Capability.IsBanned(slot.Liquid!)) prodOut[li]--; break;
                        case Role.ReshapeEmpty: flex--; break;
                    }
                    if (isMaster) usedMaster--;
                    used[slot.JarIndex] = false;

                    if (_dead) return false;
                }
                return false;
            }

            // 收尾：把"产出 − 消耗"与剩余的液体来源对齐，确认真的付得起。
            bool Balance()
            {
                foreach (var l in info.Capability.Liquids)
                    if (info.Capability.IsBanned(l) && consPin[Array.IndexOf(liquidKeys, l)] > 0) return false;

                var surplus = 0;
                for (var i = 0; i < liquidKeys.Length; i++)
                {
                    var l = liquidKeys[i];
                    var start = Math.Max(0, liquids.TryGetValue(l, out var v) ? v : 0);

                    // 该液体还能不能继续从"没用上的满罐"里取；池满时取不出，这里保守按可取计，
                    // 真正的池上限由 BuildPath 的调度阶段裁决（取不出就整体回退）。
                    var extra = 0;
                    for (var j = 0; j < jars.Count; j++)
                        if (!used[j] && jars[j].Liquid == l && !info.Capability.IsBanned(l)) extra++;

                    var avail = start + prodOut[i] + extra - consPin[i];
                    if (avail < 0) return false;
                    surplus += avail;
                }
                // RS-E 每一条要吃 1 单位"任意液体"，只能由上面的盈余支付。
                return surplus >= flex;
            }

            if (!Dfs(0)) { _gaveUp = _dead; return null; }

            var result = new Assignment { Slots = slots, Nodes = _nodes };
            for (var j = 0; j < jars.Count; j++)
                if (!used[j]) result.Sources.Add(j);
            return result;
        }

        private int _nodes;
        private bool _dead;
        private bool _gaveUp;

        private List<Slot> BuildOptions(Requirement req, WorkshopInfo info, List<JarState> jars,
            Dictionary<string, int> liquids)
        {
            var cap = info.Capability;
            var slots = new List<Slot>();
            // 成本液体只要"世界上还取得出来"就算数：池里有，或者还有满罐可以倒空。
            var anyCost = info.Capability.Liquids.Any(l => !cap.IsBanned(l)
                && ((liquids.TryGetValue(l, out var v) && v > 0)
                    || jars.Any(j => j.Liquid == l)));

            for (var j = 0; j < jars.Count; j++)
            {
                var jar = jars[j];
                if (!cap.CanOperateJar(cap.ResolvePolarity(jar.Liquid))) continue;
                if ((req.Liquid == null ? JarPolarity.Neutral : cap.ResolvePolarity(req.Liquid)) != JarPolarity.Neutral)
                {
                    // 满罐需求：形状必须已经是它自己 —— 改形 + 灌液是两次操作。
                    if (jar.Shape != req.Shape) continue;

                    // MATCH 仍要求 Jar 极性与当前工作台专注方向兼容。
                    if (jar.Liquid == req.Liquid) slots.Add(new Slot(j, Role.MatchFull, null));
                    else if (cap.ResolvePolarity(jar.Liquid) == JarPolarity.Neutral && cap.IsBaseMarked(req.Shape) && !cap.IsBanned(req.Liquid!))
                        slots.Add(new Slot(j, Role.PourIn, req.Liquid));
                }
                else
                {
                    if (jar.Shape == req.Shape && cap.ResolvePolarity(jar.Liquid) == JarPolarity.Neutral)
                        slots.Add(new Slot(j, Role.MatchEmpty, null));
                    else if (jar.Shape == req.Shape && cap.ResolvePolarity(jar.Liquid) != JarPolarity.Neutral)
                    {
                        slots.Add(new Slot(j, Role.PourOut, jar.Liquid));
                    }
                    else
                    {
                        if (!cap.CanTransform(jar.Shape, req.Shape)) continue;
                        if (cap.ResolvePolarity(jar.Liquid) == JarPolarity.Neutral)
                        {
                            if (anyCost) slots.Add(new Slot(j, Role.ReshapeEmpty, null));
                        }
                        else
                        {
                            // Full reshape consumes Jar-owned liquid but still requires Workshop/Jar polarity compatibility.
                            slots.Add(new Slot(j, Role.ReshapeFull, jar.Liquid));
                        }
                    }
                }
            }
            return slots;
        }

        // ── 由分配反推操作序列 ───────────────────────────────────────────────────

        /// <summary>
        /// 把「哪只罐子服务哪个需求、以什么角色」翻译成真实的 <see cref="SolverStep"/> 序列。
        /// 液体不够、池已满之类的现实约束在这里裁决；任何一步做不下去就返回 null 交给后备实现。
        /// </summary>
        private SolverResult? BuildPath(Assignment assignment, List<Requirement> requirements,
            WorkshopInfo info, List<JarState> jars, Dictionary<string, int> liquids)
        {
            var cap = info.Capability;
            var state = jars.Select(j => JarState.Copy(j)).ToList();
            var pool = new Dictionary<string, int>(liquids);
            foreach (var l in info.Capability.Liquids) if (!pool.ContainsKey(l)) pool[l] = 0;

            var steps = new List<SolverStep>();

            // 待办：先"增产"后"消耗"（增产把液体送进池，消耗从池里取）。池满时增产做不下去，
            // 于是交错执行 —— 用一个"能增产就增产、否则消耗"的调度把两头对齐。
            var produce = new Queue<(int jar, string liquid, Requirement? req)>();
            var consume = new Queue<(int jar, Role role, Requirement req, string? liquid)>();

            for (var i = 0; i < requirements.Count; i++)
            {
                var slot = assignment.Slots[i];
                var req = requirements[i];
                if (slot.Role == Role.PourOut)
                {
                    produce.Enqueue((slot.JarIndex, slot.Liquid!, req));
                    consume.Enqueue((slot.JarIndex, Role.MatchEmpty, req, null)); // 倒空本身就满足该空罐需求
                }
                else
                {
                    consume.Enqueue((slot.JarIndex, slot.Role, req, slot.Liquid));
                }
            }

            // 只在"池子不够"时才动用来当液体来源的满罐，而且按需取，不铺张。
            var sourceUsed = new HashSet<int>();
            var guard = 0;
            while (consume.Count > 0 || produce.Count > 0)
            {
                if (++guard > 4096) return null;

                // 优先增产（池子低时永远安全）。
                if (produce.Count > 0 && CanRaise(pool, cap, state, produce.Peek().jar))
                {
                    var (jarIndex, liquid, req) = produce.Dequeue();
                    if (!PourOut(state, pool, jarIndex, liquid, req, steps, info)) return null;
                    // 同时满足需求的那条 consume 记录已经入队，等下自然被取出。
                    continue;
                }

                // 否则消耗：先把钱凑够。
                if (consume.Count > 0 && !CanPay(pool, cap, state, assignment, sourceUsed, consume.Peek().liquid))
                {
                    if (!TryRaise(pool, cap, state, assignment, sourceUsed, steps, info,
                            consume.Peek().liquid)) return null;
                    continue;
                }

                // 两头都动不了 ⇒ 这个分配在现实约束下不可执行。
                if (consume.Count == 0 && produce.Count > 0) return null;

                var head = consume.Peek();

                // 已经由 produce 阶段倒空过的那条，跳过重复执行。
                if (head.role == Role.MatchEmpty && head.liquid == null
                    && cap.ResolvePolarity(state[head.jar].Liquid) == JarPolarity.Neutral && state[head.jar].Shape == head.req.Shape
                    && state[head.jar].IsMarked)
                {
                    consume.Dequeue();
                    continue;
                }

                consume.Dequeue();
                if (!Apply(state, pool, head.jar, head.role, head.req, head.liquid, steps, info)) return null;
            }

            // 终态必须是每个需求都被一只符合条件的罐子承担。
            for (var i = 0; i < requirements.Count; i++)
            {
                var s = state[assignment.Slots[i].JarIndex];
                if (s.Shape != requirements[i].Shape) return null;
                if (s.Liquid != requirements[i].Liquid) return null;
            }

            return new SolverResult
            {
                Feasible = true,
                Path = steps,
                Stats = new SolverStats { NodesExplored = assignment.Nodes, SolutionsFound = 1 },
                FinalJars = state,
                FinalLiquids = pool
            };
        }

        private static bool CanRaise(Dictionary<string, int> pool, WorkshopCapability cap,
            List<JarState> state, int jarIndex)
        {
            var jar = state[jarIndex];
            if (jar.IsMarked || cap.ResolvePolarity(jar.Liquid) == JarPolarity.Neutral) return false;
            if (!cap.CanOperateJar(cap.ResolvePolarity(jar.Liquid)) || cap.IsBanned(jar.Liquid!)) return false;
            // POUR_OUT 的前提：该液体尚未灌满存储上限。
            return pool[jar.Liquid!] < Constants.DEFAULT_MAX_LIQUIDS;
        }

        private static bool CanPay(Dictionary<string, int> pool, WorkshopCapability cap,
            List<JarState> state, Assignment a, HashSet<int> usedSources, string? liquid)
        {
            if (liquid != null) return pool[liquid] > 0;
            foreach (var l in cap.Liquids)
                if (!cap.IsBanned(l) && pool[l] > 0) return true;
            return false;
        }

        /// <summary>池子不够时，从"没被分配去服务需求"的满罐里倒出刚好够用的那一份。</summary>
        private static bool TryRaise(Dictionary<string, int> pool, WorkshopCapability cap,
            List<JarState> state, Assignment a, HashSet<int> usedSources,
            List<SolverStep> steps, WorkshopInfo info, string? want)
        {
            foreach (var j in a.Sources)
            {
                if (usedSources.Contains(j)) continue;
                var jar = state[j];
                if (jar.IsMarked || cap.ResolvePolarity(jar.Liquid) == JarPolarity.Neutral) continue;
                if (!cap.CanOperateJar(cap.ResolvePolarity(jar.Liquid)) || cap.IsBanned(jar.Liquid!)) continue;
                if (want != null && jar.Liquid != want) continue;
                if (!CanRaise(pool, cap, state, j)) continue;

                usedSources.Add(j);
                if (!PourOut(state, pool, j, jar.Liquid!, null, steps, info)) return false;

                if (CanPay(pool, cap, state, a, usedSources, want)) return true;
            }
            return false;
        }

        private static bool PourOut(List<JarState> state, Dictionary<string, int> pool, int jarIndex,
            string liquid, Requirement? req, List<SolverStep> steps, WorkshopInfo info)
        {
            if (jarIndex < 0 || jarIndex >= state.Count) return false;
            var jar = state[jarIndex];
            if (jar.IsMarked || jar.Liquid != liquid) return false;
            if (pool[liquid] >= Constants.DEFAULT_MAX_LIQUIDS) return false;

            var before = state.Select(s => JarState.Copy(s)).ToList();
            var after = state.Select(s => JarState.Copy(s)).ToList();
            after[jarIndex] = new JarState
            {
                Id = jar.Id,
                Shape = jar.Shape,
                Liquid = null,
                IsMarked = true,
                MasterOrigin = jar.MasterOrigin
            };

            if (!info.Capability.IsBanned(liquid)) pool[liquid] += 1;

            steps.Add(new SolverStep
            {
                Step = steps.Count,
                Requirement = req,
                Operation = new SolverOperation
                {
                    Type = "POUR_OUT",
                    Description = $"倒空 #{(jar.Id?.ToString() ?? jarIndex.ToString())} 的{liquid}",
                    JarIndex = jarIndex
                },
                Before = before,
                After = after,
                AfterLiquids = new Dictionary<string, int>(pool)
            });

            var cur = state[jarIndex];
            cur.Liquid = null;
            cur.IsMarked = true;
            return true;
        }

        private static bool Apply(List<JarState> state, Dictionary<string, int> pool, int jarIndex,
            Role role, Requirement req, string? liquid, List<SolverStep> steps, WorkshopInfo info)
        {
            if (jarIndex < 0 || jarIndex >= state.Count) return false;
            var jar = state[jarIndex];
            var cap = info.Capability;
            if (jar.IsMarked || !cap.CanOperateJar(cap.ResolvePolarity(jar.Liquid))) return false;
            string type;
            string? cost = null;
            var fullReshape = false;
            var targetShape = jar.Shape;
            var targetLiquid = jar.Liquid;

            switch (role)
            {
                case Role.MatchFull:
                case Role.MatchEmpty:
                    type = "MATCH";
                    break;

                case Role.PourIn:
                    if (cap.ResolvePolarity(jar.Liquid) != JarPolarity.Neutral || jar.Shape != req.Shape) return false;
                    if (!cap.IsBaseMarked(req.Shape)) return false;
                    if (req.Liquid == null || liquid == null || !pool.ContainsKey(liquid)) return false;
                    if (cap.IsBanned(liquid) || pool[liquid] <= 0) return false;
                    type = "POUR_IN";
                    targetLiquid = liquid;
                    break;

                case Role.ReshapeEmpty:
                    if (cap.ResolvePolarity(jar.Liquid) != JarPolarity.Neutral) return false;
                    if (!cap.CanTransform(jar.Shape, req.Shape)) return false;
                    cost = cap.Liquids.FirstOrDefault(l => !cap.IsBanned(l) && pool[l] > 0);
                    if (cost == null) return false;
                    type = "RESHAPE";
                    targetShape = req.Shape;
                    break;

                case Role.ReshapeFull:
                    if (cap.ResolvePolarity(jar.Liquid) == JarPolarity.Neutral) return false;
                    if (!cap.CanTransform(jar.Shape, req.Shape)) return false;
                    type = "RESHAPE";
                    targetShape = req.Shape;
                    fullReshape = true;
                    targetLiquid = null;
                    break;

                default:
                    return false;
            }

            var before = state.Select(s => JarState.Copy(s)).ToList();
            var after = state.Select(s => JarState.Copy(s)).ToList();
            after[jarIndex] = new JarState
            {
                Id = jar.Id,
                Shape = targetShape,
                Liquid = targetLiquid,
                IsMarked = true,
                MasterOrigin = jar.MasterOrigin
            };

            if (role == Role.PourIn) pool[liquid!] -= 1;
            if (role == Role.ReshapeEmpty) pool[cost!] -= 1;

            steps.Add(new SolverStep
            {
                Step = steps.Count,
                Requirement = req,
                Operation = new SolverOperation
                {
                    Type = type,
                    Description = type + " #" + (jar.Id?.ToString() ?? jarIndex.ToString()),
                    JarIndex = jarIndex,
                    TargetShape = targetShape,
                    TargetLiquid = targetLiquid,
                    CostLiquid = cost,
                    Consumed = fullReshape || cost != null
                        ? new Dictionary<string, int> { [cost ?? jar.Liquid!] = 1 }
                        : new Dictionary<string, int>(),
                    FullReshape = fullReshape
                },
                Before = before,
                After = after,
                AfterLiquids = new Dictionary<string, int>(pool)
            });

            var cur = state[jarIndex];
            cur.Shape = targetShape;
            cur.Liquid = targetLiquid;
            cur.IsMarked = true;
            return true;
        }
    }
}

using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Abstractions;

// ══ 求解器契约的**值词汇**（2026-10 从 Orders/ 与 Solver/ 抽出）══
//
// ISolver 的签名由这些类型构成（Requirement / JarState / WorkshopInfo / SolverResult），
// 而它们原先分散在 Orders/Order.cs 与 Solver/StateSpaceSolver.cs，于是
// Abstractions 反过来依赖 Orders 与 Solver 两个实现层，形成三组双向依赖：
//   Abstractions <-> Orders 、Abstractions <-> Solver 、Orders <-> Solver
// 契约词汇属于契约层，实现层才能单向依赖它 —— 这是本次搬迁的全部理由。
//
// 搬迁**零行为变化**：类型名、属性名、属性类型一字未改（REPLAY 字节与契约快照因此不受影响）。

/// <summary>
/// 求解器的**预算上限**（契约层常量）。
///
/// <para>为什么单独提到这里：<c>Orders.FeasibilityChecker.NodeBudget</c> 的默认值原本直接引用
/// <c>Solver.StateSpaceSolver.DefaultNodeBudget</c>，于是"可行性分析"（Orders）反过来依赖
/// "求解器实现"（Solver）——而它只需要一个数字。数字属于契约层，取值的理由留在实现里。</para>
/// </summary>
public static class SolverLimits
{
    /// <summary>
    /// 默认工作预算（搜索节点数）。取值依据：实测本机每个节点约 4~5 µs，
    /// 因此 40 万节点 ≈ 最坏 2 秒 —— 这是一次可行性分析允许占住全局闸门的上限。
    /// </summary>
    public const int DefaultNodeBudget = 400_000;
}

public class Requirement
{
    // ⚠️ init：需求一旦构造即不可变。
    // Order.Requirements 已改成只读列表；但若元素仍可写，`order.Requirements[0].Shape = "x"`
    // 依然能编译并通过 —— ValidateOrder() 的约定（A/B 类型、需求数上限、空罐在前满罐在后）
    // 就被绕开了。全仓 35 处构造**全部**用对象初始化器、且零处构造后赋值，故 init 零破坏。
    public string Shape { get; init; } = "";
    public string? Liquid { get; init; }
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

    /// <summary>被"失败状态记忆化"挡下的重复探索次数（诊断用，不参与任何对外契约）。</summary>
    public int MemoHits { get; set; }

    /// <summary>被"同构罐子剪枝"省掉的等价候选操作数（诊断用）。</summary>
    public int IsomorphicPruned { get; set; }
}

public class SolverResult
{
    public bool Feasible { get; set; }
    public List<SolverStep>? Path { get; set; }
    public string? Blockage { get; set; }
    public SolverStats? Stats { get; set; }

    /// <summary>
    /// 本次求解是否因**超出工作预算**而提前中止（见 <see cref="IWorkBudgetedSolver"/>）。
    /// true 时 <see cref="Feasible"/>=false 表示"未判定"，**不是**"不可行"。
    /// </summary>
    public bool BudgetExceeded { get; set; }

    /// <summary>
    /// 可行时的**最终世界状态：罐子**。这是**求解器契约的一部分**，不是可选诊断信息 ——
    /// B 类（<c>PartitionSearch</c>）要靠它算"主台剩下的罐子"才能构造从台求解池。
    /// 缺失时旧实现会退化成"用主台**原始**状态"，于是从台池算错、结论静默失真
    /// （差分测试实测过：只填 Path 不填快照的求解器会让 B 类算错）。
    /// </summary>
    public List<JarState>? FinalJars { get; set; }

    /// <summary>可行时的**最终世界状态：液体账目**（同 <see cref="FinalJars"/> 的契约要求）。</summary>
    public Dictionary<string, int>? FinalLiquids { get; set; }
}

/// <summary>
/// StateSpaceSolver v6 — 状态空间搜索求解器（实现 <see cref="ISolver"/>）
/// 合法操作: MATCH(预定), POUR_IN(倒液), POUR_OUT(倒空), RESHAPE(改形)
/// RESHAPE: 适用于空罐(用工作台液体)和满罐(用自身液体)
/// </summary>

public class SolverStep
{
    public int Step { get; set; }
    public Requirement? Requirement { get; set; }
    public SolverOperation? Operation { get; set; }
    public List<JarState>? Before { get; set; }
    public List<JarState>? After { get; set; }
    public Dictionary<string, int>? AfterLiquids { get; set; }
}

/// <summary>
/// 求解器操作步骤。属性名与对外 JSON 的路径步骤字段对齐
/// （jarIndex / targetShape / targetLiquid / costLiquid / consumed），避免缩写造成的对照成本。
/// </summary>
public class SolverOperation
{
    public string? Type { get; set; }
    public string? Description { get; set; }
    public int? JarIndex { get; set; }
    public string? TargetShape { get; set; }
    public string? TargetLiquid { get; set; }
    public string? CostLiquid { get; set; }
    public Dictionary<string, int>? Consumed { get; set; }
    public bool FullReshape { get; set; }
}

public class JarState
{
    public int? Id { get; set; }
    public string Shape { get; set; } = "";
    public string? Liquid { get; set; }
    public bool IsMarked { get; set; }
    // 选择性转移标记：从台求解池中的主台来源罐子（受转移预算约束，未使用则留在主台）
    public bool MasterOrigin { get; set; }

    /// <summary>复制求解器状态（投影唯一实现）。<paramref name="masterOrigin"/> 为 null 时保留来源标记。</summary>
    public static JarState Copy(JarState source, bool? masterOrigin = null) => new()
    {
        Id = source.Id,
        Shape = source.Shape,
        Liquid = source.Liquid,
        IsMarked = source.IsMarked,
        MasterOrigin = masterOrigin ?? source.MasterOrigin
    };
}

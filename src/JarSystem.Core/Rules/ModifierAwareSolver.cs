using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Solver;

namespace JarSystem.Core.Rules;

/// <summary>
/// **修饰器感知的求解器**（装饰器）：不修改求解器内部，而是在它**外面**统一处理"后来的新规则"。
///
/// <para><b>为什么用装饰器而不是改求解器</b>：求解器是被差分门（4003 次比对）与 5000 用例钉住的既有资产；
/// 把"临时规则"塞进它的规则读取点，等于每加一条规则都要重走一遍求解器的正确性论证。更本质的是职责分离：
/// 修饰器是**世界状态的一部分**（区间性、随时来去），求解器是**纯算法** —— 两者的变更理由不同。</para>
///
/// <para><b>三条 sound 语义</b>（关键：绝不把"说不准"说成"不可行"）：</para>
/// <list type="number">
/// <item><b>无修饰器 ⇒ 直通</b>：逐字节等于原实现（这是等价性的第一道保障）；</item>
/// <item><b>可表达的规则 ⇒ 前置投影</b>：把抑制折进输入（被抑制的液体视作被禁）、把授予折进附加标记；</item>
/// <item><b>无法表达的规则 ⇒ 未判定</b>（undetermined，经 <see cref="IWorkBudgetedSolver.InconclusiveReason"/> 上报），
/// 而不是"不可行" —— 因为"第一个找到的路径踩了限制"不等于"没有别的路径"。</item>
/// </list>
///
/// <para>哪些规则可表达、哪些不可表达，见 <see cref="Solve"/> 的分类注释。时间类（`Retime`）与强度类
/// （`Intensity`）**不影响静态可行性**，因此被忽略（它们由过程系统与产出环节消费）。</para>
/// </summary>
public sealed class ModifierAwareSolver : ISolver, IWorkBudgetedSolver
{
    private readonly ISolver _inner;
    private readonly ModifierStack _stack;
    private string? _inconclusive;

    public ModifierAwareSolver(ISolver inner, ModifierStack stack)
    {
        _inner = inner;
        _stack = stack;
    }

    private IWorkBudgetedSolver? InnerBudgeted => _inner as IWorkBudgetedSolver;

    /// <inheritdoc />
    public void BeginCheck(int nodeBudget)
    {
        _inconclusive = null;
        InnerBudgeted?.BeginCheck(nodeBudget);
    }

    /// <inheritdoc />
    public bool BudgetExceeded => _inconclusive != null || (InnerBudgeted?.BudgetExceeded ?? false);

    /// <inheritdoc />
    public string? InconclusiveReason => _inconclusive ?? InnerBudgeted?.InconclusiveReason;

    /// <inheritdoc />
    public SolverResult Solve(List<Requirement> requirements, WorkshopInfo workshopInfo,
        List<JarState> jars, Dictionary<string, int> liquids, int transferBudget = int.MaxValue)
    {
        // ① 直通：没有活跃修饰器时，本层**完全不可见**（行为与原实现逐字节相同）
        if (_stack.Active.Count == 0)
            return _inner.Solve(requirements, workshopInfo, jars, liquids, transferBudget);

        // ② 分类：可表达的折进输入；不可表达的 ⇒ 未判定；与静态可行性无关的 ⇒ 忽略
        var banned = new List<string>();
        var granted = new List<string>();
        var removedShapes = new List<string>();
        foreach (var m in _stack.Active)
        {
            switch (m)
            {
                case RetimeModifier:
                case IntensityModifier:
                    break;                       // 只影响时间 / 强度，不影响静态可行性

                case GrantModifier g when !string.IsNullOrEmpty(g.Shape):
                    if (!granted.Contains(g.Shape)) granted.Add(g.Shape);
                    break;                       // 可表达：视作附加标记

                case SuppressModifier s when !string.IsNullOrEmpty(s.Liquid)
                    && s.Operation is null or "POUR_IN" or "POUR_OUT" or "RESHAPE":
                    // 可表达：基础法则里"被禁液体"恰好覆盖倒入、存储、作为改形成本、以及倒出（PourOut 也会拒）
                    if (!banned.Contains(s.Liquid)) banned.Add(s.Liquid);
                    break;

                case SuppressModifier s when string.IsNullOrEmpty(s.Liquid)
                    && !string.IsNullOrEmpty(s.Shape) && s.Operation is null:
                    // 可表达（P4d 升级）：**整个形状被禁**（不限操作）等价于"这些罐子不在池子里" ⇒
                    // 把该形状的罐子从求解池里移除即可**精确判定**（不是保守退化成未判定）。
                    if (!removedShapes.Contains(s.Shape)) removedShapes.Add(s.Shape);
                    break;

                default:
                    // 不可表达（例：抑制某个**形状**、抑制 MATCH/TRANSFER/GREEDY、Require 这类额外条件）
                    // ⇒ 如实回"未判定"，绝不说"不可行"。
                    _inconclusive = "当前规则含求解器无法表达的限制：" + m.Name;
                    return new SolverResult { Feasible = false, Stats = new SolverStats() };
            }
        }

        var projected = Project(workshopInfo, banned, granted);
        var pool = removedShapes.Count == 0
            ? jars
            : jars.Where(j => !removedShapes.Contains(j.Shape)).ToList();
        return _inner.Solve(requirements, projected, pool, liquids, transferBudget);
    }

    /// <summary>
    /// 前置投影：把"被抑制的液体"折进禁令表、把"被授予的形状"折进附加标记。
    /// **不修改调用方传进来的对象**（求解器看到的是一份新视图，调用方的规则视图不受影响）。
    /// </summary>
    private static WorkshopInfo Project(WorkshopInfo info, List<string> bannedLiquids, List<string> grantedShapes)
    {
        if (bannedLiquids.Count == 0 && grantedShapes.Count == 0) return info;

        var baseMarks = info.Capability.BaseMarks;
        var additional = info.Capability.AdditionalMarks;
        var ban = info.Capability.Ban;

        var newAdditional = new List<string>(additional);
        foreach (var s in grantedShapes)
            if (!newAdditional.Contains(s) && !baseMarks.Contains(s)) newAdditional.Add(s);

        var newBan = new List<string>(ban);
        foreach (var l in bannedLiquids)
            if (!newBan.Contains(l)) newBan.Add(l);

        return new WorkshopInfo
        {
            Capability = new WorkshopCapability(baseMarks, newAdditional, newBan),
            MaxJars = info.MaxJars,
            MaxLiquids = info.MaxLiquids
        };
    }
}

using System;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Config;
using JarSystem.Core.Orders;
using JarSystem.Core.Solver;
using JarSystem.Core.World.Demands;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 当前罐子玩法的订单规格适配器。
/// A/B 类均经规则候选、物理 Jar 绑定、有序资源重放与 Manifest 物化。PartitionSearch 仅保留为差分对照器。
/// 输入只来自 WorldSnapshot；不会读取实时 Workshop、Jar 或 ModifierStack。
/// </summary>
public sealed class JarOrderSpecificationSolver : IWorldOrderCandidateBookAwareSolver
{
    private readonly ISolver _innerSolver;

    public Type SpecificationType => typeof(JarOrderSpecification);
    public int NodeBudget { get; set; } = SolverLimits.DefaultNodeBudget;

    public JarOrderSpecificationSolver(ISolver innerSolver)
    {
        _innerSolver = innerSolver ?? throw new ArgumentNullException(nameof(innerSolver));
    }

    public WorldOrderSolveResult Solve(OrderDemand demand, WorldSnapshot snapshot) =>
        SolveCore(demand, snapshot, null);

    public WorldOrderSolveResult Solve(
        OrderDemand demand,
        WorldSnapshot snapshot,
        WorldOrderRuleCandidateBook candidateBook) =>
        SolveCore(demand, snapshot, candidateBook);

    /// <summary>
    /// Runs the retained legacy B-class PartitionSearch path for differential tests only.
    /// Production Solve() always uses the concrete 9l branch enumerator.
    /// </summary>
    public WorldOrderSolveResult SolvePartitionOracleForDifferentialTesting(
        OrderDemand demand,
        WorldSnapshot snapshot) =>
        WorldOrderPartitionBClassOracle.Solve(demand, snapshot, _innerSolver, NodeBudget);

    private WorldOrderSolveResult SolveCore(
        OrderDemand demand,
        WorldSnapshot snapshot,
        WorldOrderRuleCandidateBook? candidateBook)
    {
        if (demand.Specification is not JarOrderSpecification specification)
            return Result(demand, snapshot, WorldOrderSolveStatus.UnsupportedSpecification,
                reason: "罐子求解器收到不匹配的规格类型");

        if (snapshot.Master == null)
            return Result(demand, snapshot, WorldOrderSolveStatus.InvalidWorld,
                reason: "当前世界没有主工作台");

        if (specification.Type == Constants.OrderType.A)
        {
            var masterProfile = candidateBook?.GetOrBuild(
                    snapshot, snapshot.Master, "Master", specification)
                ?? WorldOrderRuleCandidateCatalog.Build(
                    snapshot, snapshot.Master, "Master", specification);
            var symbolicSolutions = candidateBook?.GetOrBuildSolutions(
                new[] { masterProfile }, maxSolutions: 256, maxNodes: 100_000);
            var enumeration = WorldOrderAClassBranchEnumerator.Enumerate(
                demand.Id, masterProfile, snapshot,
                maxBranches: 256,
                maxNodes: Math.Max(1, NodeBudget),
                precomputedSolutions: symbolicSolutions);

            if (enumeration.Status == WorldOrderAClassBranchEnumerationStatus.Feasible)
                return enumeration.Assembly?.SolveResult
                    ?? Result(demand, snapshot, WorldOrderSolveStatus.Undetermined,
                        "9l_concrete_a", "A 类分支报告可行但未返回具体计划");

            return Result(demand, snapshot,
                enumeration.Status == WorldOrderAClassBranchEnumerationStatus.Infeasible
                    ? WorldOrderSolveStatus.Infeasible
                    : WorldOrderSolveStatus.Undetermined,
                "9l_concrete_a",
                enumeration.Reason ?? "A 类具体规则分支没有得到确定结论");
        }

        if (snapshot.Slave == null)
            return Result(demand, snapshot, WorldOrderSolveStatus.InvalidWorld,
                reason: "B 类订单需要主台和从台两个岗位");
        if (snapshot.Slave.Jars.Count != 0 ||
            snapshot.Slave.Liquids.Values.Any(amount => amount != 0))
            return Result(demand, snapshot, WorldOrderSolveStatus.InvalidWorld,
                reason: "B 类订单开始求解时，从台必须为空");

        var masterProfile = candidateBook?.GetOrBuild(
                snapshot, snapshot.Master, "Master", specification)
            ?? WorldOrderRuleCandidateCatalog.Build(
                snapshot, snapshot.Master, "Master", specification);
        var slaveProfile = candidateBook?.GetOrBuild(
                snapshot, snapshot.Slave, "Slave", specification)
            ?? WorldOrderRuleCandidateCatalog.Build(
                snapshot, snapshot.Slave, "Slave", specification);
        var maxNodes = Math.Max(1, NodeBudget);
        var precomputedSolutions = candidateBook?.GetOrBuildSolutions(
            new[] { masterProfile, slaveProfile },
            maxSolutions: 256,
            maxNodes: maxNodes);
        var enumeration = WorldOrderBClassBranchEnumerator.Enumerate(
            demand.Id, masterProfile, slaveProfile, snapshot,
            maxBranches: 256,
            maxNodes: maxNodes,
            precomputedSolutions: precomputedSolutions);

        if (enumeration.Status == WorldOrderBClassBranchEnumerationStatus.Feasible)
            return enumeration.Assembly?.SolveResult
                ?? Result(demand, snapshot, WorldOrderSolveStatus.Undetermined,
                    "9l_concrete_b", "B 类分支报告可行但未返回具体计划");

        return Result(demand, snapshot,
            enumeration.Status == WorldOrderBClassBranchEnumerationStatus.Infeasible
                ? WorldOrderSolveStatus.Infeasible
                : WorldOrderSolveStatus.Undetermined,
            "9l_concrete_b",
            enumeration.Reason ?? "B 类具体规则分支没有得到确定结论");
    }

    private static WorldOrderSolveResult Result(
        OrderDemand demand,
        WorldSnapshot snapshot,
        WorldOrderSolveStatus status,
        string? method = null,
        string? reason = null,
        int directCount = 0,
        int operationCount = 0,
        WorldOrderSolvePlan? plan = null,
        PartitionSearchReport? partitionReport = null) =>
        new(demand.Id, snapshot.Version, status, method, reason, directCount, operationCount, plan, partitionReport,
            snapshot.RuleSetVersion, snapshot.WorkshopProfileVersion, snapshot.ResourceVersion,
            demand.Specification is JarOrderSpecification specification
                ? WorldOrderRuleCandidateCatalog.Fingerprint(specification)
                : string.Empty);

}

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Config;
using JarSystem.Core.Orders;
using JarSystem.Core.Solver;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// Legacy B-class PartitionSearch path retained only as a differential oracle.
/// It is not the production B-class solver entry point.
/// </summary>
public static class WorldOrderPartitionBClassOracle
{
    public static WorldOrderSolveResult Solve(
        OrderDemand demand,
        WorldSnapshot snapshot,
        ISolver innerSolver,
        int nodeBudget = SolverLimits.DefaultNodeBudget)
    {
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(innerSolver);
        if (demand.Specification is not JarOrderSpecification specification)
            return Result(demand, snapshot, WorldOrderSolveStatus.UnsupportedSpecification,
                reason: "罐子求解器收到不匹配的规格类型");
        if (specification.Type != Constants.OrderType.B)
            return Result(demand, snapshot, WorldOrderSolveStatus.UnsupportedSpecification,
                reason: "PartitionSearch 对照器只接受 B 类订单");
        if (snapshot.Master == null)
            return Result(demand, snapshot, WorldOrderSolveStatus.InvalidWorld,
                reason: "当前世界没有主工作台");
        var masterInfo = snapshot.Master.CreateWorkshopInfo();
        var slaveInfo = snapshot.Slave?.CreateWorkshopInfo();
        var requirements = RequirementOrdering.FullFirst(specification.Requirements);

        // PartitionSearch remains the B-class differential oracle until the full B-class
        // concrete branch enumerator and entry-point parity suite are complete.
        var snapshotSolver = new SnapshotProjectedSolver(
            _innerSolver, masterInfo, snapshot.Master, slaveInfo, snapshot.Slave);
        if (snapshotSolver is IWorkBudgetedSolver budgeted)
            budgeted.BeginCheck(nodeBudget);

        var slaveSnapshot = snapshot.Slave;
        if (slaveSnapshot == null || slaveInfo == null)
            return Result(demand, snapshot, WorldOrderSolveStatus.InvalidWorld,
                reason: "B 类订单需要主台和从台两个岗位");

        if (slaveSnapshot.Jars.Count != 0 || slaveSnapshot.Liquids.Values.Any(amount => amount != 0))
            return Result(demand, snapshot, WorldOrderSolveStatus.InvalidWorld,
                reason: "B 类订单开始求解时，从台必须为空");

        var partition = new PartitionSearch(snapshotSolver);
        var outcome = partition.Search(new PartitionSearchRequest
        {
            Requirements = requirements,
            MasterInfo = masterInfo,
            SlaveInfo = slaveInfo,
            MasterJars = snapshot.Master.CreateSolverJars(),
            MasterLiquids = CreateMasterSupplyLiquids(snapshot)
        }, out var blockage, out var inconclusiveReason, out var partitionReport);

        if (snapshotSolver.BudgetExceeded || inconclusiveReason != null)
            return Result(demand, snapshot, WorldOrderSolveStatus.Undetermined,
                "partition", inconclusiveReason ?? snapshotSolver.InconclusiveReason ?? "分析超出预算",
                partitionReport: partitionReport);

        if (outcome == null)
            return Result(demand, snapshot, WorldOrderSolveStatus.Infeasible,
                "partition", blockage ?? "没有找到可行的主从需求划分",
                partitionReport: partitionReport);

        return Result(demand, snapshot, WorldOrderSolveStatus.Feasible,
            "partition", directCount: outcome.DirectCount,
            operationCount: outcome.OperationCount,
            plan: WorldOrderSolvePlan.Partitioned(outcome),
            partitionReport: partitionReport);
    }

    private static Dictionary<string, int> CreateMasterSupplyLiquids(WorldSnapshot snapshot)
    {
        var balances = snapshot.ShipLiquidInventory == null
            ? snapshot.Master!.CreateSolverLiquids()
            : snapshot.ShipLiquidInventory.Balances.ToDictionary(
                item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var item in snapshot.MasterPrefillLiquidBalances)
            balances[item.Key] = checked(balances.GetValueOrDefault(item.Key) + item.Value);
        return balances;
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

    /// <summary>
    /// 把快照中的静态修饰器投影到一次求解输入。
    /// 规则无法精确表达时中止为未判定；绝不忽略规则后给出错误的可行/不可行结论。
    /// </summary>
    private sealed class SnapshotProjectedSolver : ISolver, IWorkBudgetedSolver
    {
        private readonly ISolver _inner;
        private readonly WorkshopInfo _masterInfo;
        private readonly WorkshopSolveSnapshot _master;
        private readonly WorkshopInfo? _slaveInfo;
        private readonly WorkshopSolveSnapshot? _slave;
        private string? _inconclusive;

        public SnapshotProjectedSolver(
            ISolver inner,
            WorkshopInfo masterInfo,
            WorkshopSolveSnapshot master,
            WorkshopInfo? slaveInfo,
            WorkshopSolveSnapshot? slave)
        {
            _inner = inner;
            _masterInfo = masterInfo;
            _master = master;
            _slaveInfo = slaveInfo;
            _slave = slave;
        }

        public bool BudgetExceeded =>
            _inconclusive != null || (_inner as IWorkBudgetedSolver)?.BudgetExceeded == true;

        public string? InconclusiveReason =>
            _inconclusive ?? (_inner as IWorkBudgetedSolver)?.InconclusiveReason;

        public void BeginCheck(int nodeBudget)
        {
            _inconclusive = null;
            (_inner as IWorkBudgetedSolver)?.BeginCheck(nodeBudget);
        }

        public SolverResult Solve(
            List<Requirement> requirements,
            WorkshopInfo workshopInfo,
            List<JarState> jars,
            Dictionary<string, int> liquids,
            int transferBudget = int.MaxValue)
        {
            WorkshopSolveSnapshot? source;
            if (ReferenceEquals(workshopInfo, _masterInfo))
                source = _master;
            else if (_slaveInfo != null && ReferenceEquals(workshopInfo, _slaveInfo))
                source = _slave;
            else
            {
                _inconclusive = "求解请求没有对应的世界快照工作台";
                return new SolverResult { Feasible = false };
            }

            if (source == null)
            {
                _inconclusive = "求解请求对应的工作台快照不存在";
                return new SolverResult { Feasible = false };
            }

            if (source.HasOpaqueSolverRules)
            {
                _inconclusive = "工作台包含无法静态投影的规则";
                return new SolverResult { Feasible = false };
            }

            // WorkshopSolveSnapshot 已经重建出只读 RuleContext。
            // 规则在规则引擎内精确求值，不再被压扁成 Ban/AdditionalMarks，
            // 更不能把“禁止某形状操作”错误地解释成“删除该形状的 Jar”。
            return _inner.Solve(requirements, workshopInfo, jars, liquids, transferBudget);
        }
    }
}

using System;
using System.Collections.Generic;
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
/// A 类在主台单阶段求解；B 类复用已验证的 PartitionSearch，在主/从台快照上做需求划分。
/// 输入只来自 WorldSnapshot；不会读取实时 Workshop、Jar 或 ModifierStack。
/// </summary>
public sealed class JarOrderSpecificationSolver : IWorldOrderSpecificationSolver
{
    private readonly ISolver _innerSolver;

    public Type SpecificationType => typeof(JarOrderSpecification);
    public int NodeBudget { get; set; } = SolverLimits.DefaultNodeBudget;

    public JarOrderSpecificationSolver(ISolver innerSolver)
    {
        _innerSolver = innerSolver ?? throw new ArgumentNullException(nameof(innerSolver));
    }

    public WorldOrderSolveResult Solve(OrderDemand demand, WorldSnapshot snapshot)
    {
        if (demand.Specification is not JarOrderSpecification specification)
            return Result(demand, snapshot, WorldOrderSolveStatus.UnsupportedSpecification,
                reason: "罐子求解器收到不匹配的规格类型");

        if (snapshot.Master == null)
            return Result(demand, snapshot, WorldOrderSolveStatus.InvalidWorld,
                reason: "当前世界没有主工作台");

        var masterInfo = snapshot.Master.CreateWorkshopInfo();
        var slaveInfo = snapshot.Slave?.CreateWorkshopInfo();
        var snapshotSolver = new SnapshotProjectedSolver(
            _innerSolver, masterInfo, snapshot.Master, slaveInfo, snapshot.Slave);

        if (snapshotSolver is IWorkBudgetedSolver budgeted)
            budgeted.BeginCheck(NodeBudget);

        var requirements = RequirementOrdering.FullFirst(specification.Requirements);

        if (specification.Type == Constants.OrderType.A)
        {
            var solved = snapshotSolver.Solve(
                requirements,
                masterInfo,
                snapshot.Master.CreateSolverJars(),
                CreateMasterSupplyLiquids(snapshot));

            if (snapshotSolver.BudgetExceeded)
                return Result(demand, snapshot, WorldOrderSolveStatus.Undetermined,
                    "single_phase", snapshotSolver.InconclusiveReason ?? "分析超出预算");

            if (!solved.Feasible)
                return Result(demand, snapshot, WorldOrderSolveStatus.Infeasible,
                    "single_phase", solved.Blockage);

            if (solved.Path == null)
                return Result(demand, snapshot, WorldOrderSolveStatus.Undetermined,
                    "single_phase", "求解器报告可行，但没有提供可执行路径");

            var path = solved.Path;
            return Result(demand, snapshot, WorldOrderSolveStatus.Feasible,
                "single_phase", directCount: path.Count(x => x.Operation?.Type == "MATCH"),
                operationCount: path.Count(x => x.Operation?.Type != "MATCH"),
                plan: WorldOrderSolvePlan.SinglePhase(path));
        }

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
        }, out var blockage, out var inconclusiveReason);

        if (snapshotSolver.BudgetExceeded || inconclusiveReason != null)
            return Result(demand, snapshot, WorldOrderSolveStatus.Undetermined,
                "partition", inconclusiveReason ?? snapshotSolver.InconclusiveReason ?? "分析超出预算");

        if (outcome == null)
            return Result(demand, snapshot, WorldOrderSolveStatus.Infeasible,
                "partition", blockage ?? "没有找到可行的主从需求划分");

        return Result(demand, snapshot, WorldOrderSolveStatus.Feasible,
            "partition", directCount: outcome.DirectCount,
            operationCount: outcome.OperationCount,
            plan: WorldOrderSolvePlan.Partitioned(outcome));
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
        WorldOrderSolvePlan? plan = null) =>
        new(demand.Id, snapshot.Version, status, method, reason, directCount, operationCount, plan);

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

            var additionalMarks = new List<string>(workshopInfo.Capability.AdditionalMarks);
            var bannedLiquids = new List<string>(workshopInfo.Capability.Ban);
            var removedShapes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var modifier in source.SolverModifiers)
            {
                if (modifier.IsOpaque)
                {
                    _inconclusive = "存在无法表达的规则: " + modifier.Name;
                    return new SolverResult { Feasible = false };
                }

                if (modifier.RuntimeType.EndsWith(nameof(GrantModifier), StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(modifier.Shape))
                    {
                        _inconclusive = "附加标记规则缺少形状";
                        return new SolverResult { Feasible = false };
                    }
                    if (!additionalMarks.Contains(modifier.Shape) &&
                        !workshopInfo.Capability.BaseMarks.Contains(modifier.Shape))
                        additionalMarks.Add(modifier.Shape);
                    continue;
                }

                if (modifier.RuntimeType.EndsWith(nameof(SuppressModifier), StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(modifier.Operation) && string.IsNullOrEmpty(modifier.Shape) &&
                        !string.IsNullOrEmpty(modifier.Liquid))
                    {
                        if (!bannedLiquids.Contains(modifier.Liquid))
                            bannedLiquids.Add(modifier.Liquid);
                        continue;
                    }

                    if (string.IsNullOrEmpty(modifier.Operation) && string.IsNullOrEmpty(modifier.Liquid) &&
                        !string.IsNullOrEmpty(modifier.Shape))
                    {
                        removedShapes.Add(modifier.Shape);
                        continue;
                    }

                    _inconclusive = "存在无法精确投影的抑制规则: " + modifier.Name;
                    return new SolverResult { Feasible = false };
                }

                _inconclusive = "存在未知求解规则: " + modifier.Name;
                return new SolverResult { Feasible = false };
            }

            var projectedInfo = new WorkshopInfo
            {
                Capability = new WorkshopCapability(
                    workshopInfo.Capability.BaseMarks,
                    additionalMarks,
                    bannedLiquids),
                MaxJars = workshopInfo.MaxJars,
                MaxLiquids = workshopInfo.MaxLiquids,
                MaxLiquidCapacity = workshopInfo.MaxLiquidCapacity
            };

            var projectedJars = removedShapes.Count == 0
                ? jars
                : jars.Where(j => !removedShapes.Contains(j.Shape)).ToList();

            return _inner.Solve(requirements, projectedInfo, projectedJars, liquids, transferBudget);
        }
    }
}

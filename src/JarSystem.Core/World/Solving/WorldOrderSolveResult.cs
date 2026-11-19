using System;
using System.Collections.Generic;
using JarSystem.Core.Domain;
using JarSystem.Core.World.Demands;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 世界订单的一次求解结论。它只描述分析结论，不代表资源预留或执行承诺。
/// 每个结果都绑定需求 ID 与输入快照版本，调用方不得把旧版本结论当作当前方案。
/// </summary>
public sealed class WorldOrderSolveResult
{
    public int DemandId { get; }
    public string WorldVersion { get; }
    public WorldOrderSolveStatus Status { get; }
    public string? CheckMethod { get; }
    public string? Reason { get; }
    public int DirectCount { get; }
    public int OperationCount { get; }

    public bool IsFeasible => Status == WorldOrderSolveStatus.Feasible;
    public bool IsUndetermined => Status == WorldOrderSolveStatus.Undetermined;

    public WorldOrderSolveResult(
        int demandId,
        string worldVersion,
        WorldOrderSolveStatus status,
        string? checkMethod = null,
        string? reason = null,
        int directCount = 0,
        int operationCount = 0)
    {
        if (demandId <= 0) throw new ArgumentOutOfRangeException(nameof(demandId));
        if (string.IsNullOrWhiteSpace(worldVersion))
            throw new ArgumentException("世界版本不能为空", nameof(worldVersion));
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (directCount < 0) throw new ArgumentOutOfRangeException(nameof(directCount));
        if (operationCount < 0) throw new ArgumentOutOfRangeException(nameof(operationCount));

        DemandId = demandId;
        WorldVersion = worldVersion;
        Status = status;
        CheckMethod = checkMethod;
        Reason = reason;
        DirectCount = directCount;
        OperationCount = operationCount;
    }
}

public enum WorldOrderSolveStatus
{
    Feasible,
    Infeasible,
    Undetermined,
    UnsupportedSpecification,
    InvalidWorld
}

/// <summary>一个玩法规格对应一个求解适配器。</summary>
public interface IWorldOrderSpecificationSolver
{
    Type SpecificationType { get; }
    WorldOrderSolveResult Solve(OrderDemand demand, WorldSnapshot snapshot);
}

/// <summary>
/// 订单规格求解器注册表。按规格的精确运行时类型分派，不让通用订单板解释玩法字段。
/// 不做基类/接口模糊匹配，避免注册顺序改变分派结果。
/// </summary>
public sealed class WorldOrderSolverRegistry
{
    private readonly Dictionary<Type, IWorldOrderSpecificationSolver> _solvers = new();

    public WorldOrderSolverRegistry(IEnumerable<IWorldOrderSpecificationSolver>? solvers = null)
    {
        if (solvers == null) return;
        foreach (var solver in solvers) Register(solver);
    }

    public void Register(IWorldOrderSpecificationSolver solver)
    {
        if (solver == null) throw new ArgumentNullException(nameof(solver));
        if (solver.SpecificationType == null)
            throw new ArgumentException("求解器必须声明规格类型", nameof(solver));
        if (!typeof(IOrderSpecification).IsAssignableFrom(solver.SpecificationType))
            throw new ArgumentException("注册类型必须实现 IOrderSpecification", nameof(solver));
        if (!_solvers.TryAdd(solver.SpecificationType, solver))
            throw new InvalidOperationException("订单规格求解器重复注册: " + solver.SpecificationType.FullName);
    }

    public WorldOrderSolveResult Solve(OrderDemand demand, WorldSnapshot snapshot)
    {
        if (demand == null) throw new ArgumentNullException(nameof(demand));
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

        if (!_solvers.TryGetValue(demand.Specification.GetType(), out var solver))
            return new WorldOrderSolveResult(
                demand.Id, snapshot.Version, WorldOrderSolveStatus.UnsupportedSpecification,
                reason: "没有为订单规格注册求解器: " + demand.Specification.GetType().FullName);

        return solver.Solve(demand, snapshot);
    }
}

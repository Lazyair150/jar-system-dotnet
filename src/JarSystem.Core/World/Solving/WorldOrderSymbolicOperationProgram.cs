using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JarSystem.Core.World.Solving;

public enum WorldOrderSymbolicOperationKind
{
    Rule,
    MasterToSlaveHandoff
}

public enum WorldOrderSymbolicDependencyKind
{
    JarState,
    FreeLiquid
}

/// <summary>
/// An abstract version of one physical Jar. JarKey preserves identity while Revision
/// distinguishes successive states of that same Jar in an operation program.
/// </summary>
public sealed record WorldOrderSymbolicJarVersion
{
    public string JarKey { get; }
    public int Revision { get; }
    public string WorkshopRole { get; }
    public string Shape { get; }
    public string? Liquid { get; }

    public WorldOrderSymbolicJarVersion(
        string jarKey, int revision, string workshopRole, string shape, string? liquid)
    {
        if (string.IsNullOrWhiteSpace(jarKey)) throw new ArgumentException("Jar 符号身份不能为空", nameof(jarKey));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(workshopRole)) throw new ArgumentException("工作台角色不能为空", nameof(workshopRole));
        if (string.IsNullOrWhiteSpace(shape)) throw new ArgumentException("Jar 形状不能为空", nameof(shape));
        JarKey = jarKey;
        Revision = revision;
        WorkshopRole = workshopRole;
        Shape = shape;
        Liquid = liquid;
    }
}

/// <summary>
/// One ordered symbolic operation. Rule nodes transform one Jar version; the B-class
/// handoff node is a single batch boundary that may move any number of Jar identities.
/// It deliberately carries no bound resource amount or live Jar ID.
/// </summary>
public sealed class WorldOrderSymbolicOperationNode
{
    public string NodeId { get; }
    public int Sequence { get; }
    public WorldOrderSymbolicOperationKind Kind { get; }
    public int? RequirementIndex { get; }
    public WorldOrderRuleCandidate? Candidate { get; }
    public string? WorkshopRole { get; }
    public string? FromRole { get; }
    public string? ToRole { get; }
    public IReadOnlyList<WorldOrderSymbolicJarVersion> InputJars { get; }
    public IReadOnlyList<WorldOrderSymbolicJarVersion> OutputJars { get; }

    private WorldOrderSymbolicOperationNode(
        string nodeId,
        int sequence,
        WorldOrderSymbolicOperationKind kind,
        int? requirementIndex,
        WorldOrderRuleCandidate? candidate,
        string? workshopRole,
        string? fromRole,
        string? toRole,
        IEnumerable<WorldOrderSymbolicJarVersion> inputJars,
        IEnumerable<WorldOrderSymbolicJarVersion> outputJars)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("操作节点身份不能为空", nameof(nodeId));
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        NodeId = nodeId;
        Sequence = sequence;
        Kind = kind;
        RequirementIndex = requirementIndex;
        Candidate = candidate;
        WorkshopRole = workshopRole;
        FromRole = fromRole;
        ToRole = toRole;
        InputJars = Array.AsReadOnly(inputJars.ToArray());
        OutputJars = Array.AsReadOnly(outputJars.ToArray());
    }

    public static WorldOrderSymbolicOperationNode Rule(
        string nodeId,
        int sequence,
        string workshopRole,
        int requirementIndex,
        WorldOrderRuleCandidate candidate,
        WorldOrderSymbolicJarVersion input,
        WorldOrderSymbolicJarVersion output)
    {
        if (string.IsNullOrWhiteSpace(workshopRole)) throw new ArgumentException("工作台角色不能为空", nameof(workshopRole));
        if (requirementIndex < 0) throw new ArgumentOutOfRangeException(nameof(requirementIndex));
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (input.WorkshopRole != workshopRole || output.WorkshopRole != workshopRole)
            throw new ArgumentException("规则操作的输入和输出 Jar 必须属于执行岗位");
        if (input.JarKey != output.JarKey || output.Revision != checked(input.Revision + 1))
            throw new ArgumentException("规则操作必须推进同一 Jar 身份的一个状态版本");
        if (candidate.RequirementIndex != requirementIndex ||
            candidate.SourceShape != input.Shape || candidate.SourceLiquid != input.Liquid ||
            candidate.TargetShape != output.Shape || candidate.TargetLiquid != output.Liquid)
            throw new ArgumentException("规则候选与输入/输出 Jar 事实不一致");

        return new WorldOrderSymbolicOperationNode(
            nodeId, sequence, WorldOrderSymbolicOperationKind.Rule,
            requirementIndex, candidate, workshopRole, null, null,
            new[] { input }, new[] { output });
    }

    public static WorldOrderSymbolicOperationNode MasterToSlaveHandoff(
        string nodeId,
        int sequence,
        string masterRole,
        string slaveRole,
        IEnumerable<WorldOrderSymbolicJarVersion> inputJars)
    {
        if (string.IsNullOrWhiteSpace(masterRole)) throw new ArgumentException("主台角色不能为空", nameof(masterRole));
        if (string.IsNullOrWhiteSpace(slaveRole)) throw new ArgumentException("从台角色不能为空", nameof(slaveRole));
        if (masterRole == slaveRole) throw new ArgumentException("主台和从台角色必须不同");
        if (inputJars == null) throw new ArgumentNullException(nameof(inputJars));

        var inputs = inputJars.ToArray();
        if (inputs.Any(x => x == null)) throw new ArgumentException("交接 Jar 不能包含 null", nameof(inputJars));
        if (inputs.Any(x => x.WorkshopRole != masterRole))
            throw new ArgumentException("交接输入 Jar 必须全部属于主台", nameof(inputJars));
        if (inputs.Select(x => x.JarKey).Distinct(StringComparer.Ordinal).Count() != inputs.Length)
            throw new ArgumentException("同一批交接不能重复包含同一 Jar 身份", nameof(inputJars));

        var outputs = inputs.Select(x => new WorldOrderSymbolicJarVersion(
            x.JarKey, checked(x.Revision + 1), slaveRole, x.Shape, x.Liquid)).ToArray();

        return new WorldOrderSymbolicOperationNode(
            nodeId, sequence, WorldOrderSymbolicOperationKind.MasterToSlaveHandoff,
            null, null, null, masterRole, slaveRole, inputs, outputs);
    }
}

/// <summary>
/// Explicit edge between two operations. JarState edges identify the exact Jar version;
/// FreeLiquid edges identify a liquid dependency and may receive an exact amount later.
/// </summary>
public sealed record WorldOrderSymbolicOperationDependency
{
    public string FromNodeId { get; }
    public string ToNodeId { get; }
    public WorldOrderSymbolicDependencyKind Kind { get; }
    public string? JarKey { get; }
    public int? JarRevision { get; }
    public string? Liquid { get; }
    public int? Amount { get; }

    private WorldOrderSymbolicOperationDependency(
        string fromNodeId, string toNodeId, WorldOrderSymbolicDependencyKind kind,
        string? jarKey, int? jarRevision, string? liquid, int? amount)
    {
        if (string.IsNullOrWhiteSpace(fromNodeId)) throw new ArgumentException("依赖源节点不能为空", nameof(fromNodeId));
        if (string.IsNullOrWhiteSpace(toNodeId)) throw new ArgumentException("依赖目标节点不能为空", nameof(toNodeId));
        if (fromNodeId == toNodeId) throw new ArgumentException("操作节点不能依赖自身");
        if (amount is <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        FromNodeId = fromNodeId;
        ToNodeId = toNodeId;
        Kind = kind;
        JarKey = jarKey;
        JarRevision = jarRevision;
        Liquid = liquid;
        Amount = amount;
    }

    public static WorldOrderSymbolicOperationDependency JarState(
        string fromNodeId, string toNodeId, string jarKey, int jarRevision) =>
        new(fromNodeId, toNodeId, WorldOrderSymbolicDependencyKind.JarState,
            string.IsNullOrWhiteSpace(jarKey) ? throw new ArgumentException("Jar 身份不能为空", nameof(jarKey)) : jarKey,
            jarRevision < 0 ? throw new ArgumentOutOfRangeException(nameof(jarRevision)) : jarRevision,
            null, null);

    public static WorldOrderSymbolicOperationDependency FreeLiquid(
        string fromNodeId, string toNodeId, string liquid, int? amount = null) =>
        new(fromNodeId, toNodeId, WorldOrderSymbolicDependencyKind.FreeLiquid,
            null, null,
            string.IsNullOrWhiteSpace(liquid) ? throw new ArgumentException("液体名称不能为空", nameof(liquid)) : liquid,
            amount);
}

/// <summary>
/// Immutable ordered symbolic program. A-class programs have no handoff; B-class programs
/// have exactly one Master-to-Slave handoff node. This validates structure, not resource
/// sufficiency, staging peaks, exact transfer amounts, or physical Jar binding.
/// </summary>
public sealed class WorldOrderSymbolicOperationProgram
{
    public bool IsBClass { get; }
    public string MasterRole { get; }
    public string SlaveRole { get; }
    public IReadOnlyList<WorldOrderSymbolicOperationNode> Nodes { get; }
    public IReadOnlyList<WorldOrderSymbolicOperationDependency> Dependencies { get; }

    public WorldOrderSymbolicOperationProgram(
        bool isBClass,
        string masterRole,
        string slaveRole,
        IEnumerable<WorldOrderSymbolicOperationNode> nodes,
        IEnumerable<WorldOrderSymbolicOperationDependency> dependencies)
    {
        if (string.IsNullOrWhiteSpace(masterRole)) throw new ArgumentException("主台角色不能为空", nameof(masterRole));
        if (string.IsNullOrWhiteSpace(slaveRole)) throw new ArgumentException("从台角色不能为空", nameof(slaveRole));
        if (masterRole == slaveRole) throw new ArgumentException("主台和从台角色必须不同");
        if (nodes == null) throw new ArgumentNullException(nameof(nodes));
        if (dependencies == null) throw new ArgumentNullException(nameof(dependencies));

        var orderedNodes = nodes.OrderBy(x => x.Sequence).ToArray();
        if (orderedNodes.Any(x => x == null)) throw new ArgumentException("操作节点不能包含 null", nameof(nodes));
        if (orderedNodes.Select(x => x.NodeId).Distinct(StringComparer.Ordinal).Count() != orderedNodes.Length)
            throw new ArgumentException("操作节点身份不能重复", nameof(nodes));
        if (orderedNodes.Select(x => x.Sequence).Distinct().Count() != orderedNodes.Length)
            throw new ArgumentException("操作序号不能重复", nameof(nodes));

        var handoffs = orderedNodes.Where(x => x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff).ToArray();
        if (isBClass ? handoffs.Length != 1 : handoffs.Length != 0)
            throw new ArgumentException(isBClass
                ? "B 类订单必须且只能有一个主从资源交接节点"
                : "A 类订单不得包含主从资源交接节点", nameof(nodes));

        var handoff = handoffs.SingleOrDefault();
        foreach (var node in orderedNodes)
        {
            if (node.Kind == WorldOrderSymbolicOperationKind.Rule)
            {
                if (node.Candidate == null || node.WorkshopRole == null)
                    throw new ArgumentException("规则节点缺少候选或岗位", nameof(nodes));
                if (node.WorkshopRole != masterRole && node.WorkshopRole != slaveRole)
                    throw new ArgumentException("规则节点使用了未知岗位", nameof(nodes));
                if (!isBClass && node.WorkshopRole != masterRole)
                    throw new ArgumentException("A 类订单只能使用主台", nameof(nodes));
                if (isBClass && node.WorkshopRole == masterRole && node.Sequence >= handoff!.Sequence)
                    throw new ArgumentException("主台操作必须位于唯一交接节点之前", nameof(nodes));
                if (isBClass && node.WorkshopRole == slaveRole && node.Sequence <= handoff!.Sequence)
                    throw new ArgumentException("从台操作必须位于唯一交接节点之后", nameof(nodes));
            }
            else if (node.FromRole != masterRole || node.ToRole != slaveRole)
            {
                throw new ArgumentException("交接节点必须是主台到从台", nameof(nodes));
            }
        }

        var byId = orderedNodes.ToDictionary(x => x.NodeId, StringComparer.Ordinal);
        var edges = dependencies.ToArray();
        if (edges.Any(x => x == null)) throw new ArgumentException("依赖边不能包含 null", nameof(dependencies));
        if (edges.Select(x => (x.FromNodeId, x.ToNodeId, x.Kind, x.JarKey, x.JarRevision, x.Liquid))
            .Distinct().Count() != edges.Length)
            throw new ArgumentException("依赖边不能重复", nameof(dependencies));

        foreach (var edge in edges)
        {
            if (!byId.TryGetValue(edge.FromNodeId, out var from) ||
                !byId.TryGetValue(edge.ToNodeId, out var to))
                throw new ArgumentException("依赖边引用了不存在的操作节点", nameof(dependencies));
            if (from.Sequence >= to.Sequence)
                throw new ArgumentException("依赖边必须从较早操作指向较晚操作", nameof(dependencies));

            if (edge.Kind == WorldOrderSymbolicDependencyKind.JarState)
            {
                var produced = from.OutputJars.Any(x => x.JarKey == edge.JarKey && x.Revision == edge.JarRevision);
                var consumed = to.InputJars.Any(x => x.JarKey == edge.JarKey && x.Revision == edge.JarRevision);
                if (!produced || !consumed)
                    throw new ArgumentException("Jar 依赖边必须连接同一 Jar 的精确输出/输入版本", nameof(dependencies));
            }
            else if (edge.Kind == WorldOrderSymbolicDependencyKind.FreeLiquid &&
                     string.IsNullOrWhiteSpace(edge.Liquid))
            {
                throw new ArgumentException("游离液体依赖必须指定液体种类", nameof(dependencies));
            }
        }

        var allInputs = orderedNodes.SelectMany(x => x.InputJars).ToArray();
        if (allInputs.GroupBy(x => (x.JarKey, x.Revision)).Any(x => x.Count() > 1))
            throw new ArgumentException("同一 Jar 状态版本不能被多个操作重复消费", nameof(nodes));

        var allOutputs = orderedNodes.SelectMany(x => x.OutputJars).ToArray();
        if (allOutputs.GroupBy(x => (x.JarKey, x.Revision)).Any(x => x.Count() > 1))
            throw new ArgumentException("同一 Jar 状态版本不能由多个操作重复产生", nameof(nodes));

        foreach (var input in allInputs)
        {
            var producers = orderedNodes.Where(x =>
                x.OutputJars.Any(y => y.JarKey == input.JarKey && y.Revision == input.Revision)).ToArray();
            if (producers.Length > 1) throw new ArgumentException("Jar 状态版本存在多个生产者", nameof(nodes));
            if (producers.Length == 1 && !edges.Any(x =>
                x.Kind == WorldOrderSymbolicDependencyKind.JarState &&
                x.FromNodeId == producers[0].NodeId &&
                x.ToNodeId == orderedNodes.First(n => n.InputJars.Contains(input)).NodeId &&
                x.JarKey == input.JarKey && x.JarRevision == input.Revision))
                throw new ArgumentException("Jar 状态版本被后续操作使用时必须显式声明依赖边", nameof(dependencies));
        }

        IsBClass = isBClass;
        MasterRole = masterRole;
        SlaveRole = slaveRole;
        Nodes = Array.AsReadOnly(orderedNodes);
        Dependencies = Array.AsReadOnly(edges);
    }
}

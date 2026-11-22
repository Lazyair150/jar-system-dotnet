using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.World;

namespace JarSystem.Core.World.Solving;

public enum WorldOrderConcreteMaterializationStatus
{
    Complete,
    Infeasible,
    Undetermined
}

/// <summary>
/// Concrete SolverStep paths produced from a symbolic program and a physical Jar binding.
/// Complete means identity/state materialization succeeded; it is not a resource or
/// staging feasibility proof. The caller must still run ordered policy replay and Manifest validation.
/// </summary>
public sealed class WorldOrderConcreteMaterializationResult
{
    public WorldOrderConcreteMaterializationStatus Status { get; }
    public IReadOnlyList<SolverStep> MasterPath { get; }
    public IReadOnlyList<SolverStep> SlavePath { get; }
    public IReadOnlyList<int> TransferJarIds { get; }
    public IReadOnlyDictionary<string, int> TransferLiquids { get; }
    public string? Reason { get; }
    public bool IsComplete => Status == WorldOrderConcreteMaterializationStatus.Complete;

    internal WorldOrderConcreteMaterializationResult(
        WorldOrderConcreteMaterializationStatus status,
        IEnumerable<SolverStep>? masterPath,
        IEnumerable<SolverStep>? slavePath,
        IEnumerable<int>? transferJarIds,
        IReadOnlyDictionary<string, int>? transferLiquids,
        string? reason)
    {
        Status = status;
        MasterPath = Array.AsReadOnly((masterPath ?? Array.Empty<SolverStep>()).ToArray());
        SlavePath = Array.AsReadOnly((slavePath ?? Array.Empty<SolverStep>()).ToArray());
        TransferJarIds = Array.AsReadOnly((transferJarIds ?? Array.Empty<int>()).ToArray());
        TransferLiquids = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(transferLiquids ?? new Dictionary<string, int>(), StringComparer.Ordinal));
        Reason = reason;
    }
}

/// <summary>
/// Joins symbolic operation nodes to the physical Jar identities selected from the
/// initial Master inventory and emits concrete, role-local SolverStep snapshots.
/// It preserves symbolic node order and rule identity. It does not authorize operations,
/// simulate staging rejection/capacity, or replace WorldOrderResourceManifest.
/// </summary>
public static class WorldOrderConcreteOperationMaterializer
{
    public static WorldOrderConcreteMaterializationResult Materialize(
        WorldOrderSymbolicOperationProgram program,
        WorldOrderPhysicalJarBindingResult binding,
        WorldSnapshot world,
        WorldOrderRuleCandidateProfile masterProfile,
        WorldOrderRuleCandidateProfile? slaveProfile = null)
    {
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (binding == null) throw new ArgumentNullException(nameof(binding));
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (masterProfile == null) throw new ArgumentNullException(nameof(masterProfile));

        if (binding.Status == WorldOrderPhysicalJarBindingStatus.Undetermined)
            return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                binding.Reason ?? "物理 Jar 身份绑定未能确定");
        if (binding.Status != WorldOrderPhysicalJarBindingStatus.Complete)
            return Failure(WorldOrderConcreteMaterializationStatus.Infeasible,
                binding.Reason ?? "没有足够的初始主台物理 Jar");

        if (world.Master == null)
            return Failure(WorldOrderConcreteMaterializationStatus.Undetermined, "缺少主台快照");
        if (program.MasterRole != masterProfile.Role ||
            masterProfile.WorkshopName != world.Master.Name ||
            masterProfile.RuleSetVersion != world.RuleSetVersion ||
            masterProfile.WorkshopProfileVersion != world.WorkshopProfileVersion)
            return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                "主台候选 Profile 与当前世界快照不一致");

        if (program.IsBClass)
        {
            if (world.Slave == null || slaveProfile == null)
                return Failure(WorldOrderConcreteMaterializationStatus.Undetermined, "B 类物化需要从台快照和从台候选 Profile");
            if (world.Slave.Jars.Count != 0 || world.Slave.Liquids.Values.Any(x => x != 0))
                return Failure(WorldOrderConcreteMaterializationStatus.Infeasible, "B 类订单开始时从台必须为空");
            if (program.SlaveRole != slaveProfile.Role ||
                slaveProfile.WorkshopName != world.Slave.Name ||
                slaveProfile.RuleSetVersion != world.RuleSetVersion ||
                slaveProfile.WorkshopProfileVersion != world.WorkshopProfileVersion ||
                slaveProfile.SpecificationVersion != masterProfile.SpecificationVersion)
                return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                    "从台候选 Profile 与当前世界/订单规格不一致");
        }
        else if (slaveProfile != null)
        {
            return Failure(WorldOrderConcreteMaterializationStatus.Undetermined, "A 类订单不能提供从台候选 Profile");
        }

        if (masterProfile.SpecificationVersion.Length == 0 ||
            masterProfile.IsUndetermined || (program.IsBClass && slaveProfile!.IsUndetermined))
            return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                masterProfile.Reason ?? slaveProfile?.Reason ?? "规则候选 Profile 未确定");

        var nodes = program.Nodes.OrderBy(x => x.Sequence).ToArray();
        var ruleNodes = nodes.Where(x => x.Kind == WorldOrderSymbolicOperationKind.Rule).ToArray();
        foreach (var node in ruleNodes)
        {
            var candidate = node.Candidate!;
            var profile = node.WorkshopRole == program.MasterRole ? masterProfile : slaveProfile;
            if (profile == null || !ContainsCandidate(profile, candidate))
                return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                    "符号操作引用了不属于当前规则 Profile 的候选：" + node.NodeId);
        }

        var symbolicKeys = nodes.SelectMany(x => x.InputJars.Concat(x.OutputJars))
            .Select(x => x.JarKey).Distinct(StringComparer.Ordinal).ToArray();
        if (binding.JarIds.Count != symbolicKeys.Length ||
            symbolicKeys.Any(key => !binding.JarIds.ContainsKey(key)) ||
            binding.JarIds.Values.Any(id => id <= 0) ||
            binding.JarIds.Values.Distinct().Count() != binding.JarIds.Count)
            return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                "物理 Jar 绑定没有与符号 Jar 身份形成一一对应");

        var initialById = world.Master.Jars
            .Where(x => x.Id is > 0)
            .ToDictionary(x => x.Id!.Value, x => x);
        var physicalByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in symbolicKeys)
        {
            var id = binding.JarIds[key];
            if (!initialById.TryGetValue(id, out var actual))
                return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                    "绑定的物理 Jar 不属于初始主台库存：#" + id);
            var root = nodes.SelectMany(x => x.InputJars.Concat(x.OutputJars))
                .Where(x => x.JarKey == key).OrderBy(x => x.Revision).First();
            if (root.Revision != 0 || root.WorkshopRole != program.MasterRole ||
                root.IsMarked || actual.IsMarked || actual.Shape != root.Shape || actual.Liquid != root.Liquid)
                return Failure(WorldOrderConcreteMaterializationStatus.Infeasible,
                    "物理 Jar 初态与符号根状态不匹配：#" + id);
            physicalByKey.Add(key, id);
        }

        var handoffs = nodes.Where(x => x.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff).ToArray();
        var handoff = handoffs.SingleOrDefault();
        if (program.IsBClass != (handoff != null))
            return Failure(WorldOrderConcreteMaterializationStatus.Undetermined, "符号程序的交接结构与订单类型不一致");

        var currentSymbolic = nodes.SelectMany(x => x.InputJars.Concat(x.OutputJars))
            .GroupBy(x => x.JarKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => g.OrderBy(x => x.Revision).First(), StringComparer.Ordinal);
        var masterStates = world.Master.Jars.Select(x => x.ToSolverState()).ToList();
        var masterIndex = masterStates.Select((jar, index) => (jar, index))
            .ToDictionary(x => x.jar.Id!.Value, x => x.index);
        var slaveIds = handoff == null
            ? Array.Empty<int>()
            : handoff.InputJars.Select(x => physicalByKey[x.JarKey]).ToArray();
        if (slaveIds.Distinct().Count() != slaveIds.Length)
            return Failure(WorldOrderConcreteMaterializationStatus.Undetermined, "交接批次包含重复物理 Jar");
        // The Slave inventory is projected only at the handoff point, after every
        // Master operation has been replayed. This matters for a transferred Jar that
        // was operated on the Master: its mark and final state must cross the boundary.
        var slaveStates = new List<JarState>();
        var slaveIndex = new Dictionary<int, int>();

        var operatedPhysicalIds = new HashSet<int>();
        var masterPath = new List<SolverStep>();
        var slavePath = new List<SolverStep>();
        var stage = 0;

        foreach (var node in nodes)
        {
            if (node.Kind == WorldOrderSymbolicOperationKind.MasterToSlaveHandoff)
            {
                foreach (var version in node.InputJars)
                {
                    var id = physicalByKey[version.JarKey];
                    var actual = masterStates[masterIndex[id]];
                    if (!SymbolicMatches(version, actual))
                        return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                            "交接节点输入与主台实际 Jar 状态不一致：" + version.JarKey);
                    currentSymbolic[version.JarKey] = node.OutputJars.Single(x => x.JarKey == version.JarKey);
                }
                slaveStates = slaveIds.Select(id =>
                    JarState.Copy(masterStates[masterIndex[id]], masterOrigin: true)).ToList();
                slaveIndex = slaveStates.Select((jar, index) => (jar, index))
                    .ToDictionary(x => x.jar.Id!.Value, x => x.index);
                continue;
            }

            var candidate = node.Candidate!;
            var idForNode = physicalByKey[node.InputJars.Single().JarKey];
            var isMaster = node.WorkshopRole == program.MasterRole;
            var stateList = isMaster ? masterStates : slaveStates;
            var indexMap = isMaster ? masterIndex : slaveIndex;
            if (!indexMap.TryGetValue(idForNode, out var jarIndex))
                return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                    "操作节点使用的 Jar 没有进入对应岗位库存：" + node.NodeId);

            var input = node.InputJars.Single();
            var output = node.OutputJars.Single();
            var current = currentSymbolic[input.JarKey];
            var actualBefore = stateList[jarIndex];
            if (!SameVersion(current, input) || !SymbolicMatches(input, actualBefore) ||
                actualBefore.IsMarked || !operatedPhysicalIds.Add(idForNode))
                return Failure(WorldOrderConcreteMaterializationStatus.Infeasible,
                    "操作节点违反 Jar 当前版本或订单级一次操作约束：" + node.NodeId);

            var operation = CreateOperation(candidate, jarIndex, idForNode);
            if (operation == null)
                return Failure(WorldOrderConcreteMaterializationStatus.Undetermined,
                    "暂不支持物化该规则算子：" + candidate.Operation);

            var before = stateList.Select(x => JarState.Copy(x)).ToList();
            var after = stateList.Select(x => JarState.Copy(x)).ToList();
            after[jarIndex].Shape = output.Shape;
            after[jarIndex].Liquid = output.Liquid;
            after[jarIndex].IsMarked = true;
            after[jarIndex].MasterOrigin = !isMaster;
            stateList[jarIndex] = JarState.Copy(after[jarIndex]);
            currentSymbolic[input.JarKey] = output;

            var step = new SolverStep
            {
                Step = stage++,
                Requirement = node.RequirementIndex is int requirementIndex
                    ? new Requirement { Shape = candidate.TargetShape, Liquid = candidate.TargetLiquid }
                    : null,
                Operation = operation,
                Before = before,
                After = after,
                AfterLiquids = new Dictionary<string, int>(StringComparer.Ordinal)
            };
            (isMaster ? masterPath : slavePath).Add(step);
        }

        if (program.IsBClass && slaveIds.Any(id => !initialById.ContainsKey(id)))
            return Failure(WorldOrderConcreteMaterializationStatus.Undetermined, "从台交接 Jar 不属于初始主台库存");

        var slaveTransfer = RequiredInitialLiquids(slavePath);
        var masterRequired = RequiredInitialLiquids(masterPath, slaveTransfer);
        PopulateAfterLiquidSnapshots(masterPath, masterRequired);
        PopulateAfterLiquidSnapshots(slavePath, slaveTransfer);

        return new WorldOrderConcreteMaterializationResult(
            WorldOrderConcreteMaterializationStatus.Complete,
            masterPath, slavePath, slaveIds, slaveTransfer, null);
    }

    private static bool ContainsCandidate(WorldOrderRuleCandidateProfile profile, WorldOrderRuleCandidate candidate)
    {
        if (candidate.RequirementIndex < 0)
            return profile.PreparatoryOperations.Contains(candidate);
        return profile.Requirements.Any(x => x.RequirementIndex == candidate.RequirementIndex &&
            x.Candidates.Contains(candidate));
    }

    private static bool SameVersion(WorldOrderSymbolicJarVersion left, WorldOrderSymbolicJarVersion right) =>
        left.JarKey == right.JarKey && left.Revision == right.Revision &&
        left.WorkshopRole == right.WorkshopRole && left.Shape == right.Shape &&
        left.Liquid == right.Liquid && left.IsMarked == right.IsMarked;

    private static bool SymbolicMatches(WorldOrderSymbolicJarVersion expected, JarState actual) =>
        expected.Shape == actual.Shape && expected.Liquid == actual.Liquid &&
        expected.IsMarked == actual.IsMarked;

    private static SolverOperation? CreateOperation(
        WorldOrderRuleCandidate candidate, int jarIndex, int physicalJarId)
    {
        var consumed = new Dictionary<string, int>(StringComparer.Ordinal);
        string type;
        string? targetShape = null;
        string? targetLiquid = null;
        string? costLiquid = null;
        var fullReshape = false;

        switch (candidate.Operation)
        {
            case "MATCH":
                type = "MATCH";
                targetShape = candidate.TargetShape;
                targetLiquid = candidate.TargetLiquid;
                break;
            case "POUR_IN":
                if (candidate.SourceLiquid != null || candidate.TargetLiquid == null ||
                    candidate.ConsumedFreeLiquid != candidate.TargetLiquid)
                    return null;
                type = "POUR_IN";
                targetLiquid = candidate.TargetLiquid;
                consumed[targetLiquid] = 1;
                break;
            case "POUR_OUT":
            case "PREPARE_POUR_OUT":
                if (candidate.SourceLiquid == null || candidate.TargetLiquid != null)
                    return null;
                type = "POUR_OUT";
                break;
            case "RESHAPE_EMPTY":
                if (candidate.SourceLiquid != null || candidate.TargetLiquid != null ||
                    string.IsNullOrWhiteSpace(candidate.CostLiquid) ||
                    candidate.ConsumedFreeLiquid != candidate.CostLiquid)
                    return null;
                type = "RESHAPE";
                targetShape = candidate.TargetShape;
                costLiquid = candidate.CostLiquid;
                consumed[costLiquid] = 1;
                break;
            case "RESHAPE_FULL":
                if (candidate.SourceLiquid == null || candidate.TargetLiquid != null ||
                    candidate.ConsumedJarLiquid != candidate.SourceLiquid)
                    return null;
                type = "RESHAPE";
                targetShape = candidate.TargetShape;
                costLiquid = candidate.SourceLiquid;
                fullReshape = true;
                consumed[costLiquid] = 1;
                break;
            default:
                return null;
        }

        if (candidate.RuleVersion <= 0 || string.IsNullOrWhiteSpace(candidate.RuleId))
            return null;
        return new SolverOperation
        {
            Type = type,
            Description = candidate.RuleId + "@" + candidate.RuleVersion + " → " + candidate.Operation + " #" + physicalJarId,
            JarIndex = jarIndex,
            TargetShape = targetShape,
            TargetLiquid = targetLiquid,
            CostLiquid = costLiquid,
            Consumed = consumed,
            FullReshape = fullReshape,
            RuleId = candidate.RuleId,
            RuleVersion = candidate.RuleVersion
        };
    }

    private static Dictionary<string, int> RequiredInitialLiquids(
        IReadOnlyList<SolverStep> steps,
        IReadOnlyDictionary<string, int>? requiredAtEnd = null)
    {
        var net = new Dictionary<string, int>(StringComparer.Ordinal);
        var peak = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var op = step.Operation!;
            if (op.Type == "POUR_IN")
                Add(net, op.TargetLiquid!, 1);
            else if (op.Type == "RESHAPE" && !op.FullReshape)
                Add(net, op.CostLiquid!, 1);
            else if (op.Type == "POUR_OUT")
                Add(net, step.Before![op.JarIndex!.Value].Liquid!, -1);

            foreach (var item in net)
                peak[item.Key] = Math.Max(peak.GetValueOrDefault(item.Key), item.Value);
        }
        if (requiredAtEnd != null)
            foreach (var item in requiredAtEnd)
                peak[item.Key] = Math.Max(peak.GetValueOrDefault(item.Key),
                    net.GetValueOrDefault(item.Key) + item.Value);
        foreach (var key in peak.Keys.ToArray())
            peak[key] = Math.Max(0, peak[key]);
        return peak;
    }

    private static void PopulateAfterLiquidSnapshots(
        IReadOnlyList<SolverStep> steps, IReadOnlyDictionary<string, int> initial)
    {
        var balances = initial.Where(x => x.Value > 0)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var op = step.Operation!;
            if (op.Type == "POUR_IN")
                Add(balances, op.TargetLiquid!, -1);
            else if (op.Type == "RESHAPE" && !op.FullReshape)
                Add(balances, op.CostLiquid!, -1);
            else if (op.Type == "POUR_OUT")
                Add(balances, step.Before![op.JarIndex!.Value].Liquid!, 1);
            step.AfterLiquids = balances.Where(x => x.Value > 0)
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        }
    }

    private static void Add(Dictionary<string, int> balances, string liquid, int amount) =>
        balances[liquid] = checked(balances.GetValueOrDefault(liquid) + amount);

    private static WorldOrderConcreteMaterializationResult Failure(
        WorldOrderConcreteMaterializationStatus status, string reason) =>
        new(status, null, null, null, null, reason);
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using JarSystem.Core.World;

namespace JarSystem.Core.World.Solving;

public enum WorldOrderPhysicalJarBindingStatus
{
    Complete,
    Infeasible,
    Undetermined
}

/// <summary>
/// Binds symbolic Jar keys to distinct physical Jar IDs from the initial Master snapshot.
/// This is identity allocation only: it does not prove liquid sufficiency or materialize
/// SolverSteps. A successful result is therefore necessary but not sufficient for a plan.
/// </summary>
public sealed class WorldOrderPhysicalJarBindingResult
{
    public WorldOrderPhysicalJarBindingStatus Status { get; }
    public IReadOnlyDictionary<string, int> JarIds { get; }
    public string? Reason { get; }
    public bool IsComplete => Status == WorldOrderPhysicalJarBindingStatus.Complete;

    internal WorldOrderPhysicalJarBindingResult(
        WorldOrderPhysicalJarBindingStatus status,
        IDictionary<string, int>? jarIds,
        string? reason)
    {
        Status = status;
        JarIds = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(jarIds ?? new Dictionary<string, int>(), StringComparer.Ordinal));
        Reason = reason;
    }
}

/// <summary>
/// Allocates one initial physical Master Jar to each distinct symbolic Jar identity.
/// The symbolic program already enforces one rule operation per JarKey and the single
/// Master-to-Slave handoff. Handoff revisions never create a new physical identity.
/// </summary>
public static class WorldOrderPhysicalJarBinder
{
    public static WorldOrderPhysicalJarBindingResult Bind(
        WorldOrderSymbolicOperationProgram program,
        WorkshopSolveSnapshot masterSnapshot)
    {
        if (program == null) throw new ArgumentNullException(nameof(program));
        if (masterSnapshot == null) throw new ArgumentNullException(nameof(masterSnapshot));

        var allVersions = program.Nodes
            .SelectMany(node => node.InputJars.Concat(node.OutputJars))
            .GroupBy(version => version.JarKey, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                JarKey = group.Key,
                Root = group.OrderBy(version => version.Revision).First()
            })
            .ToArray();

        if (allVersions.Length == 0)
            return Result(WorldOrderPhysicalJarBindingStatus.Complete,
                new Dictionary<string, int>(StringComparer.Ordinal), null);

        if (allVersions.Any(item => item.Root.Revision != 0 ||
                                    item.Root.WorkshopRole != program.MasterRole ||
                                    item.Root.IsMarked))
            return Result(WorldOrderPhysicalJarBindingStatus.Undetermined,
                null, "符号 Jar 缺少未标记的主台初始状态；不能把交接或 revision 当成新 Jar");

        var physical = masterSnapshot.Jars.ToArray();
        if (physical.Any(jar => jar.Id is not > 0))
            return Result(WorldOrderPhysicalJarBindingStatus.Undetermined,
                null, "主台快照包含没有有效物理 ID 的 Jar，无法建立稳定身份映射");

        if (physical.Select(jar => jar.Id!.Value).Distinct().Count() != physical.Length)
            return Result(WorldOrderPhysicalJarBindingStatus.Undetermined,
                null, "主台快照包含重复物理 Jar ID");

        // Candidates are exact state matches. Grouping by state avoids greedy allocation
        // pitfalls: within one state class every symbolic key has exactly the same choices.
        var assignments = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stateGroup in allVersions.GroupBy(
                     item => (item.Root.Shape, item.Root.Liquid)))
        {
            var symbolic = stateGroup.OrderBy(item => item.JarKey, StringComparer.Ordinal).ToArray();
            var available = physical
                .Where(jar => !jar.IsMarked &&
                              jar.Shape == stateGroup.Key.Shape &&
                              jar.Liquid == stateGroup.Key.Liquid)
                .OrderBy(jar => jar.Id)
                .ToArray();

            if (available.Length < symbolic.Length)
                return Result(WorldOrderPhysicalJarBindingStatus.Infeasible,
                    null,
                    "物理 Jar 不足或初态不匹配：需要 " + symbolic.Length +
                    " 只未标记的 " + stateGroup.Key.Shape +
                    (stateGroup.Key.Liquid == null ? " 空 Jar" : "（内部液体：" + stateGroup.Key.Liquid + "）") +
                    "，实际只有 " + available.Length + " 只");

            for (var index = 0; index < symbolic.Length; index++)
                assignments.Add(symbolic[index].JarKey, available[index].Id!.Value);
        }

        if (assignments.Values.Distinct().Count() != assignments.Count)
            return Result(WorldOrderPhysicalJarBindingStatus.Undetermined,
                null, "物理 Jar 分配出现身份复用，拒绝生成不可靠绑定");

        return Result(WorldOrderPhysicalJarBindingStatus.Complete, assignments, null);
    }

    private static WorldOrderPhysicalJarBindingResult Result(
        WorldOrderPhysicalJarBindingStatus status,
        IDictionary<string, int>? jarIds,
        string? reason) => new(status, jarIds, reason);
}

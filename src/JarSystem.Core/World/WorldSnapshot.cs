using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;

namespace JarSystem.Core.World;

/// <summary>
/// 一次求解所需的世界输入快照。
///
/// Version 是求解输入内容的 SHA-256 指纹，而不是世界时钟或命令序号：
/// 只有会改变静态求解输入的内容才改变 Version；世界恢复到完全相同的求解输入时，
/// 会得到相同 Version。快照不持有可变 Workshop/Jar 实体。
/// </summary>
public sealed class WorldSnapshot
{
    public string Version { get; }
    public WorkshopSolveSnapshot? Master { get; }
    public WorkshopSolveSnapshot? Slave { get; }

    internal WorldSnapshot(
        string version,
        WorkshopSolveSnapshot? master,
        WorkshopSolveSnapshot? slave)
    {
        Version = version;
        Master = master;
        Slave = slave;
    }
}

/// <summary>单个岗位的纯值求解输入，不持有 Workshop 或 Jar 实体引用。</summary>
public sealed class WorkshopSolveSnapshot
{
    private readonly IReadOnlyList<WorldJarState> _jars;
    private readonly IReadOnlyDictionary<string, int> _liquids;
    private readonly IReadOnlyList<WorldRuleModifierSnapshot> _modifiers;

    public string Name { get; }
    public int MaxJars { get; }
    public int MaxLiquids { get; }
    public WorkshopCapability Capability { get; }
    public IReadOnlyList<WorldJarState> Jars => _jars;
    public IReadOnlyDictionary<string, int> Liquids => _liquids;
    public IReadOnlyList<WorldRuleModifierSnapshot> SolverModifiers => _modifiers;

    /// <summary>
    /// true 表示存在求解器不能静态投影的规则（例如带任意委托的 Require）。
    /// 后续规格求解适配器必须将该订单报告为未判定，而不能忽略该规则后误报可行。
    /// </summary>
    public bool HasOpaqueSolverRules { get; }

    internal WorkshopSolveSnapshot(Workshop workshop)
    {
        Name = workshop.Name;
        MaxJars = workshop.MaxJars;
        MaxLiquids = workshop.MaxLiquids;
        Capability = workshop.Capability;

        _jars = Array.AsReadOnly(workshop.Jars
            .Select(JarStateMapper.From)
            .Select(j => new WorldJarState(j.Id, j.Shape, j.Liquid, j.IsMarked))
            .ToArray());

        _liquids = new ReadOnlyDictionary<string, int>(
            workshop.LiquidStorage.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

        var modifiers = new List<WorldRuleModifierSnapshot>();
        foreach (var modifier in workshop.Rules.Stack.CaptureEffectiveModifiers())
        {
            // 改拍与强度影响执行过程/生产，不改变当前静态求解输入。
            if (modifier is RetimeModifier or IntensityModifier)
                continue;

            switch (modifier)
            {
                case SuppressModifier suppress:
                    modifiers.Add(WorldRuleModifierSnapshot.Suppress(suppress));
                    break;
                case GrantModifier grant:
                    modifiers.Add(WorldRuleModifierSnapshot.Grant(grant));
                    break;
                case RequireModifier require:
                    // 不复制委托：它可能闭包捕获可变世界状态，无法成为纯值快照。
                    // 仅记录其存在与描述，后续静态求解必须保守返回未判定。
                    modifiers.Add(WorldRuleModifierSnapshot.Opaque(require));
                    break;
                default:
                    modifiers.Add(WorldRuleModifierSnapshot.Opaque(modifier));
                    break;
            }
        }

        _modifiers = Array.AsReadOnly(modifiers.ToArray());
        HasOpaqueSolverRules = _modifiers.Any(x => x.IsOpaque);
    }

    /// <summary>每次返回独立的可变求解器输入，防止求解器修改共享快照。</summary>
    public List<JarState> CreateSolverJars() => _jars.Select(x => x.ToSolverState()).ToList();

    /// <summary>每次返回独立字典，防止求解器修改共享快照。</summary>
    public Dictionary<string, int> CreateSolverLiquids() =>
        _liquids.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    /// <summary>创建独立规则输入；Capability 本身不可变，可以安全共享。</summary>
    public WorkshopInfo CreateWorkshopInfo() => new()
    {
        Capability = Capability,
        MaxJars = MaxJars,
        MaxLiquids = MaxLiquids
    };
}

/// <summary>罐子的不可变求解投影，不包含真实实体身份或对象池引用。</summary>
public readonly record struct WorldJarState(
    int? Id,
    string Shape,
    string? Liquid,
    bool IsMarked)
{
    public JarState ToSolverState() => new()
    {
        Id = Id,
        Shape = Shape,
        Liquid = Liquid,
        IsMarked = IsMarked,
        MasterOrigin = false
    };
}

/// <summary>静态求解相关修饰器的纯值描述。</summary>
public sealed record WorldRuleModifierSnapshot(
    string RuntimeType,
    ModifierKind Kind,
    string Name,
    string Source,
    int Priority,
    bool HasWindow,
    int WindowFrom,
    int? WindowTo,
    string? Operation,
    string? Shape,
    string? Liquid,
    string? Reason,
    string? Text,
    bool IsOpaque)
{
    internal static WorldRuleModifierSnapshot Suppress(SuppressModifier value)
    {
        // 旧 ModifierAwareSolver 只能精确投影「整种液体被禁」或「整个形状被禁」。
        // 操作级、形状+液体组合等规则不能简化成上述任一条件，否则会多禁资源。
        var noTarget = string.IsNullOrEmpty(value.Operation)
            && string.IsNullOrEmpty(value.Shape)
            && string.IsNullOrEmpty(value.Liquid);
        var wholeLiquid = string.IsNullOrEmpty(value.Operation)
            && string.IsNullOrEmpty(value.Shape)
            && !string.IsNullOrEmpty(value.Liquid);
        var wholeShape = string.IsNullOrEmpty(value.Operation)
            && !string.IsNullOrEmpty(value.Shape)
            && string.IsNullOrEmpty(value.Liquid);

        return new WorldRuleModifierSnapshot(
            value.GetType().FullName ?? value.GetType().Name,
            value.Kind, value.Name, value.Source, value.Priority,
            value.Window.HasValue, value.Window?.From ?? 0, value.Window?.To,
            value.Operation, value.Shape, value.Liquid, value.Reason, null,
            !(noTarget || wholeLiquid || wholeShape));
    }

    internal static WorldRuleModifierSnapshot Grant(GrantModifier value) => new(
        value.GetType().FullName ?? value.GetType().Name,
        value.Kind, value.Name, value.Source, value.Priority,
        value.Window.HasValue, value.Window?.From ?? 0, value.Window?.To,
        null, value.Shape, null, null, null, false);

    internal static WorldRuleModifierSnapshot Opaque(IRuleModifier value) => new(
        value.GetType().FullName ?? value.GetType().Name,
        value.Kind, value.Name, value.Source, value.Priority,
        value.Window.HasValue, value.Window?.From ?? 0, value.Window?.To,
        null, null, null, null,
        value is RequireModifier require ? require.Text : null,
        true);
}

/// <summary>从当前主从岗位捕获同一时刻的求解输入，并生成内容指纹版本。</summary>
public static class WorldSnapshotFactory
{
    public static WorldSnapshot Capture(WorkshopRoster roster)
    {
        if (roster == null) throw new ArgumentNullException(nameof(roster));

        var master = roster.Master == null ? null : new WorkshopSolveSnapshot(roster.Master);
        var slave = roster.Slave == null ? null : new WorkshopSolveSnapshot(roster.Slave);
        var version = ComputeVersion(master, slave);
        return new WorldSnapshot(version, master, slave);
    }

    private static string ComputeVersion(
        WorkshopSolveSnapshot? master,
        WorkshopSolveSnapshot? slave)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("JarSystem.WorldSolveSnapshot.v1");
            WriteWorkshop(writer, master);
            WriteWorkshop(writer, slave);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteWorkshop(BinaryWriter writer, WorkshopSolveSnapshot? workshop)
    {
        writer.Write(workshop != null);
        if (workshop == null) return;

        writer.Write(workshop.Name);
        writer.Write(workshop.MaxJars);
        writer.Write(workshop.MaxLiquids);

        WriteStrings(writer, workshop.Capability.BaseMarks);
        WriteStrings(writer, workshop.Capability.AdditionalMarks);
        WriteStrings(writer, workshop.Capability.Ban);

        writer.Write(workshop.Jars.Count);
        foreach (var jar in workshop.Jars)
        {
            writer.Write(jar.Id.HasValue);
            if (jar.Id.HasValue) writer.Write(jar.Id.Value);
            writer.Write(jar.Shape);
            WriteNullableString(writer, jar.Liquid);
            writer.Write(jar.IsMarked);
        }

        var liquids = workshop.Liquids.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        writer.Write(liquids.Length);
        foreach (var liquid in liquids)
        {
            writer.Write(liquid.Key);
            writer.Write(liquid.Value);
        }

        writer.Write(workshop.SolverModifiers.Count);
        foreach (var modifier in workshop.SolverModifiers)
        {
            writer.Write(modifier.RuntimeType);
            writer.Write((int)modifier.Kind);
            writer.Write(modifier.Name);
            writer.Write(modifier.Source);
            writer.Write(modifier.Priority);
            writer.Write(modifier.HasWindow);
            writer.Write(modifier.WindowFrom);
            writer.Write(modifier.WindowTo.HasValue);
            if (modifier.WindowTo.HasValue) writer.Write(modifier.WindowTo.Value);
            WriteNullableString(writer, modifier.Operation);
            WriteNullableString(writer, modifier.Shape);
            WriteNullableString(writer, modifier.Liquid);
            WriteNullableString(writer, modifier.Reason);
            WriteNullableString(writer, modifier.Text);
            writer.Write(modifier.IsOpaque);
        }
    }

    private static void WriteStrings(BinaryWriter writer, IReadOnlyList<string> values)
    {
        writer.Write(values.Count);
        foreach (var value in values) writer.Write(value);
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value != null) writer.Write(value);
    }
}

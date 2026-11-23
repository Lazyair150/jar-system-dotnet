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
    /// <summary>规则语义版本与当前有效声明式修饰器的独立指纹。</summary>
    public string RuleSetVersion { get; }
    /// <summary>主从岗位工作台属性与容量配置的独立指纹。</summary>
    public string WorkshopProfileVersion { get; }
    /// <summary>Jar、游离液体、自机液库与预置液资源事实的独立指纹。</summary>
    public string ResourceVersion { get; }
    public WorkshopSolveSnapshot? Master { get; }
    public WorkshopSolveSnapshot? Slave { get; }
    /// <summary>新液体资源路径的自机液库纯值快照；为空表示旧版兼容求解输入。</summary>
    public ShipLiquidInventorySnapshot? ShipLiquidInventory { get; }
    public IReadOnlyDictionary<string, int> MasterPrefillLiquidBalances { get; }

    internal WorldSnapshot(
        string version,
        string ruleSetVersion,
        string workshopProfileVersion,
        string resourceVersion,
        WorkshopSolveSnapshot? master,
        WorkshopSolveSnapshot? slave,
        ShipLiquidInventorySnapshot? shipLiquidInventory,
        IReadOnlyDictionary<string, int>? masterPrefillLiquidBalances = null)
    {
        Version = version;
        RuleSetVersion = ruleSetVersion;
        WorkshopProfileVersion = workshopProfileVersion;
        ResourceVersion = resourceVersion;
        Master = master;
        Slave = slave;
        ShipLiquidInventory = shipLiquidInventory;
        MasterPrefillLiquidBalances = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(masterPrefillLiquidBalances ?? new Dictionary<string, int>(), StringComparer.Ordinal));
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
    /// <summary>新订单液体暂存模型的合计容量，与旧版单液种 MaxLiquids 分开捕获。</summary>
    public int LiquidStagingCapacity { get; }
    public WorkshopCapability Capability { get; }
    public IReadOnlyList<WorldJarState> Jars => _jars;
    public IReadOnlyDictionary<string, int> Liquids => _liquids;
    public IReadOnlyList<WorldRuleModifierSnapshot> SolverModifiers => _modifiers;
    /// <summary>Frozen declarative profile consumed by the solver's RuleContext adapter.</summary>
    public EffectiveRuleProfile EffectiveRuleProfile { get; }

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
        LiquidStagingCapacity = workshop.LiquidStagingCapacity;
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
        var source = new RuleContributionSource(
            "legacy-workshop-runtime", "legacy.workshop", 1, workshop.Name, 1, workshop.Name);
        EffectiveRuleProfile = WorkshopRuleContributionAdapter.Build(
            "legacy.runtime", 1, 1, source, Capability, workshop.Rules.Stack, includeExecutionModifiers: false);
    }

    /// <summary>每次返回独立的可变求解器输入，防止求解器修改共享快照。</summary>
    public List<JarState> CreateSolverJars() => _jars.Select(x => x.ToSolverState()).ToList();

    /// <summary>每次返回独立字典，防止求解器修改共享快照。</summary>
    public Dictionary<string, int> CreateSolverLiquids() =>
        _liquids.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    /// <summary>
    /// 从捕获时已经生效的声明式修饰器重建只读规则上下文。
    /// 任意委托或未知扩展不允许被静默丢弃；调用方必须先检查 HasOpaqueSolverRules。
    /// </summary>
    public RuleContext CreateRuleContext()
    {
        if (HasOpaqueSolverRules)
            throw new InvalidOperationException("工作台快照包含不可静态重建的规则");

        return RuleContext.From(EffectiveRuleProfile);

    }

    /// <summary>创建包含能力属性与冻结规则上下文的求解输入。</summary>
    public WorkshopInfo CreateWorkshopInfo() => new()
    {
        Capability = Capability,
        Rules = RuleContext.From(EffectiveRuleProfile),
        MaxJars = MaxJars,
        MaxLiquids = MaxLiquids,
        MaxLiquidCapacity = LiquidStagingCapacity
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
    internal static WorldRuleModifierSnapshot Suppress(SuppressModifier value) => new(
        value.GetType().FullName ?? value.GetType().Name,
        value.Kind, value.Name, value.Source, value.Priority,
        value.Window.HasValue, value.Window?.From ?? 0, value.Window?.To,
        value.Operation, value.Shape, value.Liquid, value.Reason, null,
        false);

    internal static WorldRuleModifierSnapshot Grant(GrantModifier value) => new(
        value.GetType().FullName ?? value.GetType().Name,
        value.Kind, value.Name, value.Source, value.Priority,
        value.Window.HasValue, value.Window?.From ?? 0, value.Window?.To,
        null, value.Shape, null, null, null, string.IsNullOrEmpty(value.Shape));

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
    public static WorldSnapshot Capture(
        WorkshopRoster roster, ShipLiquidInventory? shipLiquidInventory = null,
        IReadOnlyDictionary<string, int>? masterPrefillLiquidBalances = null)
    {
        if (roster == null) throw new ArgumentNullException(nameof(roster));

        var masterWorkshop = roster.Master;
        var slaveWorkshop = roster.Slave;
        var master = masterWorkshop == null ? null : new WorkshopSolveSnapshot(masterWorkshop);
        var slave = slaveWorkshop == null ? null : new WorkshopSolveSnapshot(slaveWorkshop);
        var ship = shipLiquidInventory?.Capture();
        if (masterPrefillLiquidBalances != null)
            ShipLiquidInventory.ValidateAmounts(masterPrefillLiquidBalances);
        var prefill = masterPrefillLiquidBalances ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var fingerprints = ComputeFingerprints(master, slave, ship, prefill);
        return new WorldSnapshot(
            fingerprints.Version,
            fingerprints.RuleSetVersion,
            fingerprints.WorkshopProfileVersion,
            fingerprints.ResourceVersion,
            master, slave, ship, prefill);
    }

    private readonly record struct SnapshotFingerprints(
        string Version,
        string RuleSetVersion,
        string WorkshopProfileVersion,
        string ResourceVersion);

    private static SnapshotFingerprints ComputeFingerprints(
        WorkshopSolveSnapshot? master,
        WorkshopSolveSnapshot? slave,
        ShipLiquidInventorySnapshot? ship,
        IReadOnlyDictionary<string, int> masterPrefill)
    {
        var ruleSet = Fingerprint("JarSystem.RuleSet.v1", writer =>
        {
            writer.Write(WorkshopRulebook.RuleContractVersion);
            writer.Write(WorkshopRulebook.Definitions.Count);
            foreach (var definition in WorkshopRulebook.Definitions)
            {
                writer.Write(definition.RuleId);
                writer.Write(definition.RuleVersion);
                writer.Write(definition.Operator);
                writer.Write((int)definition.Category);
            }
            WriteWorkshopRules(writer, master);
            WriteWorkshopRules(writer, slave);
        });

        var workshopProfile = Fingerprint("JarSystem.WorkshopProfile.v1", writer =>
        {
            WriteWorkshopProfile(writer, master);
            WriteWorkshopProfile(writer, slave);
        });

        var resources = Fingerprint("JarSystem.ResourceState.v1", writer =>
        {
            WriteWorkshopResources(writer, master);
            WriteWorkshopResources(writer, slave);
            WriteShipLiquidInventory(writer, ship);
            var prefill = masterPrefill.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            writer.Write(prefill.Length);
            foreach (var item in prefill)
            {
                writer.Write(item.Key);
                writer.Write(item.Value);
            }
        });

        var combined = Fingerprint("JarSystem.WorldSolveSnapshot.v4", writer =>
        {
            writer.Write(ruleSet);
            writer.Write(workshopProfile);
            writer.Write(resources);
        });
        return new SnapshotFingerprints(combined, ruleSet, workshopProfile, resources);
    }

    private static string Fingerprint(string domain, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(domain);
            write(writer);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteWorkshopProfile(BinaryWriter writer, WorkshopSolveSnapshot? workshop)
    {
        writer.Write(workshop != null);
        if (workshop == null) return;

        writer.Write(workshop.Name);
        writer.Write(workshop.MaxJars);
        writer.Write(workshop.MaxLiquids);
        writer.Write(workshop.LiquidStagingCapacity);
        WriteStrings(writer, workshop.Capability.BaseMarks);
        WriteStrings(writer, workshop.Capability.AdditionalMarks);
        WriteStrings(writer, workshop.Capability.Ban);
    }

    private static void WriteWorkshopResources(BinaryWriter writer, WorkshopSolveSnapshot? workshop)
    {
        writer.Write(workshop != null);
        if (workshop == null) return;

        writer.Write(workshop.Jars.Count);
        foreach (var jar in workshop.Jars)
        {
            writer.Write(jar.Id.HasValue);
            if (jar.Id.HasValue) writer.Write(jar.Id.Value);
            writer.Write(jar.Shape);
            WriteNullableString(writer, jar.Liquid);
            writer.Write(jar.IsMarked);
        }

        // 保留求解器输入字典的枚举顺序；若未来规范化排序，必须同步修改输入创建口径。
        var liquids = workshop.Liquids.ToArray();
        writer.Write(liquids.Length);
        foreach (var liquid in liquids)
        {
            writer.Write(liquid.Key);
            writer.Write(liquid.Value);
        }
    }

    private static void WriteWorkshopRules(BinaryWriter writer, WorkshopSolveSnapshot? workshop)
    {
        writer.Write(workshop != null);
        if (workshop == null) return;

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

    private static void WriteShipLiquidInventory(
        BinaryWriter writer, ShipLiquidInventorySnapshot? ship)
    {
        writer.Write(ship != null);
        if (ship == null) return;

        writer.Write(ship.Capacity);
        var balances = ship.Balances.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        writer.Write(balances.Length);
        foreach (var item in balances)
        {
            writer.Write(item.Key);
            writer.Write(item.Value);
        }
    }

    private static void WriteWorkshop(BinaryWriter writer, WorkshopSolveSnapshot? workshop)
    {
        writer.Write(workshop != null);
        if (workshop == null) return;

        writer.Write(workshop.Name);
        writer.Write(workshop.MaxJars);
        writer.Write(workshop.MaxLiquids);
        writer.Write(workshop.LiquidStagingCapacity);

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

        // 保留实际交给求解器的字典枚举顺序；若未来将求解输入规范化排序，
        // 必须同时修改 CreateSolverLiquids 与此处的指纹口径。
        var liquids = workshop.Liquids.ToArray();
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

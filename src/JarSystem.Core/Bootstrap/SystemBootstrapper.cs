using JarSystem.Core.Rules;
using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;

namespace JarSystem.Core.Bootstrap;

/// <summary>
/// 世界装配：把 <see cref="WorldOptions"/>（**数据**）装配成真实世界（生产者 + 初始库存 + 工作台）。
///
/// S3 之前这里硬编码了 4 个生产者与 6 个工作台；现在它们全部来自配置，
/// 而 <see cref="WorldOptions.Default"/> 逐值等于那套硬编码 ⇒ **不读配置时行为与重构前一致**。
///
/// 装配顺序 = 配置里的列表顺序，这一点是契约的一部分：
/// 生产者顺序决定 `producerStatus.producers` 的键序，工作台顺序决定主从下标与 `workshops` 的顺序。
/// </summary>
public static class SystemBootstrapper
{
    /// <summary>按配置装配（不清理已有内容，与重构前一致）。</summary>
    /// <param name="world">世界配置（内容 + 参数）。</param>
    /// <param name="vocabulary">词表：决定液体账目的键与顺序，以及本次装配出的罐子/工作台的形状校验。</param>
    /// <param name="jarIds">
    /// 罐子 ID 源（**回放/多世界必须注入独立实例**）：装配出的每个工作台都用它给罐子编号，
    /// 否则进程级共享计数器会让"重建出的世界"与原次的 ID 不同（见 Sessions.WorldCommand 的边界说明）。
    /// </param>
    public static void Apply(WorldOptions world, Vocabulary vocabulary, ProducerModule producers,
        List<Workshop> workshops, IIdGenerator? jarIds = null, ModifierStack? modifiers = null)
    {
        foreach (var p in world.Producers)
            producers.RegisterProducer(p.Name, new Dictionary<string, double>(p.Rates));

        foreach (var kv in world.InitialStock)
            producers.Warehouse.ProduceLiquid(kv.Key, kv.Value);

        foreach (var w in world.Workshops)
            workshops.Add(new Workshop(
                w.Name,
                new List<string>(w.BaseMarks),
                new List<string>(w.AdditionalMarks),
                new List<string>(w.Ban),
                w.MaxJars ?? world.DefaultMaxJars,
                w.MaxLiquids ?? world.DefaultMaxLiquids,
                jarIds,
                vocabulary: vocabulary,
                modifiers: modifiers));
    }
}

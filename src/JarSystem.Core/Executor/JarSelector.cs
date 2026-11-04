using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Executor;

/// <summary>罐子选择策略的枚举（对外 API；<see cref="OrderExecutor.SelectionStrategy"/> 使用它）。</summary>
public enum JarSelectionStrategy { FIFO, LIFO, RareShapeFirst, Random }

/// <summary>
/// 罐子选择器：对枚举 → <see cref="IJarSelectionStrategy"/> 实现做映射（策略注册表），
/// 并把「按谓词过滤候选」这一步保持在唯一位置。
///
/// 原实现是枚举 + 内联 <c>switch</c>；现由策略对象承载挑选逻辑，新增策略不必改动本类以外的代码。
///
/// **T4**：注册表由 <c>static</c> 改为实例字段，并把随机源注入 <see cref="RandomJarSelection"/>
/// （原先每次选择都新建 <c>Random</c>，随机策略不可复现）。
/// </summary>
internal sealed class JarSelector
{
    private readonly Dictionary<JarSelectionStrategy, IJarSelectionStrategy> _strategies = new();
    private readonly Random? _rng;

    /// <summary>枚举值越界时的兜底策略（等价于原 switch 的 default 分支）。</summary>
    private static readonly IJarSelectionStrategy Fallback = new FirstMatchJarSelection();

    /// <param name="rng">随机源；null 时新建一个（保持"无种子"的默认行为）。</param>
    public JarSelector(Random? rng = null)
    {
        _rng = rng;
    }

    public JarSelectionStrategy Strategy { get; set; } = JarSelectionStrategy.FIFO;

    /// <summary>按当前策略从满足谓词的罐子中选一个（无候选返回 null）。</summary>
    public Jar? Select(IReadOnlyList<Jar> jars, Func<Jar, bool> predicate)
    {
        var candidates = jars.Where(predicate).ToList();
        if (candidates.Count == 0) return null;
        var strategy = GetStrategy(Strategy);
        return strategy.Select(jars, candidates);
    }

    /// <summary>策略对象按需创建：默认只用 FIFO 时，不额外实例化另外三个策略。</summary>
    private IJarSelectionStrategy GetStrategy(JarSelectionStrategy strategy)
    {
        if (_strategies.TryGetValue(strategy, out var existing)) return existing;

        IJarSelectionStrategy created = strategy switch
        {
            JarSelectionStrategy.FIFO => new FifoJarSelection(),
            JarSelectionStrategy.LIFO => new LifoJarSelection(),
            JarSelectionStrategy.RareShapeFirst => new RareShapeFirstJarSelection(),
            JarSelectionStrategy.Random => new RandomJarSelection(_rng ?? new Random()),
            _ => Fallback
        };
        _strategies[strategy] = created;
        return created;
    }
}

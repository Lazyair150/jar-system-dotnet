using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Executor;

/// <summary>
/// 四种罐子选择策略的实现（阶段 3：Strategy 模式）。
/// 每个实现都**逐行对应**原 <c>JarSelector</c> switch 里的一个分支，选择顺序与判据完全一致。
/// </summary>
internal sealed class FifoJarSelection : IJarSelectionStrategy
{
    public string Name => "FIFO";

    /// <summary>按 ID 升序取第一个（先入先出；ID 缺失视作最大值，排在最后）。</summary>
    public Jar? Select(IReadOnlyList<Jar> jars, IReadOnlyList<Jar> candidates) =>
        candidates.OrderBy(j => j.Id ?? int.MaxValue).First();
}

internal sealed class LifoJarSelection : IJarSelectionStrategy
{
    public string Name => "LIFO";

    /// <summary>按 ID 降序取第一个（后入先出；ID 缺失视作 0，排在最后）。</summary>
    public Jar? Select(IReadOnlyList<Jar> jars, IReadOnlyList<Jar> candidates) =>
        candidates.OrderByDescending(j => j.Id ?? 0).First();
}

internal sealed class RareShapeFirstJarSelection : IJarSelectionStrategy
{
    public string Name => "RareShapeFirst";

    /// <summary>优先选「工作台中同形空罐最少」的候选，同分再按 ID 升序（FIFO）。</summary>
    public Jar? Select(IReadOnlyList<Jar> jars, IReadOnlyList<Jar> candidates) =>
        candidates.OrderBy(j => CountEmptyOfShape(jars, j.Shape)).ThenBy(j => j.Id ?? int.MaxValue).First();

    private static int CountEmptyOfShape(IReadOnlyList<Jar> jars, string shape) =>
        jars.Count(j => j.Shape == shape && j.IsEmpty());
}

internal sealed class RandomJarSelection : IJarSelectionStrategy
{
    private readonly Random _rng;

    /// <param name="rng">注入的随机源（由 <see cref="JarSelector"/> 统一提供）。</param>
    public RandomJarSelection(Random rng)
    {
        _rng = rng;
    }

    public string Name => "Random";

    /// <summary>
    /// 随机取一个候选。
    /// **T4（已批准的行为变更）**：随机源由"每次调用新建 <c>Random</c>"改为**注入式** ——
    /// 同一个种子给出同一串结果，随机策略从此可复现（代价：Random 策略的数值序列与历史记录不同）。
    /// </summary>
    public Jar? Select(IReadOnlyList<Jar> jars, IReadOnlyList<Jar> candidates) =>
        candidates[_rng.Next(candidates.Count)];
}

/// <summary>
/// 兜底策略：取候选中的第一个（对应原 switch 的 <c>default</c> 分支；仅在枚举值越界时使用）。
/// </summary>
internal sealed class FirstMatchJarSelection : IJarSelectionStrategy
{
    public string Name => "FirstMatch";

    public Jar? Select(IReadOnlyList<Jar> jars, IReadOnlyList<Jar> candidates) => candidates[0];
}

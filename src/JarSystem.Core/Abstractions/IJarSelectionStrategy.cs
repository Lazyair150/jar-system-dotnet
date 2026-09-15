using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Abstractions;

/// <summary>
/// 罐子选择策略（阶段 3：替换原先 <c>JarSelectionStrategy</c> 枚举 + <c>switch</c> 的写法）。
///
/// 契约：**只读**选择，不修改任何罐子状态；候选集合为空时返回 null（调用方负责空集合判断）。
/// 外部程序集可实现本接口以提供自定义策略（实现类构造后可经 <c>OrderExecutor</c> 的选择器接入）。
/// </summary>
public interface IJarSelectionStrategy
{
    /// <summary>策略名（日志与实验报告用）。</summary>
    string Name { get; }

    /// <summary>
    /// 从候选中选一个罐子。
    /// </summary>
    /// <param name="jars">工作台完整罐子列表（供需要全局分布信息的策略使用，如「稀有形状优先」）。</param>
    /// <param name="candidates">满足当前需求条件的候选罐子（调用方保证非空）。</param>
    Jar? Select(IReadOnlyList<Jar> jars, IReadOnlyList<Jar> candidates);
}

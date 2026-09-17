using System.Collections.Generic;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Abstractions;

/// <summary>
/// 罐子选择策略（阶段 3：替换原先 <c>JarSelectionStrategy</c> 枚举 + <c>switch</c> 的写法）。
///
/// 契约：**只读**选择，不修改任何罐子状态；**调用方保证候选非空**。
///
/// ⚠️ 原注释写的是"候选集合为空时返回 null（调用方负责空集合判断）"—— **这是假的，且与本文件下面
/// <c>candidates</c> 的参数说明自相矛盾**（2026-09-16 修）。真实行为：5 个内置实现都是
/// <c>.First()</c> / <c>[0]</c> / <c>[_rng.Next(Count)]</c> ⇒ 空集合会**抛异常**，没有一个返回 null。
/// 空判断确实由调用方做，但那在 <c>JarSelector.Select</c> 里（无候选时它自己返回 null，根本不调策略）。
///
/// 返回类型保留可空 <c>Jar?</c>：这是**公开扩展点**，收窄返回类型会破坏外部实现者的源码兼容；
/// 调用方按"不会为 null"处理即可（`JarSelector` 仍保留一次防御性判断）。
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

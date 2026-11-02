using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Solver;

/// <summary>
/// 求解器输入的**口径**（纯函数，无状态）。
///
/// 为什么需要它：<see cref="ISolver"/> 的 <c>liquids</c> 参数一直被当成"工作台液池里有多少"。
/// 2026-11 把禁令语义收窄为「只约束取用」之后，液池里可以存着**存得住但取不出**的液体：
/// 对求解器来说那几份不是资源，是死重量。若照样当供应，就会出现
/// "看着有 2 份液体所以可行"的误判 —— 这正是用户指出的
/// 「发生这种语义后，求解器需要重新认定什么是活性液体」。
///
/// 因此判定口径在这里统一：<b>求解器看到的液体 = 能取用的液体</b>。
/// 三个实现（<see cref="StateSpaceSolver"/> / <see cref="PrecheckedStateSpaceSolver"/> /
/// <see cref="MatchingSolver"/>）都在入口调用一次，于是"输入是什么"只有一处定义，
/// 而不是每个实现各自记得过滤。
///
/// 它**不做**的事：不改变禁令本身的判定（那是 <see cref="WorkshopCapability.IsBanned"/> 的事），
/// 也不猜容量（求解器本来就把容量当输入）。它只回答一个问题：这份库存，算不算资源。
/// </summary>
public static class SolverInput
{
    /// <summary>
    /// 把被禁液体的库存清零，其余原样保留。返回**新字典**（不改调用方的输入）。
    /// 键保留、值清零：那格子里确实有东西，只是取不出来。
    /// </summary>
    public static Dictionary<string, int> UsableLiquids(
        Dictionary<string, int> liquids, WorkshopCapability capability)
    {
        var result = new Dictionary<string, int>(liquids.Count);
        foreach (var kv in liquids)
            result[kv.Key] = capability.IsBanned(kv.Key) ? 0 : kv.Value;
        return result;
    }
}

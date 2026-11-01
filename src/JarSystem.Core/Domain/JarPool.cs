using System;
using System.Collections.Generic;
using JarSystem.Core.Config;

namespace JarSystem.Core.Domain;

/// <summary>
/// **罐子的对象池**（`Domain`）。
///
/// 为什么值得池化：罐子是这个世界里**唯一被持续产出又持续消失的实体** ——
/// 生产者每 tick 造罐子、仓库溢出丢罐子、换台装不下丢罐子。它同时也是唯一带身份
/// （`int? Id`）的领域对象，所以它既贵（每只都有 Id 分配与词表校验）又频繁生死。
///
/// **正确性边界（这是本类唯一的难点，也是它存在的理由）**：
/// 归还一只罐子，等于断言"世界上再没有第二处引用它"。这个断言一旦错，两处代码就会
/// 持有同一只罐子彼此覆盖 —— 而且**不会报错**。因此：
/// <list type="number">
/// <item>只有能证明"已经彻底离开世界"的位置才允许 `Return`（仓库溢出丢弃、换台容量不足丢弃）；
///       仍被引用的地方（交付记录 `OrderEvent._deliveredJars`、事件历史、快照）**一律不还**。</item>
/// <item>`Jar` 自带回收标记：归还后任何读写都抛异常，把这类错误从"静默串味"变成"当场炸"。</item>
/// <item>池按**词表引用**匹配：不同词表的罐子不复用，否则改形校验会用错词表。</item>
/// </list>
///
/// **确定性**：池只是复用内存，不改变任何可观察状态 —— `Rent` 出来的罐子与 `new Jar(...)`
/// 逐字段相同，且 Id 仍由工作台在入台时分配。因此同一条命令序列产出的世界状态与回放字节不变。
/// </summary>
public sealed class JarPool
{
    private readonly Stack<Jar> _idle = new();

    /// <summary>池中最多闲置多少只（防止长跑把内存全留住；超出部分直接交给 GC）。</summary>
    public int MaxIdle { get; }

    /// <summary>累计借出次数（诊断用，不参与逻辑）。</summary>
    public long Rented { get; private set; }

    /// <summary>累计成功归还次数（诊断用）。</summary>
    public long Returned { get; private set; }

    /// <summary>累计真正新建的对象数（诊断用；`Rented - Created` 就是省下的分配次数）。</summary>
    public long Created { get; private set; }

    /// <summary>当前闲置数量。</summary>
    public int Idle => _idle.Count;

    public JarPool(int maxIdle = 256)
    {
        if (maxIdle < 0)
            throw new ArgumentOutOfRangeException(nameof(maxIdle), "池容量不能为负");
        MaxIdle = maxIdle;
    }

    /// <summary>
    /// 取一只罐子（等价于 <c>new Jar(shape, liquid, id, vocabulary)</c>，优先复用池中闲置的）。
    /// </summary>
    public Jar Rent(string shape, string? liquid = null, int? id = null, Vocabulary? vocabulary = null)
    {
        var vocab = vocabulary ?? Vocabulary.Default;
        Rented++;
        while (_idle.Count > 0)
        {
            var candidate = _idle.Pop();
            if (candidate.TryRecycle(shape, liquid, id, vocab))
                return candidate;
            // 词表不符或形状不合法：这只不合适，丢掉它（它已在池外，直接交给 GC）
        }
        Created++;
        return new Jar(shape, liquid, id, vocab);
    }

    /// <summary>
    /// 归还一只罐子。返回 true 表示已进池可复用；false 表示**被拒绝**
    /// （已归还过、或池已满 —— 池满时对象直接交给 GC，不影响正确性）。
    /// </summary>
    public bool Return(Jar jar)
    {
        if (jar == null)
            throw new ArgumentNullException(nameof(jar));
        if (!jar.BeginRecycle())
            return false;                    // 重复归还：错误，但也说明有人两处都在还它
        Returned++;
        if (_idle.Count >= MaxIdle)
            return false;
        _idle.Push(jar);
        return true;
    }

    /// <summary>清空闲置队列（重置世界时用；已借出的罐子不受影响）。</summary>
    public void Clear() => _idle.Clear();

    /// <summary>一次性诊断快照，便于测试与报告直接对比。</summary>
    public (long Rented, long Returned, long Created, int Idle) Snapshot()
        => (Rented, Returned, Created, Idle);
}

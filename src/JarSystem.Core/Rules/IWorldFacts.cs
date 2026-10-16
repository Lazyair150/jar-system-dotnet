namespace JarSystem.Core.Rules;

/// <summary>
/// **世界事实的取值适配器**（P5）：把"查询要用的那几项事实"从世界里取出来的**唯一出口**。
///
/// 为什么是"事实"而不是"条件"：条件是**数据**（见 <see cref="WorldQuery"/>，受校验的词汇表），
/// 而取值是**宿主/内核的实现细节**。分开之后：
/// <list type="bullet">
/// <item>加一种新条件 = 扩展 <see cref="WorldQuery"/> 的词汇表（数据侧），不必改内核的"事实出口"；</item>
/// <item>实现方只需要如实报数（只读、不得改世界、不得抛异常 —— 取不到就报 0）。</item>
/// </list>
/// 实现方：门面 <c>JarSystem</c>（按工作台名/主从名与仓库、丢弃计数如实报数）。
/// </summary>
public interface IWorldFacts
{
    /// <summary>某工作台的罐子数；<paramref name="shape"/> 为 null 表示不限形状，<paramref name="empty"/> 为 null 表示不限空满。</summary>
    int WorkshopJars(string workshop, string? shape, bool? empty);

    /// <summary>某工作台的游离液体量。</summary>
    int WorkshopLiquid(string workshop, string liquid);

    /// <summary>中央仓库的罐子数。</summary>
    int WarehouseJars();

    /// <summary>中央仓库某种液体的库存。</summary>
    int WarehouseLiquid(string liquid);

    /// <summary>累计丢弃的罐子数（仓库存不下 / 工作台容量收缩 / 回滚溢出）。</summary>
    int DiscardedJars();

    /// <summary>累计丢弃的某种液体量。</summary>
    int DiscardedLiquid(string liquid);
}
